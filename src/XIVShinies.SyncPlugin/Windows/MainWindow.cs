using System;
using System.Collections.Generic;
// Vector2/Vector4 are simple float tuples from the .NET math library. ImGui uses them for sizes,
// positions, and colors (RGBA, each component 0..1).
using System.Numerics;
// At Dalamud API 15 the ImGui bindings live under Dalamud.Bindings.ImGui (NOT the older
// ImGuiNET package). ImGui is an "immediate mode" GUI: instead of building a retained tree of
// components like React, you re-issue draw calls every frame inside Draw() below.
using Dalamud.Bindings.ImGui;
// The windowing helpers (Window base class, WindowSystem) that manage plugin windows for us.
using Dalamud.Interface.Windowing;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Onboarding;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The plugin window opened by the <c>/shinies</c> command.
/// </summary>
/// <remarks>
/// <para>
/// Shows the first-run wizard until the user has consented, and the settings afterwards. It never
/// opens itself: Dalamud forbids unprompted windows, so it appears only from <c>/shinies</c> or the
/// installer's open and settings buttons.
/// </para>
/// <para>
/// This class draws and nothing else. Which step the wizard is on lives in <see cref="OnboardingState"/>;
/// which rows to draw lives in <see cref="CategorySettingsView"/>; what a token probe means lives in
/// <see cref="Onboarding.TokenCheck"/>. All three are pure and unit-tested, which is why almost
/// nothing here can be wrong in an interesting way.
/// </para>
/// </remarks>
// `sealed` means no other class may inherit from this one. We inherit from Dalamud's `Window` base
// class AND implement `IDisposable` — the .NET pattern for "I hold something that must be cleaned
// up", whose `Dispose()` is the rough equivalent of a React `useEffect` cleanup function.
//
// `internal` (visible only inside this assembly) rather than `public`, because it takes a SyncManager,
// which is itself internal. C# refuses to expose a public method whose parameter type is less
// accessible than the method — otherwise a caller outside the assembly could see the constructor but
// never name a value to pass it. Nothing outside the plugin constructs this window anyway.
internal sealed class MainWindow : Window, IDisposable
{
    /// <summary>The longest token string the input box will accept, comfortably above the real 47.</summary>
    private const int TokenInputCapacity = 128;

    private static readonly Vector4 ErrorColor = new(0.94f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 SuccessColor = new(0.45f, 0.85f, 0.55f, 1f);
    private static readonly Vector4 MutedColor = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly Configuration configuration;
    private readonly SyncManager syncManager;
    private readonly IReadOnlyList<ICollector> collectors;
    private readonly TokenVerifier verifier;

    private readonly OnboardingState onboarding = new();

    // The text currently in the token box. ImGui hands us a `ref string` and rewrites it in place,
    // so this must be a field rather than something rebuilt each frame. Seeded from the saved token
    // in the constructor.
    private string tokenInput;

    // The account the last successful probe belonged to, for showing which characters are claimed.
    private MeResponse? account;

    /// <summary>Builds the window. It is not opened here — Dalamud forbids unprompted windows.</summary>
    public MainWindow(
        Configuration configuration,
        ApiClient apiClient,
        SyncManager syncManager,
        IReadOnlyList<ICollector> collectors)
        // `: base(...)` calls the parent Window constructor first, passing the window's title. The
        // `###XIVShiniesMain` suffix is an ImGui trick: text before `###` is the visible title, and
        // the part from `###` on is a stable internal ID, so the visible title can change later
        // without ImGui treating it as a different window (and losing its saved position/size).
        : base($"{PluginMeta.DisplayName}###XIVShiniesMain")
    {
        this.configuration = configuration;
        this.syncManager = syncManager;
        this.collectors = collectors;
        verifier = new TokenVerifier(apiClient);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Start the token box from whatever is already saved, so reopening the wizard does not look
        // like the token vanished.
        tokenInput = configuration.Settings.Token;
    }

    /// <summary>Cancels any token probe still in flight.</summary>
    public void Dispose() => verifier.Dispose();

    /// <summary>
    /// Called once per frame by the WindowSystem while the window is open, so everything here runs
    /// roughly sixty times a second.
    /// </summary>
    /// <remarks>
    /// That is the immediate-mode model: the UI is described afresh every frame rather than mutated.
    /// Keep this cheap, and never block: whatever thread draws the game's frames is the one running
    /// this, and stalling it stalls the game.
    /// </remarks>
    public override void Draw()
    {
        // A probe finished on a background thread; fold its answer in before drawing anything that
        // depends on it.
        ConsumeTokenProbe();

        if (!configuration.Settings.OnboardingComplete)
        {
            DrawWizard();
            return;
        }

        DrawSettings();
    }

    // --- Wizard ----------------------------------------------------------------------------

    private void DrawWizard()
    {
        switch (onboarding.Step)
        {
            case OnboardingStep.Welcome:
                DrawWelcomeStep();
                break;

            case OnboardingStep.LinkAccount:
                DrawLinkAccountStep();
                break;

            case OnboardingStep.ChooseCategories:
                DrawChooseCategoriesStep();
                break;

            // Reaching Done means Finish already ran and flipped OnboardingComplete, so the next
            // frame draws the settings instead. Nothing to render.
            default:
                break;
        }
    }

    private void DrawWelcomeStep()
    {
        ImGui.TextWrapped(
            $"{PluginMeta.DisplayName} reads what you have collected in game and uploads it to " +
            "xiv-shinies.com, so the website knows what you own without you ticking it off by hand.");

        ImGui.Spacing();
        ImGui.TextColored(MutedColor, "Everything it can send, and nothing else:");
        ImGui.Spacing();

        // Each collector describes itself. Adding a collection makes it appear here with no change
        // to this window.
        foreach (var collector in collectors)
        {
            ImGui.Bullet();
            ImGui.TextWrapped($"{collector.DisplayName} — {collector.WhatGetsSent}");
        }

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Your character is identified by a one-way fingerprint computed on this machine. " +
            "Your character's name and home world are sent so the website can match the character " +
            "you already claimed. Nothing is uploaded until you finish this setup, and you choose " +
            "which of the above to include.");

        DrawWizardNav("Get started");
    }

    private void DrawLinkAccountStep()
    {
        DrawTokenPanel();
        DrawWizardNav("Continue");
    }

    /// <summary>
    /// The token box, its Verify button, and the resulting feedback.
    /// </summary>
    /// <remarks>
    /// Shared by the wizard and the settings screen. Without it in the settings, a user whose token is
    /// revoked would be told to "generate a new one" with nowhere to paste it, and the wizard never
    /// reappears once onboarding is complete — the plugin would be permanently stuck.
    /// </remarks>
    private void DrawTokenPanel()
    {
        ImGui.TextWrapped(
            "Create a plugin token on xiv-shinies.com, under Profile settings → Game plugin, " +
            "then paste it below. The token is shown once and can be revoked at any time.");

        ImGui.Spacing();

        if (ImGui.Button("Copy website address"))
            ImGui.SetClipboardText(BackendUrl.Default);

        ImGui.SameLine();
        ImGui.TextColored(MutedColor, BackendUrl.Default);

        ImGui.Spacing();

        // ImGuiInputTextFlags.Password masks the text (ImGui draws asterisks). It is a credential, and
        // a plugin window is frequently on screen while streaming.
        if (ImGui.InputText("##token", ref tokenInput, TokenInputCapacity, ImGuiInputTextFlags.Password))
        {
            // Any edit invalidates the previous verification. Forget() also bumps the verifier's
            // generation, so an answer already in flight for the OLD token is discarded when it lands
            // rather than being reported against the text now in the box.
            onboarding.NotifyTokenEdited();
            verifier.Forget();
            account = null;
        }

        ImGui.SameLine();

        // The token is saved before the probe, because the API client reads it straight from the
        // settings rather than being handed it. Persisting an unverified token is harmless: nothing
        // uploads until onboarding is complete, and the master switch gates it thereafter.
        var canVerify = !verifier.InFlight && TokenFormat.IsWellFormed(tokenInput);

        ImGui.BeginDisabled(!canVerify);
        var verifyPressed = ImGui.Button("Verify");
        ImGui.EndDisabled();

        // Acted on AFTER EndDisabled, so an exception from the disk write cannot leave ImGui's
        // disabled stack unbalanced and gray out the rest of the frame.
        if (verifyPressed)
        {
            configuration.Settings.Token = tokenInput;
            configuration.Save();

            onboarding.BeginTokenCheck();
            verifier.Start();
        }

        DrawTokenCheckFeedback();
    }

    /// <summary>Says what the token box should be telling the user, in a color that matches.</summary>
    /// <remarks>
    /// Which message applies is decided by <see cref="TokenFeedback"/>, which is pure and tested. All
    /// that is left here is turning a kind into pixels.
    /// </remarks>
    private void DrawTokenCheckFeedback()
    {
        ImGui.Spacing();

        switch (TokenFeedback.For(onboarding.TokenCheck, tokenInput))
        {
            case TokenFeedbackKind.Empty:
                ImGui.TextColored(MutedColor, "Paste your token to continue.");
                break;

            case TokenFeedbackKind.Malformed:
                ImGui.TextColored(
                    MutedColor, $"That does not look like a token. They begin with {TokenFormat.Prefix}.");
                break;

            case TokenFeedbackKind.ReadyToVerify:
                ImGui.TextColored(MutedColor, "Select Verify to check this token.");
                break;

            case TokenFeedbackKind.Checking:
                ImGui.TextColored(MutedColor, "Checking with xiv-shinies.com…");
                break;

            case TokenFeedbackKind.Accepted:
                ImGui.TextColored(SuccessColor, "Token accepted.");
                DrawClaimedCharacters();
                break;

            case TokenFeedbackKind.Rejected:
                ImGui.TextColored(
                    ErrorColor, "That token was not recognized. Generate a new one and paste it here.");
                break;

            case TokenFeedbackKind.Unreachable:
                ImGui.TextColored(
                    ErrorColor, "Could not reach xiv-shinies.com. Your token may be fine — try again.");
                break;
        }
    }

    /// <summary>
    /// Lists the characters this account has claimed, so the user can see the plugin will have
    /// somewhere to put the data before they finish setup.
    /// </summary>
    private void DrawClaimedCharacters()
    {
        if (account is null)
            return;

        if (account.Characters.Count == 0)
        {
            ImGui.TextColored(
                ErrorColor,
                "This account has not claimed any characters yet. Claim your character on the " +
                "website first, or uploads will be refused.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(MutedColor, "Claimed characters:");

        foreach (var character in account.Characters)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted($"{character.Name} ({character.World})");
        }
    }

    private void DrawChooseCategoriesStep()
    {
        ImGui.TextWrapped(
            "Choose what to upload. Everything starts switched off — nothing is sent unless you " +
            "turn it on here. You can change any of this later.");

        ImGui.Spacing();
        DrawCategoryRows();

        ImGui.Spacing();
        DrawWizardNav("Finish");
    }

    /// <summary>Draws Back and the step's forward button, disabling the latter when the step forbids it.</summary>
    private void DrawWizardNav(string forwardLabel)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (onboarding.CanGoBack)
        {
            if (ImGui.Button("Back"))
                onboarding.Back();

            ImGui.SameLine();
        }

        ImGui.BeginDisabled(!onboarding.CanAdvance);
        var forwardPressed = ImGui.Button(forwardLabel);
        ImGui.EndDisabled();

        // Acted on after EndDisabled, so a throwing disk write cannot leave ImGui's disabled stack
        // unbalanced and gray out everything drawn after it.
        if (forwardPressed)
        {
            onboarding.Advance();

            // Finish is a no-op until the last step, so calling it unconditionally is safe: it is the
            // state machine, not this window, that decides when consent has been given.
            onboarding.Finish(configuration.Settings);

            if (configuration.Settings.OnboardingComplete)
                configuration.Save();
        }
    }

    // --- Settings --------------------------------------------------------------------------

    private void DrawSettings()
    {
        DrawStatus();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var masterEnabled = configuration.Settings.MasterEnabled;
        if (ImGui.Checkbox("Sync my collections", ref masterEnabled))
        {
            configuration.Settings.MasterEnabled = masterEnabled;
            configuration.Save();
        }

        ImGui.TextColored(MutedColor, "While this is off the plugin uploads nothing at all.");

        ImGui.Spacing();
        DrawCategoryRows();

        ImGui.Spacing();
        ImGui.Separator();

        // Collapsed by default: replacing a token is rare, and the box should not invite fiddling.
        // But it must exist — a revoked token otherwise leaves the plugin permanently stuck, since the
        // wizard never returns once onboarding is complete.
        if (ImGui.CollapsingHeader("Account"))
        {
            ImGui.Spacing();
            DrawTokenPanel();
        }
    }

    private void DrawStatus()
    {
        if (syncManager.BlockedPendingUserAction)
        {
            ImGui.TextColored(
                ErrorColor,
                "Syncing has stopped. Your token may have been revoked, or this character is not " +
                "claimed on the website.");
        }
        else if (!syncManager.HasCharacter)
        {
            ImGui.TextColored(MutedColor, "Waiting for a character to finish logging in.");
        }
        else if (syncManager.LastStatus is { } status)
        {
            DrawLastStatus(status);
        }
        else
        {
            ImGui.TextColored(MutedColor, "Nothing has been uploaded yet this session.");
        }

        ImGui.Spacing();

        if (ImGui.Button("Sync now"))
            syncManager.RequestManualSync();
    }

    /// <summary>Renders the last upload's outcome. Switches on a status, never on a category.</summary>
    private static void DrawLastStatus(ApiStatus status)
    {
        switch (status)
        {
            case ApiStatus.Ok:
                ImGui.TextColored(SuccessColor, "Your collections are up to date.");
                break;

            case ApiStatus.CharacterNotClaimed:
                ImGui.TextColored(
                    ErrorColor, "Claim this character on xiv-shinies.com before it can sync.");
                break;

            case ApiStatus.InvalidToken:
                ImGui.TextColored(ErrorColor, "Your token was rejected. Generate a new one.");
                break;

            case ApiStatus.RateLimited:
            case ApiStatus.SyncDisabled:
                ImGui.TextColored(MutedColor, "Waiting before the next upload, as the server asked.");
                break;

            case ApiStatus.NetworkError:
                ImGui.TextColored(MutedColor, "Could not reach xiv-shinies.com. Will try again.");
                break;

            default:
                ImGui.TextColored(MutedColor, "The last upload did not succeed. Will try again.");
                break;
        }
    }

    /// <summary>
    /// Draws one checkbox per registered collector, shared by the wizard and the settings.
    /// </summary>
    /// <remarks>
    /// Contains no category names. Every label, description, and hint comes from the row the
    /// collector produced, which is what keeps "adding a collection is one new class" true.
    /// </remarks>
    private void DrawCategoryRows()
    {
        var rows = CategorySettingsView.Build(
            collectors, configuration.Settings, syncManager.RemoteConfig, syncManager.LastSkipped);

        foreach (var row in rows)
        {
            // The server switched this category off for everyone. Show it, disabled, with the user's
            // own preference intact underneath — flipping it back on later restores what they chose.
            ImGui.BeginDisabled(!row.ServerEnabled);

            var enabled = row.UserEnabled;

            // Everything after `##` is hidden from the label but forms part of the widget's identity.
            // ImGui derives a control's ID from its label text, so two collections that happened to
            // choose the same DisplayName would share an ID and cross-wire their clicks. The category
            // key is unique by construction, which makes this collision impossible rather than merely
            // unlikely.
            var toggled = ImGui.Checkbox($"{row.DisplayName}##{row.Key}", ref enabled);

            ImGui.EndDisabled();

            // Saved after EndDisabled so a throwing disk write cannot leave ImGui's disabled stack
            // unbalanced, which would gray out the remainder of the frame.
            if (toggled)
            {
                configuration.Settings.SetCategoryEnabled(row.Key, enabled);
                configuration.Save();
            }

            ImGui.Indent();
            ImGui.TextColored(MutedColor, row.WhatGetsSent);

            if (!row.ServerEnabled)
                ImGui.TextColored(MutedColor, "Temporarily switched off by XIV Shinies.");

            // The collector said why it could not read this category; the reason is turned into
            // advice without anyone here knowing which category it was.
            if (row.SkipReason is { } reason && CollectSkipReasons.Describe(reason) is { } hint)
                ImGui.TextColored(ErrorColor, hint);

            ImGui.Unindent();
            ImGui.Spacing();
        }
    }

    // --- Token probe -----------------------------------------------------------------------

    /// <summary>Folds a finished token probe into the wizard's state.</summary>
    private void ConsumeTokenProbe()
    {
        if (verifier.TakeResult() is not { } response)
            return;

        onboarding.RecordTokenCheck(response.Status);
        account = response.Value;
    }
}
