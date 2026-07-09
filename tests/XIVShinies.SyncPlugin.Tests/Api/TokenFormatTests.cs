using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Verifies the client-side shape check for the API token. Catching a mistyped/truncated paste
// here means a clear error message instead of a pointless 401 round trip to the server.
public class TokenFormatTests
{
    // A syntactically valid token body: exactly 43 base64url characters (40 digits + "abc").
    private const string ValidBody = "0123456789012345678901234567890123456789abc";

    [Fact]
    public void Accepts_a_correctly_shaped_token()
    {
        Assert.True(TokenFormat.IsWellFormed("xvs_" + ValidBody));
    }

    // `[Theory]` + `[InlineData]` is xUnit's table-driven test: the method runs once per
    // InlineData row, with the row's values as arguments. Think `test.each([...])` in Jest.
    [Theory]
    [InlineData(null)]                                     // no token configured
    [InlineData("")]                                       // empty
    [InlineData("   ")]                                    // whitespace
    [InlineData("0123456789012345678901234567890123456789abc")] // missing the xvs_ prefix
    [InlineData("xvs_tooshort")]                           // body far too short
    [InlineData("xvs_012345678901234567890123456789012345678")]   // 42-char body (one short)
    [InlineData("xvs_01234567890123456789012345678901234567890abcd")] // too long
    public void Rejects_malformed_tokens(string? token)
    {
        Assert.False(TokenFormat.IsWellFormed(token));
    }

    [Theory]
    [InlineData('+')]  // standard base64, but NOT base64url
    [InlineData('/')]  // ditto
    [InlineData('=')]  // padding is never present
    [InlineData('!')]  // plainly invalid
    public void Rejects_characters_outside_the_base64url_alphabet(char bad)
    {
        // Replace the final character of an otherwise-valid body with the bad character.
        var body = ValidBody[..^1] + bad;
        Assert.False(TokenFormat.IsWellFormed("xvs_" + body));
    }
}
