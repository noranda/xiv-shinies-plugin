using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Onboarding;

/// <summary>What the wizard currently knows about the token the user pasted.</summary>
public enum TokenCheckState
{
    /// <summary>No probe has been made, or the token changed since the last one.</summary>
    NotChecked,

    /// <summary>A probe is in flight.</summary>
    Checking,

    /// <summary>The server recognized the token.</summary>
    Valid,

    /// <summary>The token is wrong. Only the user can fix this.</summary>
    Invalid,

    /// <summary>We could not ask. The token may be perfectly good.</summary>
    Unreachable,
}

/// <summary>
/// Interprets a <c>GET /me</c> probe for the onboarding wizard.
/// </summary>
/// <remarks>
/// The distinction this class exists to draw is <b>"your token is wrong"</b> versus <b>"we could not
/// ask"</b>. Collapsing the two would either blame the user for the server being down, or leave them
/// retrying a token that will never work. Pure, so both branches are covered by tests rather than by
/// hope.
/// </remarks>
public static class TokenCheck
{
    /// <summary>Maps the outcome of the probe onto what the wizard should say about it.</summary>
    public static TokenCheckState FromApiStatus(ApiStatus status) => status switch
    {
        ApiStatus.Ok => TokenCheckState.Valid,

        // A 401 never heals on retry: unknown, revoked, or malformed.
        ApiStatus.InvalidToken => TokenCheckState.Invalid,

        // The request was never sent — the token failed its local shape check, or the backend is one
        // the client refuses to send a token to. "Could not reach the server" would be a lie: nothing
        // was ever asked. Either way the fix is in the user's hands.
        ApiStatus.NotConfigured => TokenCheckState.Invalid,

        // Everything else — a timeout, a 500, the global kill switch, an unrecognized status — says
        // nothing whatsoever about the token.
        _ => TokenCheckState.Unreachable,
    };
}
