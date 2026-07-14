using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

// The settings screen's skeleton: the masthead, the collapsible sections, and the upload-log
// card (whose table body is UploadLogTable). One part of the MainWindow class — see
// MainWindow.cs for the class doc, the window state, and the shared card system and widget
// bindings.
internal sealed partial class MainWindow
{
    private void DrawSettings()
    {
        // The three surfaces that need the category rows — the read-status panel inside the sync card,
        // the "New" chip on the Collections header, and the consent card itself — are all drawn from
        // THIS list (see BuildCategoryRows).
        var rows = BuildCategoryRows();

        DrawSettingsHeader();

        Widgets.SectionGap();
        DrawSyncCard(rows);

        Widgets.SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Whether any manifest group anywhere in the list still counts as "New" (see AnyGroupIsNew).
        var hasNewGroup = AnyGroupIsNew(rows);

        // Captured immediately before the header so the "New" chip below can be placed on the
        // header's own row: CollapsingHeader always spans the full available width regardless of
        // whether it happens to sit inside a BrandCard, so this is the same cursor-space-to-inner-
        // right-edge fallback Widgets.DrawSectionLabel uses.
        var headerRowCursorX = ImGui.GetCursorPosX();
        var headerRowScreenX = ImGui.GetCursorScreenPos().X;
        var headerRowInnerRight =
            activeCardInnerRight ?? (headerRowCursorX + ImGui.GetContentRegionAvail().X);

        // The first of the settings screen's collapsible sections, and the only one that starts OPEN:
        // ImGuiTreeNodeFlags.DefaultOpen sets the header's initial state, after which ImGui remembers
        // whatever the user last chose. Consent is the one thing on this screen a user may want to
        // change at any moment, so it greets them expanded — but it is a long card, so it can be
        // folded away once they have made their choices.
        // "Collections", not the card's own longer title: the accordion headers name their section
        // briefly (Account, Privacy, Recent uploads) and the card inside carries the full heading.
        var collectionsOpen =
            ImGui.CollapsingHeader("Collections", ImGuiTreeNodeFlags.DefaultOpen);

        // Drawn in both states, open and collapsed (see AnyGroupIsNew). Positioned by DrawHeaderRightChip
        // rather than a plain SameLine(), because the header above just claimed the ENTIRE row width;
        // see that method's remarks for why SameLine cannot place a widget beside a full-width header.
        if (hasNewGroup)
        {
            DrawHeaderRightChip(
                FontAwesomeIcon.Star, "New", Brand.Gold,
                headerRowScreenX + (headerRowInnerRight - headerRowCursorX));
        }

        if (collectionsOpen)
        {
            ImGui.Spacing();
            DrawCategoryRows(rows, showNewChips: true, FontAwesomeIcon.Gem, "Collections to include");
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Collapsed by default: replacing a token is rare, and the box should not invite fiddling.
        // But it must exist — a revoked token otherwise leaves the plugin permanently stuck, since the
        // wizard never returns once onboarding is complete.
        if (ImGui.CollapsingHeader("Account"))
        {
            TryAutoVerifyToken();

            ImGui.Spacing();
            DrawTokenPanel();
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // The privacy disclosure from the wizard, permanently reachable: consent context should
        // not vanish the moment onboarding completes. Same card, with the last sentence phrased
        // for a configured plugin rather than a wizard mid-setup.
        if (ImGui.CollapsingHeader("Privacy"))
        {
            ImGui.Spacing();
            DrawPrivacyCard(
                "Your character is identified by a one-way fingerprint computed on this machine. " +
                "Your character's name and home world are sent so xiv-shinies.com can match the " +
                "character you already claimed. Nothing is uploaded unless syncing is switched " +
                "on, and you choose which collections to include.");
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // The privacy card's receipts: what actually went out, per upload. Lives at the very
        // bottom because it is a reference surface, not a control.
        if (ImGui.CollapsingHeader("Recent uploads"))
        {
            ImGui.Spacing();
            DrawUploadLog();
        }
    }

    /// <summary>
    /// The upload-log card: its header controls (clear, copy-to-clipboard), the in-memory
    /// disclosure, and the table body (see <see cref="UploadLogTable"/>, which owns the rows and
    /// the reasons this surface carries no category-name branches).
    /// </summary>
    private void DrawUploadLog()
    {
        var history = syncManager.UploadHistory;

        using (BrandCard())
        {
            // Custom card header: title left, the copy button right-aligned on the same row.
            // "Recent" is honest labeling — the log is in-memory and bounded, so it never claims
            // to be a full history.
            DrawCardTitle(FontAwesomeIcon.History, "Recent uploads");

            // Two right-aligned header controls: Clear wipes the in-memory log, Copy log puts a
            // wire-term plain-text dump on the clipboard for bug reports. Both dim while there is
            // nothing to act on. Buttons are drawn first and their faces after — a face's text is
            // its own item, and SameLine between the buttons must anchor on a real button
            // rectangle, not on an overlay (the same two-pass trick as the masthead links).
            const string clearLabel = "Clear";
            const string copyLabel = "Copy log";
            var clearWidth = IconButtonWidth(FontAwesomeIcon.Trash, clearLabel);
            var copyWidth = IconButtonWidth(FontAwesomeIcon.Copy, copyLabel);
            var gap = 8f * ImGuiHelpers.GlobalScale;
            var showCopied = DateTime.UtcNow < logCopyFeedbackUntil;
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            Widgets.AlignRight(clearWidth + gap + copyWidth, innerRight);

            bool clearPressed;
            bool copyPressed;
            using (ImRaii.Disabled(history.Count == 0))
            {
                var clearButtonPos = ImGui.GetCursorPos();
                clearPressed = BoldButton("###clearLog", new Vector2(clearWidth, 0f));

                ImGui.SameLine(0f, gap);
                var copyButtonPos = ImGui.GetCursorPos();
                copyPressed = BoldButton("###copyLog", new Vector2(copyWidth, 0f));

                // The faces. The red trash glyph marks Clear as the destructive one of the pair.
                var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
                DrawButtonFeedback(
                    clearButtonPos, clearWidth, FontAwesomeIcon.Trash, Widgets.ErrorColor,
                    clearLabel, textColor);

                if (showCopied)
                {
                    DrawButtonFeedback(
                        copyButtonPos, copyWidth, FontAwesomeIcon.Check, Widgets.SuccessColor,
                        "Copied", textColor);
                }
                else
                {
                    DrawButtonFeedback(
                        copyButtonPos, copyWidth, FontAwesomeIcon.Copy, Brand.Teal,
                        copyLabel, textColor);
                }
            }

            // No confirmation on Clear on purpose: the log is a bounded, in-memory convenience
            // that repopulates with the next upload — nothing of consequence is lost.
            if (clearPressed)
                syncManager.ClearUploadHistory();

            if (copyPressed && !showCopied)
            {
                // The dump names the EFFECTIVE backend (the user-overridable setting, not the
                // default): "you are pointed at the wrong server" is a classic support case.
                ImGui.SetClipboardText(UploadLogText.ClipboardText(
                    pluginVersion, configuration.Settings.BaseUrl, history));
                logCopyFeedbackUntil = DateTime.UtcNow.AddSeconds(1.5);
            }

            CloseCardHeader();

            // Normal text color: this sentence is what tells the user the log is a memory-only
            // record rather than a permanent one, so it is an explanation they need to read.
            DrawWrapped(
                "What this plugin sent recently. Kept in memory only — the log clears when the " +
                "plugin unloads.",
                ImGuiCol.Text);
            ImGui.Spacing();

            if (history.Count == 0)
            {
                // Muted: empty-state filler standing in for a table, with nothing in it to read.
                ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
                ImGui.TextDisabled("Nothing has been uploaded yet this session.");
                return;
            }

            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
            uploadLogTable.Draw(history, innerRight);
        }
    }

    /// <summary>The settings masthead: the mascot beside the plugin's name and one-line pitch.</summary>
    private void DrawSettingsHeader()
    {
        // GetWrapOrEmpty never blocks: while the file is still loading it returns a transparent
        // placeholder for this frame and the real pixels once ready — safe to call every frame.
        var mascot = mascotTexture.GetWrapOrEmpty();
        // Sized to roughly match the header block beside it (title, punchline, and link row).
        var mascotSize = 72f * ImGuiHelpers.GlobalScale;

        ImGui.Image(mascot.Handle, new Vector2(mascotSize));
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);

        // Grouping the title and pitch makes them one block beside the image: wrapped lines come
        // back to the block's left edge instead of the window's, staying clear of the mascot.
        using (ImRaii.Group())
        {
            using (headingFont.Available ? headingFont.Push() : null)
            {
                ImGui.TextColored(Brand.Teal, PluginMeta.DisplayName);
            }

            // The version, muted and right-aligned on the title line — the same spot the wizard
            // puts its step counter. It is the first thing a bug report needs.
            var versionLabel = $"v{pluginVersion}";
            ImGui.SameLine();
            Widgets.AlignRight(ImGui.CalcTextSize(versionLabel).X);
            ImGui.TextDisabled(versionLabel);

            // The manifest punchline, with the site picked out in gold like the wizard intro. The
            // rest is a null span color, meaning the normal text color: it is the one sentence
            // that says what the plugin is for, so it reads at full contrast.
            Widgets.DrawWrappedSpans(
                ("Your collections, on", null),
                ("xiv-shinies.com,", Brand.Gold),
                ("the moment you earn them.", null));

            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
            DrawLinkButtons();
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
        BrandSeparator();
    }

    /// <summary>The community links under the masthead description.</summary>
    /// <remarks>
    /// The Dalamud icon font carries only FontAwesome's solid set — no brand logos — so each link
    /// gets a fitting generic glyph instead. A link whose URL constant is empty draws no button,
    /// so retiring one is a one-line change in <see cref="PluginMeta"/>.
    /// </remarks>
    private void DrawLinkButtons()
    {
        // Two passes: every button first, overlays second. An overlay's icon and word are their
        // own (non-interactive) items, and SameLine anchors off the LAST item drawn — chaining the
        // next button off an overlay would place it mid-button and stack the row onto itself. With
        // the buttons drawn back-to-back, SameLine chains off real button rectangles.
        var overlays =
            new List<(Vector2 Position, float Width, FontAwesomeIcon Icon, Vector4 IconColor, string Label)>();
        var first = true;

        // Taller vertical item spacing for the row's scope, so when the buttons wrap onto a
        // second line the lines do not sit shoulder to shoulder. Horizontal gaps are unaffected
        // (SameLine passes them explicitly).
        using var rowSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));

        foreach (var (icon, iconColor, label, id, url) in LinkButtons)
        {
            if (url.Length == 0)
                continue;

            // The button is blank (its icon and label need two fonts, drawn over it below) but
            // sized for both plus the usual padding. Buttons flow like words: each tries the
            // current line and wraps to the next when it will not fit, so a narrow window
            // stacks the row instead of overlapping it.
            var width = IconButtonWidth(icon, label);

            if (!first)
            {
                ImGui.SameLine(0f, 10f * ImGuiHelpers.GlobalScale);
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
            }

            var position = ImGui.GetCursorPos();
            if (BoldButton(id, new Vector2(width, 0f)))
                Util.OpenLink(url);

            overlays.Add((position, width, icon, iconColor, label));
            first = false;
        }

        foreach (var (position, width, icon, iconColor, label) in overlays)
        {
            DrawButtonFeedback(
                position, width, icon, iconColor,
                label, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        }
    }
}
