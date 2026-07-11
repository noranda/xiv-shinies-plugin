using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// Client-side shape validation for the XIV Shinies API token. Purely syntactic — only the
/// server can say whether a well-formed token is actually valid. Checking the shape locally lets
/// a mistyped or truncated paste fail immediately with a clear message instead of costing a
/// round trip that comes back as an opaque 401.
/// </summary>
public static class TokenFormat
{
    /// <summary>
    /// The <c>xvs_</c> prefix (short for "XIV Shinies") every token starts with. Defined by the
    /// server, not by the plugin — do not change it. Like <c>ghp_</c> on a GitHub token, a
    /// distinctive prefix lets a leaked value be recognized by secret scanners, and lets both
    /// sides reject obvious junk before doing any lookup work.
    /// </summary>
    public const string Prefix = "xvs_";

    // 32 random bytes encoded as unpadded base64url is always exactly 43 characters.
    private const int BodyLength = 43;

    /// <summary>
    /// True when <paramref name="token"/> has the exact shape the server issues:
    /// <c>xvs_</c> followed by 43 base64url characters.
    /// </summary>
    // `string?` means "this may be null" (nullable reference types, roughly TypeScript's
    // `string | null`). The compiler warns if we dereference it without checking.
    public static bool IsWellFormed(string? token)
    {
        // `string.IsNullOrEmpty` guards both null and "". A whitespace-only string fails the
        // prefix check below, so it needs no special case.
        if (string.IsNullOrEmpty(token))
            return false;

        // StringComparison.Ordinal = plain byte-wise comparison, no culture rules. Always use
        // Ordinal for machine-readable strings; culture-aware compares can surprise you.
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        if (token.Length != Prefix.Length + BodyLength)
            return false;

        // Walk the body and reject anything outside the base64url alphabet.
        for (var i = Prefix.Length; i < token.Length; i++)
        {
            if (!IsBase64UrlCharacter(token[i]))
                return false;
        }

        return true;
    }

    // base64url swaps standard base64's '+' and '/' for '-' and '_', and drops '=' padding.
    private static bool IsBase64UrlCharacter(char c) =>
        c is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '-'
        or '_';
}
