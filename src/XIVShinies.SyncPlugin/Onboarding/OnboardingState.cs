using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Onboarding;

/// <summary>The steps of the first-run wizard, in order.</summary>
// The underlying integer values are their positions, which is what lets Advance and Back be simple
// arithmetic rather than a switch. Do not reorder them casually.
public enum OnboardingStep
{
    /// <summary>What this plugin does, and what it will send.</summary>
    Welcome,

    /// <summary>Paste an API token and prove it works.</summary>
    LinkAccount,

    /// <summary>Choose which collections to upload. Everything starts off.</summary>
    ChooseCategories,

    /// <summary>Consent is recorded and the plugin begins syncing.</summary>
    Done,
}

/// <summary>
/// The first-run wizard, as pure logic: which step, and whether the user may move on.
/// </summary>
/// <remarks>
/// <para>
/// This is where Dalamud's "explicit opt-in before any non-essential data collection" is actually
/// enforced. Nothing uploads until <see cref="Finish"/> writes the user's consent to the settings,
/// and <see cref="Finish"/> refuses to do so before the last step. The window draws whatever this
/// object says; it decides nothing on its own.
/// </para>
/// <para>
/// Deliberately free of Dalamud, ImGui, and the network. Verifying the token is an HTTP call, so the
/// window makes it and reports the answer back here through <see cref="RecordTokenCheck"/> — this
/// class only remembers what happened. That is what makes the whole flow testable.
/// </para>
/// </remarks>
public sealed class OnboardingState
{
    /// <summary>The step being shown.</summary>
    public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;

    /// <summary>What is known about the pasted token.</summary>
    public TokenCheckState TokenCheck { get; private set; } = TokenCheckState.NotChecked;

    /// <summary>True once the wizard has reached its final step.</summary>
    public bool IsComplete => Step == OnboardingStep.Done;

    /// <summary>Whether the user may move forward from the current step.</summary>
    /// <remarks>
    /// Only the account step has a real precondition. Letting the user past an unverified token would
    /// finish a wizard that cannot possibly sync, and the failure would surface much later as an
    /// opaque 401.
    /// </remarks>
    public bool CanAdvance => Step switch
    {
        OnboardingStep.LinkAccount => TokenCheck == TokenCheckState.Valid,

        // The category step imposes nothing. Every category starts off, so continuing without
        // choosing any is a coherent decision — "link my account, upload nothing yet" — and forcing a
        // selection to escape the wizard would make consent a toll rather than a choice.
        OnboardingStep.ChooseCategories => true,

        OnboardingStep.Welcome => true,
        _ => false,
    };

    /// <summary>Whether there is an earlier step to return to.</summary>
    public bool CanGoBack => Step > OnboardingStep.Welcome && Step != OnboardingStep.Done;

    /// <summary>Moves to the next step, if the current one allows it.</summary>
    /// <remarks>Silently does nothing when it may not — the button that calls this is drawn disabled.</remarks>
    public void Advance()
    {
        if (CanAdvance)
            Step++;
    }

    /// <summary>Returns to the previous step.</summary>
    /// <remarks>
    /// A verified token survives the trip. Re-probing a token the user never touched would be a
    /// pointless request, and would make stepping back feel like losing progress.
    /// </remarks>
    public void Back()
    {
        if (CanGoBack)
            Step--;
    }

    /// <summary>Records that a token probe is in flight, so the UI can show it and block advancing.</summary>
    public void BeginTokenCheck() => TokenCheck = TokenCheckState.Checking;

    /// <summary>Records what the server said about the token.</summary>
    public void RecordTokenCheck(ApiStatus status) => TokenCheck = Onboarding.TokenCheck.FromApiStatus(status);

    /// <summary>
    /// Forgets any previous verification, because the token being verified no longer exists.
    /// </summary>
    /// <remarks>
    /// Without this, a user could verify one token, paste a different one, and walk forward on the
    /// strength of the old answer — arriving at a working wizard and a broken plugin.
    /// </remarks>
    public void NotifyTokenEdited() => TokenCheck = TokenCheckState.NotChecked;

    /// <summary>
    /// Writes the user's consent. This is the moment the plugin is allowed to upload anything.
    /// </summary>
    /// <param name="settings">The settings to record consent into. The caller saves them.</param>
    /// <remarks>
    /// Note what this does <b>not</b> do: it enables no categories. Those are ticked by the user on
    /// the previous step and written as they are ticked. Opting someone in here — even into a
    /// sensible default — would be consent by omission, which is the exact thing the rule forbids.
    /// </remarks>
    public void Finish(PluginSettings settings)
    {
        // An interlock, not a formality. This method is what flips the plugin on, so it must be
        // impossible to call it from halfway through the wizard.
        if (!IsComplete)
            return;

        settings.OnboardingComplete = true;
        settings.MasterEnabled = true;
    }
}
