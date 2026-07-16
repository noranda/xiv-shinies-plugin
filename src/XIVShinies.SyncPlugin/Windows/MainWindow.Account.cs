using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Onboarding;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

// The account panel — the token box, its verification feedback, and the claimed-characters
// list — shared by the wizard's link step and the settings screen, plus the token probe
// plumbing both go through. One part of the MainWindow class — see MainWindow.cs for the
// class doc, the window state, and the shared card system and widget bindings.
internal sealed partial class MainWindow
{
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
        using (BrandCard())
        {
            // Custom card header: title left; right-aligned, a button that opens the browser
            // straight to the profile settings on the website, where tokens are created.
            DrawCardTitle(FontAwesomeIcon.Key, "Your account");

            const string openLabel = "Open profile settings";
            var openWidth = IconButtonWidth(FontAwesomeIcon.Globe, openLabel);
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            Widgets.AlignRight(openWidth, innerRight);
            var openButtonPos = ImGui.GetCursorPos();

            if (BoldButton("###openProfile", new Vector2(openWidth, 0f)))
                Util.OpenLink($"{BackendUrl.Default}/profile");

            DrawButtonFeedback(
                openButtonPos, openWidth, FontAwesomeIcon.Globe, Brand.Teal,
                openLabel, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);

            CloseCardHeader();

            DrawWrapped(
                "Create a plugin token in your profile settings on xiv-shinies.com, then paste " +
                "it below. The token is shown once and can be revoked at any time.",
                ImGuiCol.Text);

            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));

            // The button is busy for as long as anything it started is still outstanding: the token
            // probe itself, and — in the wizard — the config the accepted token goes on to fetch. Both
            // are the same wait as far as the user is concerned, and a button that went idle between
            // them would invite a second press for a request already on its way.
            var checkInFlight = verifier.InFlight || onboarding.AwaitingConfig;
            // Three periods rather than the single "…" glyph: the font centers that glyph vertically, so
            // it reads as a row of dots floating in the middle of the line instead of trailing the text.
            var verifyLabel = checkInFlight ? "Checking..." : "Verify";

            // Size the input to the space the card has left after the Verify button — clamped on
            // both ends: a token is a fixed ~47 characters, so on a very wide window a full-width
            // box would be absurd, and on a degenerately narrow one the arithmetic could go
            // negative (which ImGui reads as "size from the right edge", not zero) — the floor
            // keeps the box a box. Logical units, scaled like everything else.
            //
            // Measured against the widest label the button can wear, so the box beside it keeps its
            // size while the button changes what it says.
            var verifyWidth = Math.Max(PaddedButtonWidth("Verify"), PaddedButtonWidth("Checking..."));

            var rightEdge = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            var available =
                rightEdge - ImGui.GetCursorPosX() - verifyWidth - ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(Math.Clamp(
                available, 120f * ImGuiHelpers.GlobalScale, 420f * ImGuiHelpers.GlobalScale));

            // ImGuiInputTextFlags.Password masks the text (ImGui draws asterisks). It is a credential,
            // and a plugin window is frequently on screen while streaming.
            if (ImGui.InputText("##token", ref tokenInput, TokenInputCapacity, ImGuiInputTextFlags.Password))
            {
                // Any edit invalidates the previous verification. Forget() also bumps the verifier's
                // generation, so an answer already in flight for the OLD token is discarded when it
                // lands rather than being reported against the text now in the box.
                onboarding.NotifyTokenEdited();
                verifier.Forget();
                account = null;
            }

            ImGui.SameLine();

            // The token is saved before the probe, because the API client reads it straight from the
            // settings rather than being handed it. Persisting an unverified token is harmless:
            // nothing uploads until onboarding is complete, and the master switch gates it thereafter.
            var canVerify = !checkInFlight && TokenFormat.IsWellFormed(tokenInput);

            // `using` guarantees the matching EndDisabled even if something inside throws — the same
            // job a `finally` would do, without the ceremony. An unbalanced disabled stack grays out
            // everything drawn after it for the rest of the frame.
            bool verifyPressed;
            using (ImRaii.Disabled(!canVerify))
                verifyPressed = BoldButton(verifyLabel, new Vector2(verifyWidth, 0f));

            if (verifyPressed)
            {
                configuration.Settings.Token = tokenInput;
                configuration.Save();

                onboarding.BeginTokenCheck();
                verifier.Start();
            }

            DrawTokenCheckFeedback();
        }
    }

    /// <summary>Says what the token box should be telling the user, in a color that matches.</summary>
    /// <remarks>
    /// Which message applies is decided by <see cref="TokenFeedback"/>, which is pure and tested. All
    /// that is left here is turning a kind into pixels.
    /// </remarks>
    private void DrawTokenCheckFeedback()
    {
        ImGui.Spacing();

        // The token has been accepted and the wizard is now waiting on what the server asks about, with
        // Continue disabled until it arrives. A disabled button with no explanation beside it reads as a
        // broken wizard, so the wait says what it is waiting for.
        if (onboarding.AwaitingConfig)
        {
            ImGui.TextUnformatted("Token accepted. Asking XIV Shinies what it collects...");
            return;
        }

        // The four neutral states below draw at the normal text color: each one is either an
        // instruction ("paste", "select Verify") or the live state of a check the user is waiting
        // on, so all four are text they are meant to read. Only the two decided outcomes carry a
        // color, and that color is the message (green accepted, red rejected).
        switch (TokenFeedback.For(onboarding.TokenCheck, tokenInput))
        {
            case TokenFeedbackKind.Empty:
                ImGui.TextUnformatted("Paste your token to continue.");
                break;

            case TokenFeedbackKind.Malformed:
                ImGui.TextUnformatted(
                    $"That does not look like a token. They begin with {TokenFormat.Prefix}.");
                break;

            case TokenFeedbackKind.ReadyToVerify:
                ImGui.TextUnformatted("Select Verify to check this token.");
                break;

            case TokenFeedbackKind.Checking:
                ImGui.TextUnformatted("Checking with xiv-shinies.com...");
                break;

            case TokenFeedbackKind.Accepted:
                // The same icon-led line as the rejection below it, through the same helper: the two
                // alternate in the same spot of the same panel, and a check that sat differently from
                // the triangle it replaces on screen would read as a different kind of message.
                DrawIconedText(
                    FontAwesomeIcon.Check, Widgets.SuccessColor, "Token accepted", Widgets.InlineIconScale);
                ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
                DrawClaimedCharacters();
                break;

            case TokenFeedbackKind.Rejected:
                DrawWarning("That token was not recognized. Generate a new one and paste it here.");
                break;

            case TokenFeedbackKind.Unreachable:
                DrawWarning("Could not reach xiv-shinies.com. Your token may be fine — try again.");
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
            DrawWarning(
                "This account has not claimed any characters yet. Claim your character on " +
                "xiv-shinies.com first, or uploads will be refused.");
            return;
        }

        ImGui.Spacing();

        // A caption over the list; the character names beneath it are what the user reads.
        ImGui.TextDisabled("Claimed characters:");

        foreach (var character in account.Characters)
        {
            // A person glyph rather than ImGui.Bullet — the round bullet reads like a radio button
            // next to this window's checkboxes.
            DrawIcon(FontAwesomeIcon.User, Brand.Teal);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{character.Name} ({character.World})");
        }

        // The list is a snapshot from the last probe, and nothing about it says so — a user who
        // claimed an alt on the website after setup would otherwise stare at a list that looks live
        // and never guess that Verify doubles as refresh. During setup the probe just ran seconds
        // ago at the user's own press, so the list cannot be stale and the hint has nothing to say.
        if (!configuration.Settings.OnboardingComplete)
            return;

        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
        DrawWrapped(
            "Claimed a new character on xiv-shinies.com? Press Verify to refresh this list.",
            ImGuiCol.Text);
    }

    /// <summary>
    /// Issues one token probe per session when the Account panel first opens with a saved, usable
    /// token — so it greets the user with the token's real state and the claimed-characters list,
    /// instead of asking them to press Verify to learn what is already true.
    /// </summary>
    /// <remarks>
    /// Runs through the upload gate: a Verify press is direct user action, but this is automatic,
    /// so it must respect the same consent switches as every other unprompted request. The flag is
    /// deliberately not reset on failure — "could not reach xiv-shinies.com" plus the Verify
    /// button to retry by hand is the honest resting state, not a silent retry loop.
    /// </remarks>
    private void TryAutoVerifyToken()
    {
        if (sessionTokenProbeRequested || verifier.InFlight)
            return;

        // A probe already answered this session (the wizard's Verify, most likely) — nothing to do.
        if (account is not null)
        {
            sessionTokenProbeRequested = true;
            return;
        }

        if (!UploadGate.CanContactServer(configuration.Settings)
            || !TokenFormat.IsWellFormed(tokenInput))
        {
            return;
        }

        sessionTokenProbeRequested = true;
        onboarding.BeginTokenCheck();
        verifier.Start();
    }

    /// <summary>Folds a finished token probe into the wizard's state.</summary>
    /// <remarks>
    /// <para>
    /// An accepted token is also the moment the plugin first has a credential the server will answer
    /// to, so it is where <c>/config</c> is fetched — see
    /// <see cref="SyncManager.RequestOnboardingConfigPoll"/> for why that request is allowed to run
    /// before onboarding is complete. Without it the wizard's consent step could not list the
    /// server's item manifest groups, because nothing would ever have asked for them.
    /// </para>
    /// <para>
    /// A one-shot without needing a flag of its own. <c>Draw</c> calls this sixty times a second, but
    /// <see cref="TokenVerifier.TakeResult"/> hands a probe's answer out exactly once, so the body below
    /// runs on the single frame that answer arrives and does nothing on every other.
    /// </para>
    /// </remarks>
    private void ConsumeTokenProbe()
    {
        if (verifier.TakeResult() is not { } response)
            return;

        onboarding.RecordTokenCheck(response.Status);
        account = response.Value;

        // Two conditions, and both are load-bearing. The token must be one the server actually
        // recognized — asking for a config with a token just rejected would earn a second 401 and
        // tell nobody anything. And onboarding must still be in progress, which is the only
        // situation the gate bypass exists for: once the wizard is done, SyncManager's own
        // interval poll owns /config and runs through the ordinary consent gate like everything
        // else, so there is nothing here left to bypass it for.
        if (onboarding.TokenCheck == TokenCheckState.Valid
            && !configuration.Settings.OnboardingComplete)
        {
            syncManager.RequestOnboardingConfigPoll();
        }
    }
}
