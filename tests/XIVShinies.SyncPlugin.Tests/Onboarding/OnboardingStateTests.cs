using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Tests.Onboarding;

// The first-run wizard, as pure logic: which step we are on, and whether the user may move forward.
// The window draws whatever this says. Nothing here touches the game, the network, or ImGui.
public class OnboardingStateTests
{
    private const string ValidToken = "xvs_0123456789012345678901234567890123456789abc";

    private static OnboardingState AtCategoryStep()
    {
        var state = new OnboardingState();
        state.Advance();                                // Welcome -> LinkAccount
        state.RecordTokenCheck(ApiStatus.Ok);
        state.Advance();                                // LinkAccount -> ChooseCategories
        return state;
    }

    [Fact]
    public void A_new_wizard_starts_at_the_welcome_step()
    {
        var state = new OnboardingState();

        Assert.Equal(OnboardingStep.Welcome, state.Step);
        Assert.Equal(TokenCheckState.NotChecked, state.TokenCheck);
        Assert.False(state.IsComplete);
    }

    // The welcome step only tells the user what the plugin does. There is nothing to get wrong.
    [Fact]
    public void The_welcome_step_can_always_be_left()
    {
        Assert.True(new OnboardingState().CanAdvance);
    }

    [Fact]
    public void Advancing_moves_through_the_steps_in_order()
    {
        var state = new OnboardingState();

        state.Advance();
        Assert.Equal(OnboardingStep.LinkAccount, state.Step);

        state.RecordTokenCheck(ApiStatus.Ok);
        state.Advance();
        Assert.Equal(OnboardingStep.ChooseCategories, state.Step);

        state.Advance();
        Assert.Equal(OnboardingStep.Done, state.Step);
        Assert.True(state.IsComplete);
    }

    // The account link is the one step with a real precondition: the server has to have confirmed
    // the token. Otherwise the user finishes a wizard that cannot possibly sync.
    [Fact]
    public void The_account_step_cannot_be_left_until_the_token_is_verified()
    {
        var state = new OnboardingState();
        state.Advance();

        Assert.Equal(OnboardingStep.LinkAccount, state.Step);
        Assert.False(state.CanAdvance);

        state.RecordTokenCheck(ApiStatus.Ok);
        Assert.True(state.CanAdvance);
    }

    // `Advance()` is `Step++` on an enum, guarded only by CanAdvance's `_ => false` arm. If that guard
    // were ever loosened, Step would walk past Done into an undefined value and IsComplete would
    // silently start returning false.
    [Fact]
    public void Advancing_past_the_last_step_stays_at_the_last_step()
    {
        var state = AtCategoryStep();
        state.Advance();
        Assert.Equal(OnboardingStep.Done, state.Step);

        state.Advance();
        state.Advance();

        Assert.Equal(OnboardingStep.Done, state.Step);
        Assert.True(state.IsComplete);
    }

    [Fact]
    public void Advancing_when_it_is_not_allowed_does_nothing()
    {
        var state = new OnboardingState();
        state.Advance();

        state.Advance(); // token never verified

        Assert.Equal(OnboardingStep.LinkAccount, state.Step);
    }

    [Theory]
    [InlineData(ApiStatus.InvalidToken)]
    [InlineData(ApiStatus.NetworkError)]
    [InlineData(ApiStatus.NotConfigured)]
    public void A_failed_check_does_not_unlock_the_account_step(ApiStatus status)
    {
        var state = new OnboardingState();
        state.Advance();

        state.RecordTokenCheck(status);

        Assert.False(state.CanAdvance);
    }

    // Editing the token invalidates whatever the last probe said. Otherwise a user could verify one
    // token, paste a different one, and walk forward on the strength of the old answer.
    [Fact]
    public void Editing_the_token_discards_the_previous_verification()
    {
        var state = new OnboardingState();
        state.Advance();
        state.RecordTokenCheck(ApiStatus.Ok);
        Assert.True(state.CanAdvance);

        state.NotifyTokenEdited();

        Assert.Equal(TokenCheckState.NotChecked, state.TokenCheck);
        Assert.False(state.CanAdvance);
    }

    [Fact]
    public void A_check_in_flight_does_not_allow_advancing()
    {
        var state = new OnboardingState();
        state.Advance();
        state.BeginTokenCheck();

        Assert.Equal(TokenCheckState.Checking, state.TokenCheck);
        Assert.False(state.CanAdvance);
    }

    // Every category starts off. Consent is something the user performs, never something they omit.
    [Fact]
    public void The_category_step_can_be_left_even_with_nothing_selected()
    {
        Assert.True(AtCategoryStep().CanAdvance);
    }

    [Fact]
    public void Going_back_returns_to_the_previous_step()
    {
        var state = AtCategoryStep();

        state.Back();

        Assert.Equal(OnboardingStep.LinkAccount, state.Step);
    }

    [Fact]
    public void The_first_step_cannot_go_back()
    {
        var state = new OnboardingState();

        Assert.False(state.CanGoBack);
        state.Back();

        Assert.Equal(OnboardingStep.Welcome, state.Step);
    }

    // Going back and forth must not require re-verifying a token that has not changed.
    [Fact]
    public void Stepping_back_keeps_a_verified_token()
    {
        var state = AtCategoryStep();

        state.Back();

        Assert.Equal(TokenCheckState.Valid, state.TokenCheck);
        Assert.True(state.CanAdvance);
    }

    // Finishing is the moment consent takes effect. Until then the plugin has uploaded nothing.
    [Fact]
    public void Finishing_records_consent_and_turns_the_plugin_on()
    {
        var settings = new PluginSettings {Token = ValidToken};
        var state = AtCategoryStep();

        state.Advance();
        state.Finish(settings);

        Assert.True(settings.OnboardingComplete);
        Assert.True(settings.MasterEnabled);
    }

    // A safety interlock: the wizard is the only thing that sets these, and only at the end of it.
    [Fact]
    public void Finishing_before_the_last_step_records_nothing()
    {
        var settings = new PluginSettings {Token = ValidToken};
        var state = AtCategoryStep();

        state.Finish(settings);

        Assert.False(settings.OnboardingComplete);
        Assert.False(settings.MasterEnabled);
    }

    // Finishing must not silently opt the user into categories they never ticked.
    [Fact]
    public void Finishing_does_not_enable_any_category_on_the_users_behalf()
    {
        var settings = new PluginSettings {Token = ValidToken};
        var state = AtCategoryStep();

        state.Advance();
        state.Finish(settings);

        Assert.Empty(settings.EnabledCategories);
    }
}
