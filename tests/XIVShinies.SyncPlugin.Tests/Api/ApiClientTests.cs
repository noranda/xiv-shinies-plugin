using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Exercises ApiClient against a fake HTTP transport. This is legitimate unit testing, not a faked
// game service: HttpMessageHandler is the documented seam for substituting the network, so the
// client's real header, URL, and status-handling code runs — only the socket is replaced.
public class ApiClientTests
{
    private const string ValidToken = "xvs_0123456789012345678901234567890123456789abc";

    // A stand-in for the network. It records what the client sent, and replies with whatever the
    // test dictates. `HttpMessageHandler` is the abstract base every HttpClient sends through.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public long? LastContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentLength = request.Content.Headers.ContentLength;
            }

            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (ApiClient Client, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string token = ValidToken,
        string baseUrl = "https://xiv-shinies.com",
        bool customBackendAcknowledged = false)
    {
        var handler = new StubHandler(respond);
        var settings = new PluginSettings
        {
            Token = token,
            BaseUrl = baseUrl,
            CustomBackendAcknowledged = customBackendAcknowledged,
        };
        return (new ApiClient(settings, "1.2.3", handler), handler);
    }

    [Fact]
    public async Task GetMe_sends_bearer_auth_and_user_agent_to_the_contract_url()
    {
        const string body = """
        {"characters": [], "user": {"id": "abc"}}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal("abc", response.Value!.User.Id);

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/me", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ValidToken, request.Headers.Authorization.Parameter);
        Assert.Equal("XIVShinies.SyncPlugin/1.2.3", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetConfig_parses_the_remote_config()
    {
        const string body = """
        {"categories": {"achievements": true, "items": true, "minions": true,
                        "mounts": true, "quests": true},
         "enabled": false,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [7851], "manifestVersion": "abc123"}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var response = await client.GetConfigAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.False(response.Value!.Enabled);
        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/config", handler.LastRequest!.RequestUri!.ToString());
    }

    // The contract REQUIRES a Content-Length header; a chunked body is rejected with 413.
    [Fact]
    public async Task PostSync_sends_json_with_a_content_length()
    {
        const string body = """
        {"ok": true, "bound": false,
         "written": {"achievements": 0, "minions": 0, "mounts": 0, "quests": 1}}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var request = new SyncRequest
        {
            CharacterContentIdHash = new string('a', 64),
            CharacterName = "Some Name",
            HomeWorld = "Excalibur",
            PluginVersion = "1.2.3",
            Trigger = SyncTrigger.Manual,
            Collections = new Dictionary<string, JsonNode>
            {
                ["quests"] = SyncFacts.Ids(new uint[] { 65575 }),
            },
        };

        var response = await client.PostSyncAsync(request);

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(1, response.Value!.Written.Quests);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/sync", handler.LastRequest.RequestUri!.ToString());
        Assert.NotNull(handler.LastContentLength);
        Assert.Contains("\"trigger\":\"manual\"", handler.LastBody);
        Assert.Contains("\"quests\":[65575]", handler.LastBody);
        // The monotonic rule survives the wire: unread categories never appear.
        Assert.DoesNotContain("achievements", handler.LastBody);
    }

    [Fact]
    public async Task A_malformed_token_short_circuits_without_touching_the_network()
    {
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, "{}"), token: "not-a-token");

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NotConfigured, response.Status);
        Assert.True(ApiStatusMap.IsTerminal(response.Status));
        Assert.Equal(0, handler.CallCount); // never sent — the whole point of the local check
    }

    [Fact]
    public async Task A_plaintext_remote_base_url_short_circuits_without_leaking_the_token()
    {
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, "{}"), baseUrl: "http://evil.example");

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NotConfigured, response.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task A_401_surfaces_the_opaque_invalid_token_error()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Unauthorized, """{"error": "invalid_token"}"""));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.InvalidToken, response.Status);
        Assert.Equal("invalid_token", response.Error!.Error);
        Assert.True(ApiStatusMap.IsTerminal(response.Status));
        Assert.Null(response.Value);
    }

    [Fact]
    public async Task A_403_carries_the_character_name_and_world_for_the_claim_hint()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Forbidden,
            """{"error": "character_not_claimed", "name": "Some Name", "world": "Excalibur"}"""));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.CharacterNotClaimed, response.Status);
        Assert.Equal("Some Name", response.Error!.Name);
        Assert.Equal("Excalibur", response.Error.World);
    }

    [Fact]
    public async Task A_429_reports_the_retry_after_delay()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json((HttpStatusCode)429, """{"error": "rate_limited"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.RateLimited, response.Status);
        Assert.True(ApiStatusMap.ShouldBackOff(response.Status));
        Assert.Equal(TimeSpan.FromSeconds(30), response.RetryAfter);
    }

    [Fact]
    public async Task A_503_means_the_global_kill_switch_is_off()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"error": "sync_disabled"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
            return response;
        });

        var response = await client.GetConfigAsync();

        Assert.Equal(ApiStatus.SyncDisabled, response.Status);
        Assert.True(ApiStatusMap.ShouldBackOff(response.Status));
        Assert.Equal(TimeSpan.FromSeconds(3600), response.RetryAfter);
    }

    [Fact]
    public async Task A_500_is_retryable_because_writes_are_idempotent()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.InternalServerError, ""));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.ServerError, response.Status);
        Assert.True(ApiStatusMap.IsRetryable(response.Status));
    }

    [Fact]
    public async Task A_transport_failure_becomes_a_network_error_rather_than_an_exception()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("dns exploded"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.True(ApiStatusMap.IsRetryable(response.Status));
    }

    // A timeout surfaces as TaskCanceledException while the CALLER's token is not cancelled. The
    // exception filter must classify that as a network error rather than let it escape.
    [Fact]
    public async Task A_timeout_becomes_a_network_error()
    {
        var (client, _) = Build(_ => throw new TaskCanceledException("timed out"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
    }

    // ...but a genuine caller cancellation is NOT an error and must propagate to the caller.
    [Fact]
    public async Task A_real_cancellation_propagates_instead_of_being_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (client, _) = Build(_ => throw new TaskCanceledException("cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMeAsync(cts.Token));
    }

    // A 200 we cannot parse is not a success. It must not surface as Ok with a null Value.
    [Fact]
    public async Task A_200_with_an_unparseable_body_is_not_treated_as_success()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.OK, "this is not json"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Unknown, response.Status);
        Assert.Null(response.Value);
        Assert.False(response.IsSuccess);
    }

    // A stray path in the user's base URL must never rewrite the endpoint. This is the sole
    // defense against a configured base URL redirecting requests somewhere unintended.
    [Fact]
    public async Task A_stray_path_in_the_base_url_cannot_rewrite_the_endpoint()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, """{"characters": [], "user": {"id": "abc"}}"""),
            baseUrl: "https://xiv-shinies.com/some/stray/path");

        await client.GetMeAsync();

        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/me",
            handler.LastRequest!.RequestUri!.ToString());
    }

    // Retry-After is legal as an absolute HTTP-date as well as a delta. Both must be understood,
    // or a proxy rewriting the header would silently strip the backoff.
    [Fact]
    public async Task Retry_after_is_understood_in_its_http_date_form()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json((HttpStatusCode)429, """{"error": "rate_limited"}""");
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60));
            return response;
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.RateLimited, response.Status);
        Assert.NotNull(response.RetryAfter);
        // Allow slack for the clock ticking between building the header and reading it.
        Assert.InRange(response.RetryAfter!.Value, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
    }

    // The token goes to whatever host is configured, so a non-default backend must be explicitly
    // acknowledged first. Enforced in the send path, not just the UI.
    [Fact]
    public async Task An_unacknowledged_custom_backend_never_receives_the_token()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, "{}"),
            baseUrl: "https://someone-elses-server.example",
            customBackendAcknowledged: false);

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NotConfigured, response.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task An_acknowledged_custom_backend_is_allowed()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, """{"characters": [], "user": {"id": "abc"}}"""),
            baseUrl: "https://someone-elses-server.example",
            customBackendAcknowledged: true);

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("https://someone-elses-server.example/api/plugin/v1/me",
            handler.LastRequest!.RequestUri!.ToString());
    }

    // The official server needs no acknowledgement.
    [Fact]
    public async Task The_default_backend_needs_no_acknowledgement()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, """{"characters": [], "user": {"id": "abc"}}"""),
            customBackendAcknowledged: false);

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(1, handler.CallCount);
    }

    // Unloading the plugin cancels in-flight work. A request that starts after disposal must come
    // back as a network error, never as an ObjectDisposedException escaping into the game.
    [Fact]
    public async Task A_request_after_dispose_is_a_network_error_not_an_exception()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.OK, "{}"));
        client.Dispose();

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
    }

    // A Retry-After date already in the past means "retry now", never a negative wait.
    [Fact]
    public async Task A_retry_after_date_in_the_past_clamps_to_zero()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"error": "sync_disabled"}""");
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-60));
            return response;
        });

        var response = await client.GetConfigAsync();

        Assert.Equal(TimeSpan.Zero, response.RetryAfter);
    }
}
