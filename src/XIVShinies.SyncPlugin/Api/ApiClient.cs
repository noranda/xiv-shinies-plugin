using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The HTTPS client for the XIV Shinies plugin API. Owns a single <see cref="HttpClient"/> for the
/// plugin's lifetime and is disposed with the plugin.
/// </summary>
/// <remarks>
/// <para>
/// Every method is asynchronous and must be awaited off the game's framework thread. Blocking on
/// one of these tasks (<c>.Result</c> or <c>.Wait()</c>) from the framework thread deadlocks the
/// game.
/// </para>
/// <para>
/// No exception ever escapes these methods: every failure — transport, timeout, unload, malformed
/// body — comes back as an <see cref="ApiStatus"/>. An exception escaping into the game's async
/// machinery could destabilize the client.
/// </para>
/// <para>
/// Nothing here fires on its own. The client is constructed at load and stays idle until something
/// explicitly calls it, which is what keeps the "no silent first-run upload" rule intact.
/// </para>
/// </remarks>
// `sealed` = nothing may inherit from it. `IDisposable` = it owns something (the HttpClient) that
// must be released; whoever creates it must call Dispose.
public sealed class ApiClient : IDisposable
{
    // Every endpoint hangs off this prefix.
    private const string ApiPrefix = "api/plugin/v1";

    // The largest response body we will read. Real responses are kilobytes; this ceiling exists
    // only to stop a hostile or broken backend (the URL is user-overridable) from streaming a
    // huge body that exhausts the game process's memory. 4 MiB is comfortably above any legitimate
    // config or sync response and well below anything dangerous.
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    // The longest server-requested backoff we will honor. Retry-After is server-controlled, and a
    // hostile 429/503 could otherwise silence syncing for the rest of the session (backoff
    // deliberately survives logout). Clamped to a day, matching the full-sync interval ceiling.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    // A single HttpClient reused for every request. Creating one per call exhausts sockets — this
    // is the single most common misuse of HttpClient in .NET.
    private readonly HttpClient http;

    // Cancelled when the plugin unloads. Every request runs under this, so a request still in
    // flight at unload is cancelled cleanly instead of failing against a disposed HttpClient.
    private readonly CancellationTokenSource lifetime = new();

    // Read live on every request rather than captured, so a token or backend edited in the
    // settings UI takes effect immediately without rebuilding the client.
    private readonly PluginSettings settings;

    /// <summary>Creates a client bound to the given settings.</summary>
    /// <param name="settings">Live settings; the token and base URL are re-read per request.</param>
    /// <param name="pluginVersion">Version string for the <c>User-Agent</c> header.</param>
    /// <param name="messageHandler">
    /// Optional transport override. Production passes null (a real network handler); tests pass a
    /// stub so the client's real header/URL/status code runs against a fake socket.
    /// </param>
    public ApiClient(PluginSettings settings, string pluginVersion, HttpMessageHandler? messageHandler = null)
    {
        this.settings = settings;

        if (messageHandler is null)
        {
            // Redirects are disabled deliberately. A client that validates exactly which host may
            // receive the token must not let the server hand that decision back by replying 3xx.
            // (.NET does strip the Authorization header across a redirect, so this is belt and
            // braces — but it also keeps us from silently following a 3xx to an unvalidated host.)
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            http = new HttpClient(handler, disposeHandler: true);
        }
        else
        {
            // disposeHandler: false — we did not create this handler, so we must not tear it down.
            http = new HttpClient(messageHandler, disposeHandler: false);
        }

        http.Timeout = TimeSpan.FromSeconds(30);

        // Identify ourselves on every request, as the contract requires. TryParseAdd rather than
        // ParseAdd: a malformed version string must never throw out of the plugin's constructor
        // and abort the load. Worst case we send no User-Agent.
        http.DefaultRequestHeaders.UserAgent.TryParseAdd(PluginMeta.UserAgent(pluginVersion));
    }

    /// <summary>Probes <c>GET /me</c> — which account this token belongs to and its characters.</summary>
    public Task<ApiResponse<MeResponse>> GetMeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<MeResponse>(HttpMethod.Get, "me", content: null, cancellationToken);

    /// <summary>Fetches <c>GET /config</c> — kill switches, cadence, and the item manifest.</summary>
    public Task<ApiResponse<ConfigResponse>> GetConfigAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ConfigResponse>(HttpMethod.Get, "config", content: null, cancellationToken);

    /// <summary>Uploads a collection snapshot via <c>POST /sync</c>.</summary>
    public async Task<ApiResponse<SyncResponse>> PostSyncAsync(
        SyncRequest request, CancellationToken cancellationToken = default)
    {
        StringContent content;

        // Serializing happens before the request is built, so it needs its own guard: an
        // exception thrown here would otherwise escape into the game rather than becoming a
        // status value like every other failure.
        try
        {
            var json = JsonSerializer.Serialize(request, ApiJson.Options);

            // StringContent with a known string sets Content-Length for us. The contract
            // *requires* that header — a chunked body without it is rejected with 413.
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // We built a payload we cannot even serialize; the server would reject it too.
            return new ApiResponse<SyncResponse> { Status = ApiStatus.InvalidPayload };
        }

        return await SendAsync<SyncResponse>(HttpMethod.Post, "sync", content, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels any request still in flight, then releases the underlying <see cref="HttpClient"/>.
    /// </summary>
    // Cancelling before disposing matters: tearing down the handler under a live request would
    // otherwise surface as an ObjectDisposedException inside the awaiting continuation.
    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        http.Dispose();
    }

    // The one code path every request funnels through. `async Task<T>` is the same idea as an
    // `async` function returning a Promise<T>; `await` suspends until the operation completes
    // without blocking the calling thread.
    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
        where T : class
    {
        // These guards run before the request is built, so any content we were handed has no owner
        // yet — dispose it here or it never gets released.

        // Refuse to send a malformed token: it can only earn an opaque 401, and the round trip
        // tells the user nothing a local check couldn't.
        if (!settings.HasUsableToken())
        {
            content?.Dispose();
            return new ApiResponse<T> { Status = ApiStatus.NotConfigured };
        }

        // Refuse to send the token anywhere we consider unsafe (plaintext to a remote host, or a
        // raw IP address).
        if (!BackendUrl.TryNormalize(settings.BaseUrl, out var baseUri, out _))
        {
            content?.Dispose();
            return new ApiResponse<T> { Status = ApiStatus.NotConfigured };
        }

        // Defense in depth: the token is sent to whatever host is configured, so a non-default
        // backend requires the user to have explicitly acknowledged that. Enforcing it here means
        // a bug in the settings UI can never silently ship the token to someone else's server.
        if (!BackendUrl.IsDefault(baseUri!) && !settings.CustomBackendAcknowledged)
        {
            content?.Dispose();
            return new ApiResponse<T> { Status = ApiStatus.NotConfigured };
        }

        try
        {
            // Build from the authority only, so a stray path in the user's base URL can't rewrite
            // the endpoint (e.g. "https://host/foo" must still hit "https://host/api/plugin/v1/me").
            var url = $"{baseUri!.GetLeftPart(UriPartial.Authority)}/{ApiPrefix}/{path}";

            // A request cancelled either by the caller OR by plugin unload. `using var` on a local
            // guarantees Dispose runs when the variable leaves scope — the same job as a `finally`
            // block, and the reason we never leak request/response objects.
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
            request.Content = content;

            // ConfigureAwait(false) says "resume on any thread; I don't need the original context".
            // HttpCompletionOption.ResponseHeadersRead returns as soon as the headers arrive,
            // before the body is buffered — so the Content-Length check below can reject an
            // oversized response before a single byte of its body is read into memory.
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            var rawCode = (int)response.StatusCode;
            var status = ApiStatusMap.FromHttpStatusCode(rawCode);

            // The backend URL is user-overridable, so a hostile or broken backend is in scope: a
            // multi-hundred-megabyte body read into a string would exhaust memory and take the
            // GAME process down, not just the plugin. Our real responses are kilobytes. Refuse
            // anything implausibly large — both the advertised length and, for a server that lies
            // about or omits it, the actual bytes read (see ReadBodyAsync). Reported as a network
            // error, the same as any other unusable response.
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = rawCode };

            var body = await ReadBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
            if (body is null)
                return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = rawCode };

            if (status == ApiStatus.Ok)
            {
                var value = Deserialize<T>(body);

                // A 200 whose body we cannot read is not a success.
                return value is null
                    ? new ApiResponse<T> { Status = ApiStatus.Unknown, HttpStatusCode = rawCode }
                    : new ApiResponse<T> { Status = ApiStatus.Ok, Value = value, HttpStatusCode = rawCode };
            }

            return new ApiResponse<T>
            {
                Status = status,
                Error = Deserialize<ErrorResponse>(body),
                RetryAfter = ReadRetryAfter(response),
                HttpStatusCode = rawCode,
            };
        }
        // A `when` filter narrows which exceptions this catch handles. Catch the BASE
        // OperationCanceledException, not just TaskCanceledException: an HttpClient timeout and a
        // cancelled body read are both documented to throw the base type. When the caller's own
        // token is not cancelled, the cause was our timeout or plugin unload — a network error.
        // A genuine caller cancellation fails the filter and propagates, as it should.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResponse<T> { Status = ApiStatus.NetworkError };
        }
        catch (HttpRequestException)
        {
            // DNS failure, refused connection, TLS problem — no HTTP status ever arrived.
            return new ApiResponse<T> { Status = ApiStatus.NetworkError };
        }
        catch (ObjectDisposedException)
        {
            // The plugin unloaded while this request was starting or in flight.
            return new ApiResponse<T> { Status = ApiStatus.NetworkError };
        }
    }

    /// <summary>
    /// Reads a response body as a string, but never more than <see cref="MaxResponseBytes"/> —
    /// returning null (an unusable response) if the server exceeds that, whether or not it
    /// advertised an honest <c>Content-Length</c>.
    /// </summary>
    /// <remarks>
    /// The Content-Length pre-check in the caller catches a server that tells the truth; this
    /// catches one that lies or omits the header and just streams. Reading through a
    /// length-capped stream means the bytes past the cap are never allocated.
    /// </remarks>
    private static async Task<string?> ReadBodyAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);

        // +1 so a body of exactly the cap reads whole, but the very first overflow byte trips it.
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total), token)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        if (total > MaxResponseBytes)
            return null;

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Reads how long the server asked us to wait, from its <c>Retry-After</c> header.
    /// </summary>
    /// <remarks>
    /// The header comes in two legal forms: a delta in whole seconds, or an absolute HTTP-date.
    /// Our server sends seconds, but a proxy or CDN in between may rewrite it to a date — so read
    /// both, or a 429/503 would silently lose its backoff. Never returns a negative wait: a date
    /// already in the past means "you may retry now". Clamped to <see cref="MaxRetryAfter"/>,
    /// because the header is server-controlled and an unbounded backoff would let a hostile
    /// backend mute syncing indefinitely — the same reason the config intervals are clamped.
    /// </remarks>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;

        // `is { } delta` is a pattern that means "is not null, and call it delta".
        if (header.Delta is { } delta)
            return Clamp(delta);

        if (header.Date is { } date)
            return Clamp(date - DateTimeOffset.UtcNow);

        return null;

        static TimeSpan Clamp(TimeSpan wait) =>
            wait < TimeSpan.Zero ? TimeSpan.Zero
            : wait > MaxRetryAfter ? MaxRetryAfter
            : wait;
    }

    // Never let a malformed body throw into the game; an unparseable response is just "no value".
    // NotSupportedException is caught alongside JsonException for the same reason the serialize
    // guard does: a type the serializer cannot handle must degrade, not crash.
    private static T? Deserialize<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, ApiJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
