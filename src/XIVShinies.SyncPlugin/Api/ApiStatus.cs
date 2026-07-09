namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The meanings the server contract assigns to each HTTP status the plugin can receive.
/// </summary>
// An `enum` is a fixed set of named values — like a TypeScript string-union type
// (`type ApiStatus = 'Ok' | 'InvalidToken' | ...`), except the values are integers under the
// hood and the compiler enforces exhaustiveness far better than a bare string would.
public enum ApiStatus
{
    /// <summary>200 — the request was applied.</summary>
    Ok,

    /// <summary>400 — the payload failed validation. Our bug; the same body will fail again.</summary>
    InvalidPayload,

    /// <summary>401 — token missing, malformed, or unknown. Never heals on retry.</summary>
    InvalidToken,

    /// <summary>403 — the character is not claimed by this account on the website.</summary>
    CharacterNotClaimed,

    /// <summary>
    /// 405 — wrong HTTP method for the endpoint. Always a bug in this plugin, never something the
    /// user can fix, and repeating the call cannot help.
    /// </summary>
    MethodNotAllowed,

    /// <summary>413 — body over the size cap. Split the upload rather than repeat it.</summary>
    PayloadTooLarge,

    /// <summary>429 — over the per-token rate limit. Honor <c>Retry-After</c>.</summary>
    RateLimited,

    /// <summary>500 — the server failed mid-apply. Writes are idempotent, so a later retry is safe.</summary>
    ServerError,

    /// <summary>503 — the global kill switch is off. Back off for the advertised window.</summary>
    SyncDisabled,

    /// <summary>No HTTP response at all (DNS failure, timeout, connection reset).</summary>
    NetworkError,

    /// <summary>
    /// The request was never sent, because the plugin is not usable as configured — no
    /// well-formed token, or a backend URL we refuse to send a token to. Fix the settings; a
    /// retry cannot help.
    /// </summary>
    NotConfigured,

    /// <summary>A status the contract does not define.</summary>
    Unknown,
}

/// <summary>
/// Translates raw HTTP status codes into <see cref="ApiStatus"/> and classifies how the client
/// must react. Keeping this pure (no HttpClient, no Dalamud) is what lets it be unit-tested.
/// </summary>
public static class ApiStatusMap
{
    /// <summary>Maps an HTTP status code onto its contract meaning.</summary>
    // A `switch` expression: each `code => value` arm is tested top to bottom, and `_` is the
    // default (like a `default:` case, or the final `else`). It's an expression, so it *returns*
    // a value rather than executing statements.
    public static ApiStatus FromHttpStatusCode(int httpStatusCode) => httpStatusCode switch
    {
        200 => ApiStatus.Ok,
        400 => ApiStatus.InvalidPayload,
        401 => ApiStatus.InvalidToken,
        403 => ApiStatus.CharacterNotClaimed,
        405 => ApiStatus.MethodNotAllowed,
        413 => ApiStatus.PayloadTooLarge,
        429 => ApiStatus.RateLimited,
        500 => ApiStatus.ServerError,
        503 => ApiStatus.SyncDisabled,
        _ => ApiStatus.Unknown,
    };

    /// <summary>
    /// True when repeating the identical request cannot possibly succeed. The caller must stop
    /// and surface the problem to the user rather than retry.
    /// </summary>
    // `is A or B` is pattern matching — a compact way to write "equals any of these".
    public static bool IsTerminal(ApiStatus status) =>
        status is ApiStatus.InvalidToken
            or ApiStatus.CharacterNotClaimed
            or ApiStatus.InvalidPayload
            or ApiStatus.MethodNotAllowed
            or ApiStatus.PayloadTooLarge
            or ApiStatus.NotConfigured;

    /// <summary>
    /// True when the server explicitly asked us to wait (it sends a <c>Retry-After</c> header).
    /// Not terminal — the same request will succeed later — but never retry immediately.
    /// </summary>
    public static bool ShouldBackOff(ApiStatus status) =>
        status is ApiStatus.RateLimited or ApiStatus.SyncDisabled;

    /// <summary>
    /// True for transient failures where a single retry with backoff is reasonable.
    /// </summary>
    public static bool IsRetryable(ApiStatus status) =>
        status is ApiStatus.ServerError or ApiStatus.NetworkError;
}
