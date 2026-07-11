using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The outcome of one API call. Every failure mode is a value rather than an exception, so callers
/// handle them with a plain <c>switch</c> instead of a try/catch — and so no unhandled exception
/// can escape into the game.
/// </summary>
/// <typeparam name="T">The success payload type.</typeparam>
// The `<T>` makes this a *generic* type — one definition that works for any payload, like
// `ApiResponse<MeResponse>` or `ApiResponse<SyncResponse>`. TypeScript generics are the same idea.
// `where T : class` constrains T to reference types, which is what lets `T?` mean "may be null".
public sealed record ApiResponse<T> where T : class
{
    /// <summary>What happened, in the contract's terms.</summary>
    public required ApiStatus Status { get; init; }

    /// <summary>The parsed success body. Non-null only when <see cref="Status"/> is Ok.</summary>
    public T? Value { get; init; }

    /// <summary>The parsed error body, when the server sent one.</summary>
    public ErrorResponse? Error { get; init; }

    /// <summary>
    /// How long the server asked us to wait, from its <c>Retry-After</c> header. Present on 429
    /// and 503.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The literal HTTP status code, when a response arrived at all (null for network failures).
    /// <see cref="Status"/> is the contract's interpretation and can erase the number — a 502
    /// from a proxy and a 418 both map to <see cref="ApiStatus.Unknown"/> — so diagnostics keep
    /// the original.
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>True when the call succeeded and a body was parsed.</summary>
    public bool IsSuccess => Status == ApiStatus.Ok && Value is not null;
}
