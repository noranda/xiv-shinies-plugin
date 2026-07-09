using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// Validation for the backend server URL. Dalamud recommends letting users point a plugin at
/// their own server rather than forcing the maintainer's, so this value is user-overridable —
/// but an override must never silently downgrade the connection to plaintext.
/// </summary>
/// <remarks>
/// The rules: the host must always be a <b>DNS name, never a raw IP address</b> (Dalamud requires
/// this, with no exemption — use <c>localhost</c> rather than <c>127.0.0.1</c>); and
/// <c>https://</c> is required for any remote host, with <c>http://</c> tolerated only for
/// loopback so local development works. Note the token travels in an <c>Authorization</c> header
/// on every request, so it is sent to whatever host is configured here — which is exactly why
/// plaintext to a remote host is refused outright.
/// </remarks>
public static class BackendUrl
{
    /// <summary>The official server, addressed by DNS hostname (never a raw IP).</summary>
    public const string Default = "https://xiv-shinies.com";

    // Scheme + host + port of the official server, computed once for comparison.
    private static readonly string DefaultAuthority =
        new Uri(Default).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// True when the given URL points at the official server. Used to decide whether the user
    /// must first acknowledge that their token will be sent to a server we do not run.
    /// </summary>
    public static bool IsDefault(Uri uri) =>
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority), DefaultAuthority, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates and normalizes a user-entered URL.
    /// </summary>
    /// <param name="raw">The raw text the user typed; may be null, blank, or padded with spaces.</param>
    /// <param name="normalized">The parsed URI when valid, otherwise null.</param>
    /// <param name="error">A user-facing explanation when invalid, otherwise null.</param>
    /// <returns>True when the URL is usable.</returns>
    // The `out` keyword means the method *returns a value through* that parameter — the caller
    // passes a variable that this method fills in. C# has no tuple destructuring in the JS sense,
    // so `bool TryX(input, out result, out error)` is the idiomatic "parse that can fail" shape.
    public static bool TryNormalize(string? raw, out Uri? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Enter a server URL.";
            return false;
        }

        // UriKind.Absolute rejects relative values like "xiv-shinies.com" (no scheme).
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            error = "That is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "The URL must start with https:// (or http:// for a local server).";
            return false;
        }

        // Dalamud requires plugins to reach a backend by DNS hostname rather than a raw IP
        // address, and states no exemption — so this rejects loopback IPs too. Nothing is lost:
        // "localhost" is a DNS name and reaches the same place as 127.0.0.1.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            error = "Enter the server by domain name (use localhost, not 127.0.0.1).";
            return false;
        }

        // Uri.IsLoopback is true for the host "localhost" (and for loopback IPs, already rejected
        // above). Note it is NOT true for lookalikes such as "localhost.evil.com".
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "Only https:// is allowed for a remote server; your token would otherwise " +
                    "be sent unencrypted.";
            return false;
        }

        normalized = uri;
        return true;
    }
}
