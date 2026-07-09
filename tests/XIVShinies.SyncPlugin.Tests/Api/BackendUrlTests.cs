using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// The backend URL is user-overridable (Dalamud recommends offering a user-defined server), but
// an override must never silently downgrade the connection to plaintext. Rule: https:// always,
// except http:// is tolerated for loopback so local development works.
public class BackendUrlTests
{
    [Fact]
    public void Default_is_the_official_https_host()
    {
        Assert.Equal("https://xiv-shinies.com", BackendUrl.Default);
        Assert.True(BackendUrl.TryNormalize(BackendUrl.Default, out _, out _));
    }

    [Theory]
    [InlineData("https://xiv-shinies.com")]
    [InlineData("https://staging.xiv-shinies.com")]
    [InlineData("http://localhost:8000")]   // loopback, addressed by DNS name
    [InlineData("https://xiv-shinies.com/")] // trailing slash tolerated
    [InlineData("  https://xiv-shinies.com  ")] // surrounding whitespace trimmed
    public void Accepts_https_anywhere_and_http_on_loopback_by_name(string raw)
    {
        Assert.True(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Fact]
    public void Recognizes_the_official_server_regardless_of_path_or_case()
    {
        Assert.True(BackendUrl.TryNormalize("https://XIV-Shinies.com/ignored/path", out var uri, out _));
        Assert.True(BackendUrl.IsDefault(uri!));

        Assert.True(BackendUrl.TryNormalize("https://staging.xiv-shinies.com", out var other, out _));
        Assert.False(BackendUrl.IsDefault(other!));
    }

    // Lookalike hosts must never be mistaken for loopback and allowed over plaintext.
    [Theory]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://127.0.0.1.evil.com")]
    [InlineData("http://localhost@evil.com")] // userinfo — the real host is evil.com
    public void Rejects_loopback_lookalike_hosts_over_plaintext(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("http://xiv-shinies.com")]  // plaintext to a remote host — the downgrade we block
    [InlineData("http://example.com:8080")]
    public void Rejects_plaintext_http_to_a_remote_host(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("xiv-shinies.com")]      // no scheme — not an absolute URI
    [InlineData("ftp://xiv-shinies.com")] // wrong scheme entirely
    public void Rejects_blank_relative_and_non_http_urls(string? raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    // Dalamud requires a backend be reached by DNS hostname, never a raw IP address, and states
    // no exemption — so even loopback IPs are refused. Use "localhost" instead of 127.0.0.1.
    [Theory]
    [InlineData("https://203.0.113.5")]      // remote IPv4
    [InlineData("https://203.0.113.5:8443")]
    [InlineData("https://[2001:db8::1]")]    // remote IPv6
    [InlineData("http://127.0.0.1:8000")]    // loopback IPv4 — still an IP address
    [InlineData("https://127.0.0.1:8000")]
    [InlineData("http://[::1]:8000")]        // loopback IPv6
    public void Rejects_every_raw_ip_address_including_loopback(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }
}
