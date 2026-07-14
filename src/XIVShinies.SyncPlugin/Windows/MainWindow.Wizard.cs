using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Windows;

// The first-run wizard: its three steps and the Back/forward footer. One part of the
// MainWindow class — see MainWindow.cs for the class doc, the window state, and the shared
// card system and widget bindings every part draws with.
internal sealed partial class MainWindow
{
    private void DrawWizard()
    {
        // Frame-scoped: the answer must describe THIS frame's rows, not a frame whose rows are gone.
        wizardShowedGroups = false;

        // The branded header carries "Step 1 of 3" — without it the wizard's length is unknowable.
        // The numbers come from the enum's positions, so a new step renumbers this automatically.
        var stepCount = (int)OnboardingStep.Done;
        var stepNumber = (int)onboarding.Step + 1;
        DrawBrandTitle(stepNumber <= stepCount ? $"Step {stepNumber} of {stepCount}" : null);

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
        // The website's name is picked out in the brand gold mid-sentence, which TextWrapped cannot
        // do — hence the span helper.
        Widgets.DrawWrappedSpans(
            ($"{PluginMeta.DisplayName} reads what you have collected in game and uploads it to", null),
            ("xiv-shinies.com,", Brand.Gold),
            ("so the website knows what you own without you ticking it off by hand.", null));

        Widgets.SectionGap();
        DrawSectionHeading("What it sends");

        // The description's left edge lines up with the name's, not the gem's: measure the icon
        // column (glyph plus the spacing SameLine inserts) and indent by exactly that.
        float iconColumn;
        using (iconFont.Push())
        {
            iconColumn = ImGui.CalcTextSize(FontAwesomeIcon.Gem.ToIconString()).X
                + ImGui.GetStyle().ItemSpacing.X;
        }

        // Each collector describes itself. Adding a collection makes it appear here with no change
        // to this window: a gold gem, the name, and the collector's own plain-language disclosure
        // beneath it. The disclosure draws at the normal text color, never muted: it is the consent
        // copy telling the user what leaves their machine, so it has to be comfortably legible.
        foreach (var collector in collectors)
        {
            DrawIcon(FontAwesomeIcon.Gem, Brand.Gold);
            ImGui.SameLine();
            ImGui.TextUnformatted(collector.DisplayName);

            ImGui.Indent(iconColumn);
            DrawWrapped(collector.WhatGetsSent, ImGuiCol.Text);
            ImGui.Unindent(iconColumn);
        }

        Widgets.SectionGap();
        DrawPrivacyCard(
            "Your character is identified by a one-way fingerprint computed on this machine. " +
            "Your character's name and home world are sent so xiv-shinies.com can match the " +
            "character you already claimed. Nothing is uploaded until you finish this setup, " +
            "and you choose which of the above to include.");

        DrawWizardNav("Get started");
    }

    private void DrawLinkAccountStep()
    {
        // A verified token is the first moment the server will answer this plugin at all, so it is when
        // the config — and with it the list of item groups the consent step must offer — is fetched.
        // Holding the user here until that answer lands is what makes the next step whole: it sees the
        // group checkboxes from its very first frame, so ticking a category can tick the groups that
        // belong to it, and no consent can be granted for a checkbox that was not on screen at the time.
        // A failed poll still answers, so this can never become a trap; it just leaves the next step
        // with no groups to show.
        onboarding.NotifyAwaitingConfig(
            onboarding.TokenCheck == TokenCheckState.Valid && syncManager.OnboardingConfigPending);

        DrawTokenPanel();
        DrawWizardNav("Continue");
    }

    private void DrawChooseCategoriesStep()
    {
        ImGui.TextWrapped(
            "Choose what to upload. Everything starts switched off — nothing is sent unless you " +
            "turn it on here. You can change any of this later.");

        Widgets.SectionGap();

        // Everything this step will ever show is on screen from its first frame: the account step holds
        // the user until the server's config has answered (see DrawLinkAccountStep), so a category's
        // group checkboxes exist by the time its own checkbox can be ticked. That is what makes ticking
        // a category able to tick the groups it means, and it is why no consent here can ever be granted
        // for a checkbox the user was not looking at.
        //
        // No "New" badges: the manifest groups drawn beneath a category are all being shown for the
        // first time, to a user who installed the plugin minutes ago. DrawGroupCheckboxes still marks
        // every group it draws as seen, so the settings screen greets them badge-free afterwards.
        DrawCategoryRows(BuildCategoryRows(), showNewChips: false);

        ImGui.Spacing();
        DrawWizardNav("Finish");
    }

    /// <summary>Draws Back and the step's forward button, disabling the latter when the step forbids it.</summary>
    /// <remarks>
    /// Classic wizard footer: Back sits quietly on the left in the default style; the forward button
    /// is the branded primary, right-aligned — the strongest visual weight on the one action that
    /// moves the user forward.
    /// </remarks>
    private void DrawWizardNav(string forwardLabel)
    {
        // The footer gets more air than the sections above it: the primary action should sit
        // apart from the content, not crowd the last paragraph.
        Widgets.SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Wide enough to feel like a primary action even for short labels, and grows with long ones.
        // Back uses the same size, so the footer's two buttons read as a matched pair.
        var buttonSize = new Vector2(
            Math.Max(120f * ImGuiHelpers.GlobalScale,
                ImGui.CalcTextSize(forwardLabel).X + (40f * ImGuiHelpers.GlobalScale)),
            0f);

        if (onboarding.CanGoBack)
        {
            if (BoldButton("Back", buttonSize))
                onboarding.Back();

            ImGui.SameLine();
        }

        // Right-align: the forward button's right edge meets the content region's.
        Widgets.AlignRight(buttonSize.X);

        // The forward button is the wizard's one live action, and the steps that hold it shut — an
        // unverified token, a config still being fetched — are the whole point of it being shut. It has
        // to LOOK shut, which is what PrimaryButton's own disabled treatment is for.
        var forwardPressed = PrimaryButton(forwardLabel, buttonSize, onboarding.CanAdvance);

        if (forwardPressed)
        {
            onboarding.Advance();

            // Finish is a no-op until the last step, so calling it unconditionally is safe: it is the
            // state machine, not this window, that decides when consent has been given.
            onboarding.Finish(configuration.Settings);

            if (configuration.Settings.OnboardingComplete)
            {
                // Settles the one-time migration flag for a user who chose their groups by hand, so that
                // migration can never later re-enable a group they deliberately left off. What settles it
                // is what the wizard DREW, tracked in wizardShowedGroups, never what the server sent: a
                // user shown no checkbox chose nothing, and the migration must stay free to speak for
                // them. See PluginSettings.SettleItemGroupConsent.
                configuration.Settings.SettleItemGroupConsent(wizardShowedGroups);

                // Unconditional: Finish has just written OnboardingComplete, and that has to reach disk
                // whether or not there was any group consent to settle alongside it.
                configuration.Save();
            }
        }
    }
}
