using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Tests.Onboarding;

// Turns the outcome of a GET /me probe into something the wizard can show the user. The distinction
// that matters is "your token is wrong" (only you can fix it) versus "we could not ask" (try again).
public class TokenCheckTests
{
    [Fact]
    public void A_successful_probe_means_the_token_is_valid()
    {
        Assert.Equal(TokenCheckState.Valid, TokenCheck.FromApiStatus(ApiStatus.Ok));
    }

    // A 401 never heals on retry: the token is unknown, revoked, or malformed.
    [Fact]
    public void A_rejected_token_is_invalid()
    {
        Assert.Equal(TokenCheckState.Invalid, TokenCheck.FromApiStatus(ApiStatus.InvalidToken));
    }

    // The request was never sent — a malformed token, or a backend we refuse to send it to. Telling
    // the user "could not reach the server" would be wrong: nothing was ever asked.
    [Fact]
    public void A_request_that_was_never_sent_is_invalid_not_unreachable()
    {
        Assert.Equal(TokenCheckState.Invalid, TokenCheck.FromApiStatus(ApiStatus.NotConfigured));
    }

    // Everything else says nothing about the token. Do not blame the user for the server being down.
    [Theory]
    [InlineData(ApiStatus.NetworkError)]
    [InlineData(ApiStatus.ServerError)]
    [InlineData(ApiStatus.SyncDisabled)]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.Unknown)]
    [InlineData(ApiStatus.MethodNotAllowed)]
    public void A_failure_that_says_nothing_about_the_token_is_unreachable(ApiStatus status)
    {
        Assert.Equal(TokenCheckState.Unreachable, TokenCheck.FromApiStatus(status));
    }
}
