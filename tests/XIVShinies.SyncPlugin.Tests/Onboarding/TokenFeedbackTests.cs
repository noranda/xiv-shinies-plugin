using Xunit;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Tests.Onboarding;

// Which message the token box shows. It depends on BOTH what the server last said and what is typed,
// which is why it lives here rather than inside the window where nothing could test it.
public class TokenFeedbackTests
{
    private const string ValidToken = "xvs_0123456789012345678901234567890123456789abc";

    // A server verdict overrides whatever is in the box: once the server has spoken, its answer is
    // the interesting fact.
    [Theory]
    [InlineData(TokenCheckState.Checking, TokenFeedbackKind.Checking)]
    [InlineData(TokenCheckState.Valid, TokenFeedbackKind.Accepted)]
    [InlineData(TokenCheckState.Invalid, TokenFeedbackKind.Rejected)]
    [InlineData(TokenCheckState.Unreachable, TokenFeedbackKind.Unreachable)]
    public void A_server_verdict_is_reported_as_it_stands(TokenCheckState check, TokenFeedbackKind expected)
    {
        Assert.Equal(expected, TokenFeedback.For(check, ValidToken));
    }

    // Before any probe, the box's own contents decide what needs saying.
    [Fact]
    public void An_empty_box_asks_for_a_token()
    {
        Assert.Equal(TokenFeedbackKind.Empty, TokenFeedback.For(TokenCheckState.NotChecked, string.Empty));
        Assert.Equal(TokenFeedbackKind.Empty, TokenFeedback.For(TokenCheckState.NotChecked, null));
    }

    [Fact]
    public void Something_that_is_not_a_token_is_called_out_before_a_pointless_request()
    {
        Assert.Equal(
            TokenFeedbackKind.Malformed, TokenFeedback.For(TokenCheckState.NotChecked, "hunter2"));
    }

    [Fact]
    public void A_well_formed_unchecked_token_invites_the_user_to_verify()
    {
        Assert.Equal(
            TokenFeedbackKind.ReadyToVerify, TokenFeedback.For(TokenCheckState.NotChecked, ValidToken));
    }

    // "Checking" beats the box contents: a probe is in flight for whatever was there when it started.
    [Fact]
    public void A_probe_in_flight_reports_checking_even_over_an_empty_box()
    {
        Assert.Equal(TokenFeedbackKind.Checking, TokenFeedback.For(TokenCheckState.Checking, string.Empty));
    }
}
