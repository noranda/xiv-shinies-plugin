using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Onboarding;

/// <summary>What the token box should be telling the user right now.</summary>
public enum TokenFeedbackKind
{
    /// <summary>Nothing has been typed yet.</summary>
    Empty,

    /// <summary>Something is typed, but it is not shaped like a token.</summary>
    Malformed,

    /// <summary>A well-formed token that has not been checked with the server.</summary>
    ReadyToVerify,

    /// <summary>A probe is in flight.</summary>
    Checking,

    /// <summary>The server recognized the token.</summary>
    Accepted,

    /// <summary>The server rejected the token. Only the user can fix it.</summary>
    Rejected,

    /// <summary>The server could not be reached. The token may be perfectly good.</summary>
    Unreachable,
}

/// <summary>
/// Chooses which message the token box shows, from what is typed and what the server last said.
/// </summary>
/// <remarks>
/// Extracted from the window because it is a real decision — it consults both the probe's outcome and
/// the contents of the text box — and a window cannot be unit-tested. The window is left holding only
/// the mapping from a kind to a color and a sentence, which is the part that cannot be wrong in an
/// interesting way.
/// </remarks>
public static class TokenFeedback
{
    /// <summary>Decides what to say about the token as it currently stands.</summary>
    /// <param name="check">What the last probe concluded, if any.</param>
    /// <param name="token">The text currently in the box.</param>
    public static TokenFeedbackKind For(TokenCheckState check, string? token) => check switch
    {
        TokenCheckState.Checking => TokenFeedbackKind.Checking,
        TokenCheckState.Valid => TokenFeedbackKind.Accepted,
        TokenCheckState.Invalid => TokenFeedbackKind.Rejected,
        TokenCheckState.Unreachable => TokenFeedbackKind.Unreachable,

        // Nothing has been checked. What to say depends on what is in the box: an empty box needs an
        // instruction, a malformed one needs a correction, and a well-formed one needs a nudge to
        // press the button. Telling the user "select Verify" beside an empty box would be silly.
        _ when string.IsNullOrEmpty(token) => TokenFeedbackKind.Empty,
        _ when !TokenFormat.IsWellFormed(token) => TokenFeedbackKind.Malformed,
        _ => TokenFeedbackKind.ReadyToVerify,
    };
}
