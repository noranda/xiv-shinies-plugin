using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Windows;

// The sync card: the master switch, the live status line, the Sync now button, and the
// "Reading from:" read-status panel. One part of the MainWindow class — see MainWindow.cs
// for the class doc, the window state, and the shared card system and widget bindings.
internal sealed partial class MainWindow
{
    /// <summary>Everything sync in one card: the master switch, current status, manual trigger.</summary>
    /// <param name="rows">
    /// This frame's category rows, forwarded to the read-status panel at the bottom of the card (see
    /// <see cref="DrawStatus"/>). Passed in rather than rebuilt so the settings screen builds them
    /// exactly once per frame — see <see cref="DrawSettings"/>.
    /// </param>
    private void DrawSyncCard(IReadOnlyList<CategorySettingsRow> rows)
    {
        using (BrandCard())
        {
            // Custom card header: icon and title left, the master switch right-aligned on the
            // same row.
            DrawCardTitle(FontAwesomeIcon.Sync, "Sync my collections");

            // The master switch is a toggle rather than a checkbox on purpose: the category
            // checkboxes below select what to include, while this is an on/off power switch —
            // giving it a different shape keeps the two from reading as the same kind of control.
            var masterEnabled = configuration.Settings.MasterEnabled;
            var stateLabel = masterEnabled ? "ON" : "OFF";

            // Position the state label + toggle as one right-aligned cluster: the toggle's right
            // edge sits on the card's inner edge.
            var gap = 8f * ImGuiHelpers.GlobalScale;
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            Widgets.AlignRight(
                Widgets.ToggleWidth + gap + ImGui.CalcTextSize(stateLabel).X, innerRight);

            // The label echoes the switch state: teal when on, muted when off.
            if (masterEnabled)
                ImGui.TextColored(Brand.Teal, stateLabel);
            else
                ImGui.TextDisabled(stateLabel);

            ImGui.SameLine(0f, gap);
            if (Widgets.BrandToggle("##masterToggle", ref masterEnabled))
            {
                configuration.Settings.MasterEnabled = masterEnabled;
                configuration.Save();
            }

            CloseCardHeader();
            DrawStatus(rows);
        }
    }

    /// <summary>
    /// The sync card's live status: what the last upload did, and what this session can currently read.
    /// </summary>
    /// <param name="rows">
    /// This frame's category rows, which the "Reading from:" panel turns into its collection lines.
    /// </param>
    private void DrawStatus(IReadOnlyList<CategorySettingsRow> rows)
    {
        // Ordered by which fact overrides which. The master switch beats everything: while it is
        // off, reporting the last upload's outcome (with its "will try again") would be a lie — the
        // plugin will not try again until the switch comes back.
        if (!configuration.Settings.MasterEnabled)
        {
            // Red: everything below this line is inert while the switch is off, and a quiet gray
            // would read as "resting" when the truth is "doing nothing at all".
            DrawWarning("Syncing is switched off.");
        }
        else if (syncManager.BlockedPendingUserAction)
        {
            // The 403 case names the character when one is loaded, because "claim Some Name" is
            // actionable and "your token may have been revoked, or…" is a shrug. The server echoes
            // name and world for exactly this purpose; the local identity is the same information.
            var claimTarget = syncManager.LastStatus == ApiStatus.CharacterNotClaimed
                && syncManager.CharacterName is { } name
                    ? $"Claim {name} on xiv-shinies.com, then press Sync now."
                    : "Your token may have been revoked, or this character is not claimed on the " +
                      "website. Fix it there, then press Sync now.";

            DrawWarning($"Syncing has stopped. {claimTarget}");
        }
        else if (!syncManager.HasCharacter)
        {
            // Normal text color, like the colored states around it: this line IS the sync card's
            // status — the sentence the user came to read — even though it is neither good news nor
            // bad.
            //
            // This state covers two moments the plugin cannot tell apart from here: nobody is
            // logged in at all, and the few seconds right after login while the character is still
            // being identified (identity is only captured once the game reports it settled). The
            // copy speaks to both — naming the post-login delay matters most, because a player who
            // IS logged in would otherwise read "waiting for a character" as the plugin failing to
            // see them.
            ImGui.TextUnformatted("Waiting for a character — syncing starts a few seconds after you log in.");
        }
        else if (syncManager.LastStatus is { } status)
        {
            DrawLastStatus(status);
        }
        else
        {
            ImGui.TextUnformatted("Nothing has been uploaded yet this session.");
        }

        // "When?" is half of what a status line is for: without it, a deliberately quiet stretch
        // (item acquisitions fire no event) is indistinguishable from a hang. Muted, unlike the
        // status line above it: a relative timestamp is a footnote to the status, not the status.
        if (syncManager.LastSyncedAt is { } syncedAt)
            ImGui.TextDisabled($"Last synced {TimeText.Ago(DateTimeOffset.UtcNow - syncedAt)}.");

        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));

        // Primary: within this card, syncing now is the action everything above leads to. Clicking
        // gives the same in-button feedback as the copy button, but tied to reality: the label
        // yields to "Syncing" while the upload is actually on the wire, with a short minimum so
        // even an instant sync visibly reacts. The `###` suffix keeps the button's identity stable
        // while its visible label changes.
        var syncWidth = PaddedButtonWidth("Sync now");
        var showSyncing = DateTime.UtcNow < syncFeedbackUntil || syncManager.UploadInFlight;
        var syncButtonPos = ImGui.GetCursorPos();

        if (PrimaryButton(showSyncing ? "###syncNow" : "Sync now###syncNow", new Vector2(syncWidth, 0f))
            && !showSyncing)
        {
            syncManager.RequestManualSync();
            syncFeedbackUntil = DateTime.UtcNow.AddSeconds(1.5);
        }

        if (showSyncing)
        {
            // Dark-on-teal like the button's own label; the glyph matches the card's sync icon.
            DrawButtonFeedback(
                syncButtonPos, syncWidth, FontAwesomeIcon.Sync, Brand.TealForeground,
                "Syncing", Brand.TealForeground);
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Both blocks below describe a pipeline that is actually running, so both are hidden when it
        // is not: while the master switch is off (everything in this card is inert), and while the
        // sync is halted for something only the user can fix (a bad token, an unclaimed character).
        // In the halted state the status line above is already telling them what to do, and a
        // cheerful cadence promise beneath it would simply be false. The source notes hide for a
        // second reason too: they carry a red "not scanned yet" tone, and a halted card is already
        // red — leaving them on would flatten "your sync is broken" and "one container is empty"
        // into the same alarm.
        var pipelineRunning =
            configuration.Settings.MasterEnabled && !syncManager.BlockedPendingUserAction;

        // Sets the expectation for every collection at once, so no category's own description has
        // to explain the sync mechanism. Phrased by mechanism, not by category name: unlock-style
        // acquisitions announce themselves and upload within seconds, while anything the game fires
        // no event for (item possession, for instance) waits for the scheduled sweep. The cadence
        // is the live value — the server tunes it — never a hardcoded number.
        if (pipelineRunning)
        {
            DrawWrapped(
                "New unlocks upload within seconds. Everything else syncs automatically every " +
                $"{TimeText.Interval(syncManager.FullSyncInterval)} — press Sync now to update " +
                "immediately.",
                ImGuiCol.Text);
        }

        // A snapshot of everything this sync reads: each collection the user has switched on (was it
        // readable this pass, or is the game withholding it?), and the physical storage containers
        // the item counts come from (inventory, saddlebag, armoire, and so on). This lives in the
        // sync card rather than beneath any one category's row, because it describes the pipeline as
        // a whole — and because storage containers are not category-scoped: a future collection that
        // also reads items would draw from this exact same set of containers.
        //
        // Gated on a pass having actually run for this character (see SyncManager.HasCollected).
        // Before then, no collection has a skip reason yet, so every enabled one would falsely
        // report as read.
        if (pipelineRunning && syncManager.HasCollected)
        {
            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

            // Normal text color, matching the notes it introduces: this heading is what makes the
            // list beneath it mean anything, and the notes themselves are colored by tone.
            ImGui.TextUnformatted("Reading from:");

            // The whole panel is assembled by a pure, tested builder, so this window stays a printer:
            // it draws whatever notes it is handed and never asks which collection or which container
            // produced one. The rows are this frame's, built once at the top of DrawSettings and
            // shared with the consent card below.
            var readStatus = ReadStatusView.Build(rows, syncManager.LastSourceNotes);

            // Whether any note drawn below is Missing — something contributing nothing at all right
            // now, whether a collection the game will not answer for or a storage container that has
            // never been opened. Tracked while drawing so the follow-up hint beneath the panel is
            // gated on the exact tones just shown, rather than re-deriving the same answer. The two
            // groups OR their answers together: the hint speaks for both.
            var hasMissingNote = DrawReadStatusGroup("Collections", readStatus.Collections);
            hasMissingNote |= DrawReadStatusGroup("Containers", readStatus.Containers);

            // Every Missing line names its own action — open the Saddlebag, open the Achievements
            // window — but none of them can say what happens next, because acting in game changes
            // nothing until the plugin reads again. This is the shared other half: the sync that
            // actually picks the change up. Hidden once nothing is Missing, since a permanently
            // Cached container has nothing left the user can do about it.
            if (hasMissingNote)
            {
                ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
                DrawWrapped(
                    "Anything above that has not been read yet names the action that fixes it — do " +
                    "it in game, then press Sync now.",
                    ImGuiCol.Text);
            }
        }
    }

    /// <summary>
    /// Draws one labelled group of the "Reading from:" panel — its heading, its healthy sources as a
    /// wrapped row of chips, then its full-line notes — and reports whether any note in it was
    /// <see cref="SourceTone.Missing"/>. An empty group draws nothing at all, heading included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heading is what tells the reader which KIND of thing the notes beneath it are — see
    /// <see cref="ReadStatus"/> for the two questions the groups answer. Without the labels the two read
    /// as one undifferentiated list of nouns. The label is a parameter rather than a branch on the group
    /// — this method never learns which of the two it is drawing, exactly as it never learns which
    /// collection or container any individual note came from. Drawn muted via
    /// <see cref="DrawSectionLabel"/>, unlike the "Reading from:" heading above it: these headings are
    /// captions naming the notes beneath them, and the tone-colored notes are what the reader is meant
    /// to actually read.
    /// </para>
    /// <para>
    /// The group is exception-first: a healthy source (Live or Cached) has nothing the user must do,
    /// so it compresses into a small chip — its tone's icon and color, the note's short label, and the
    /// note's optional detail on hover — while a Missing note keeps a full line, because its text names
    /// a required in-game action and hover must never be the only way to see one (see
    /// <see cref="SourceNote"/> for the rule). The chips draw first, as one row that wraps at the
    /// card's inner edge; the lines follow. Both decisions switch on the TONE alone, never on a source
    /// key or category name, which is what keeps a future source renderable without touching this
    /// method.
    /// </para>
    /// </remarks>
    /// <param name="label">The group's heading.</param>
    /// <param name="notes">The group's notes, already written and toned by the pure builder.</param>
    /// <returns>True when at least one note carries the <see cref="SourceTone.Missing"/> tone.</returns>
    private bool DrawReadStatusGroup(string label, IReadOnlyList<SourceNote> notes)
    {
        // A group with nothing in it (no collection switched on; no item pass yet) skips its heading
        // too — an empty labelled section would only ask the reader what is supposed to be there.
        if (notes.Count == 0)
            return false;

        ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
        DrawSectionLabel(label);

        // A Missing note renders as a full line ONLY while it actually carries one; the builders
        // guarantee it always does (see SourceNote), and a note that somehow arrived without a text
        // degrades to a chip rather than being dropped or crashing the draw.
        static bool IsLineForm(SourceNote note) =>
            note.Tone is SourceTone.Missing && note.Text is not null;

        // --- The chip row -----------------------------------------------------------------------
        // All healthy notes, side by side, wrapping at the card's inner edge. The wrap decision has
        // to happen before each chip is drawn (a drawn chip has already reserved its footprint), so
        // the row is laid out from measured widths in cursor space — the same coordinate space
        // activeCardInnerRight arrives in (see DrawSectionLabel for the two-space distinction).
        var innerRight = activeCardInnerRight
            ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        var chipGap = 6f * ImGuiHelpers.GlobalScale;

        var anyChipDrawn = false;
        var rowRight = 0f; // cursor-space right edge of the last chip drawn

        foreach (var note in notes)
        {
            if (IsLineForm(note))
                continue;

            var icon = ToneIcon(note.Tone);
            var width = ChipWidth(icon, note.Label);

            // DrawChip ends each chip's row (its footprint Dummy advances the cursor to the next
            // line), so continuing the row is the explicit act: SameLine only when this chip still
            // fits before the card's inner edge. When it does not, the cursor is already sitting at
            // the start of a fresh line and the row wraps — with a little vertical air first,
            // because back-to-back rows of outlined chips sit flush otherwise. The gap is tighter
            // than the horizontal chipGap: rows read as one group, not as separate sections.
            if (anyChipDrawn && rowRight + chipGap + width <= innerRight)
            {
                ImGui.SameLine(0f, chipGap);
            }
            else if (anyChipDrawn)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (4f * ImGuiHelpers.GlobalScale));
            }

            var chipStart = ImGui.GetCursorPosX();
            DrawChip(icon, note.Label, ToneColor(note.Tone));
            rowRight = chipStart + width;
            anyChipDrawn = true;

            // The optional hover copy: what the source covers, or an optional refresh action. Read
            // right after the chip, whose reserved footprint is the item IsItemHovered answers for.
            if (note.Detail is { } detail && ImGui.IsItemHovered())
                Widgets.DrawTooltip(detail);
        }

        // --- The full lines ---------------------------------------------------------------------
        var hasMissingNote = false;

        foreach (var note in notes)
        {
            // Cached does not count as missing (see SourceNoteText for why Cached is a healthy resting
            // state rather than a gap): gating the caller's follow-up hint on it too would make that
            // hint permanent noise that never clears. Missing is different — it means the source is
            // contributing nothing at all, and every Missing note names a real, one-time in-game action
            // that changes that.
            if (note.Tone is SourceTone.Missing)
                hasMissingNote = true;

            if (!IsLineForm(note) || note.Text is not { } text)
                continue;

            // Drawn at Widgets.InlineIconScale, like every other inline status icon (see the constant).
            DrawIconedText(ToneIcon(note.Tone), ToneColor(note.Tone), text, Widgets.InlineIconScale);
        }

        return hasMissingNote;
    }

    /// <summary>
    /// Maps a source note's tone to the color it draws in. A switch on the <em>tone</em>, never on a
    /// source key or scan-state string — the tones are the only vocabulary this method knows, so a
    /// future storage source colors correctly the moment <see cref="SourceNoteText.Describe"/>
    /// assigns it one of them, with no change needed here.
    /// </summary>
    private static Vector4 ToneColor(SourceTone tone) =>
        tone switch
        {
            SourceTone.Live => Widgets.SuccessColor,
            SourceTone.Cached => Widgets.CautionColor,
            SourceTone.Missing => Widgets.ErrorColor,

            // Muted on purpose: an unreadable source is information, not a problem to solve, and
            // any alarm color would read as one.
            SourceTone.Unreadable => ImGuiColors.DalamudGrey,

            // Keeps the expression exhaustive against a future tone without throwing at draw time.
            _ => ImGuiColors.DalamudGrey,
        };

    /// <summary>
    /// Maps a source note's tone to the icon drawn beside it — the same switch-on-tone discipline as
    /// <see cref="ToneColor"/>, and for the reason given there.
    /// </summary>
    private static FontAwesomeIcon ToneIcon(SourceTone tone) =>
        tone switch
        {
            SourceTone.Live => FontAwesomeIcon.CheckCircle,
            SourceTone.Cached => FontAwesomeIcon.Clock,
            SourceTone.Missing => FontAwesomeIcon.ExclamationCircle,

            // A struck-through eye: the game itself cannot see this source. Paired with the muted
            // color above, and the chip's hover explains why.
            SourceTone.Unreadable => FontAwesomeIcon.EyeSlash,

            // Mirrors ToneColor's fallback arm: keeps the expression exhaustive against a future
            // tone without throwing at draw time.
            _ => FontAwesomeIcon.QuestionCircle,
        };

    /// <summary>Renders the last upload's outcome. Switches on a status, never on a category.</summary>
    private void DrawLastStatus(ApiStatus status)
    {
        switch (status)
        {
            case ApiStatus.Ok:
                ImGui.TextColored(Widgets.SuccessColor, "Your collections are up to date.");
                break;

            case ApiStatus.CharacterNotClaimed:
                DrawWarning(
                    syncManager.CharacterName is { } name
                        ? $"Claim {name} on xiv-shinies.com before it can sync."
                        : "Claim this character on xiv-shinies.com before it can sync.");
                break;

            case ApiStatus.InvalidToken:
                DrawWarning("Your token was rejected. Generate a new one.");
                break;

            // The three self-healing outcomes below need no action from the user, so they carry no
            // warning color — but they are still the sync card's status line, so they draw at full
            // contrast. Only red says "you have to do something".
            case ApiStatus.RateLimited:
            case ApiStatus.SyncDisabled:
                ImGui.TextUnformatted("Waiting before the next upload, as the server asked.");
                break;

            case ApiStatus.NetworkError:
                ImGui.TextUnformatted("Could not reach xiv-shinies.com. Will try again.");
                break;

            default:
                ImGui.TextUnformatted("The last upload did not succeed. Will try again.");
                break;
        }
    }
}
