using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Windows;

// The consent surfaces: the per-category checkbox rows, their per-group checkboxes and "New"
// badges, and the select-all control — shared by the wizard's consent step and the settings
// screen's Collections section. One part of the MainWindow class — see MainWindow.cs for the
// class doc, the window state, and the shared card system and widget bindings.
internal sealed partial class MainWindow
{
    /// <summary>
    /// The settings window's category rows, from the pure builder every consent and status surface in
    /// this window reads.
    /// </summary>
    /// <remarks>
    /// A cheap list build with no game calls in it, but it still allocates — so each frame builds the
    /// list ONCE and hands it to whichever surfaces need it, rather than each surface rebuilding it
    /// for itself sixty times a second on an always-visible path.
    /// </remarks>
    private IReadOnlyList<CategorySettingsRow> BuildCategoryRows() =>
        CategorySettingsView.Build(
            collectors, configuration.Settings, syncManager.RemoteConfig, syncManager.LastSkipped);

    /// <summary>
    /// Draws one checkbox per registered collector, shared by the wizard and the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contains no category names. Every label and description comes from the row the collector
    /// produced, which is what keeps "adding a collection is one new class" true.
    /// </para>
    /// <para>
    /// This card is about <b>consent alone</b> — what the user chooses to send. Whether a chosen
    /// collection could actually be READ is a live status, and it belongs with every other live
    /// status, in the sync card's read-status panel (see <see cref="DrawStatus"/>).
    /// </para>
    /// </remarks>
    /// <param name="rows">
    /// This frame's category rows, from <see cref="BuildCategoryRows"/>. Passed in rather than rebuilt
    /// here so a settings frame builds them exactly once for all three of its consumers — see
    /// <see cref="DrawSettings"/>.
    /// </param>
    /// <param name="showNewChips">
    /// Whether a manifest group the user has not been shown before wears a "New" badge (see
    /// <see cref="DrawGroupCheckboxes"/>). The settings pass true; the wizard passes false, because
    /// in the wizard every group is being shown for the first time and a badge on all of them at
    /// once distinguishes nothing. Either way the groups drawn are marked seen, so a user arriving
    /// in the settings straight from the wizard finds no badges waiting for them.
    /// </param>
    /// <param name="headerIcon">The card header's icon, or null for a headerless card.</param>
    /// <param name="headerTitle">The card header's title, or null for a headerless card.</param>
    private void DrawCategoryRows(
        IReadOnlyList<CategorySettingsRow> rows,
        bool showNewChips,
        FontAwesomeIcon? headerIcon = null,
        string? headerTitle = null)
    {
        // ItemInnerSpacing is the gap ImGui puts between a checkbox's box and its label — wider
        // here so the labels get some air. Pushed as a style variable scoped to this card.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            // The header is optional because this card is shared: the settings screen titles it,
            // while the wizard's step already introduces the list with its own copy.
            if (headerIcon is { } icon && headerTitle is not null)
            {
                DrawCardTitle(icon, headerTitle);
                CloseCardHeader();
            }
            // A checkbox's label starts after the box itself plus the inner spacing; indenting the
            // description by the same amount lines its left edge up with the label above it.
            // Measured inside the push so the description column moves with the label.
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
            DrawSelectAll(rows);
            BrandSeparator();
            ImGui.Spacing();

            foreach (var row in rows)
            {
                var enabled = row.UserEnabled;
                bool toggled;

                // The server switched this category off for everyone. Show it, disabled, with the
                // user's own preference intact underneath — flipping it back on later restores what
                // they chose.
                using (ImRaii.Disabled(!row.ServerEnabled))
                {
                    // Everything after `##` is hidden from the label but forms part of the widget's
                    // identity. ImGui derives a control's ID from its label text, so two collections
                    // that happened to choose the same DisplayName would share an ID and cross-wire
                    // their clicks. The category key is unique by construction, which makes this
                    // collision impossible rather than merely unlikely.
                    toggled = ImGui.Checkbox($"{row.DisplayName}##{row.Key}", ref enabled);
                }

                if (toggled)
                {
                    ManifestConsent.SetRowConsent(row, enabled, configuration.Settings);
                    configuration.Save();
                }

                // The description draws at the normal text color: it is the consent copy for this
                // category — what the plugin will send if the box is ticked — so it has to be
                // comfortably legible.
                ImGui.Indent(checkboxColumn);
                DrawWrapped(row.WhatGetsSent, ImGuiCol.Text);

                // Muted: it only restates why the checkbox above it is grayed out, which the
                // disabled control already conveys on its own.
                if (!row.ServerEnabled)
                    ImGui.TextDisabled("Temporarily switched off by XIV Shinies.");

                // Disabled along with the category above them. A group belongs to its category and is
                // only ever scanned as part of that category's pass, so leaving the groups live under a
                // greyed-out parent would offer the user a consent choice that cannot mean anything —
                // and ticking one would switch its category back on behind the very control that says it
                // is off.
                using (ImRaii.Disabled(!row.ServerEnabled))
                    DrawGroupCheckboxes(row, showNewChips);

                ImGui.Unindent(checkboxColumn);
                ImGui.Spacing();
            }
        }
    }

    /// <summary>
    /// True when at least one manifest-driven category's consent group (see
    /// <see cref="CategorySettingsRow.Groups"/>) still counts as "New" — the same test
    /// <see cref="DrawGroupCheckboxes"/> uses to decide whether to draw that group's own badge.
    /// </summary>
    /// <remarks>
    /// Used to decide whether the collapsed "Collections" header itself should wear a
    /// "New" chip (see <see cref="DrawSettings"/>): with the card collapsed, none of the per-group
    /// badges beneath it are visible, so a group added since the last session would otherwise go
    /// unnoticed until the user happened to expand the card. This reads the same rows
    /// <see cref="CategorySettingsView.Build"/> already produces and the same <c>seenThisSession</c>
    /// set <see cref="DrawGroupCheckboxes"/> already maintains — no new state, and no branch on which
    /// category or group is being asked about.
    /// </remarks>
    /// <param name="rows">The category rows to scan, from <see cref="CategorySettingsView.Build"/>.</param>
    private bool AnyGroupIsNew(IReadOnlyList<CategorySettingsRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Groups is not { Count: > 0 } groups)
                continue;

            foreach (var group in groups)
            {
                // Mirrors DrawGroupCheckboxes's own badge condition exactly: never displayed by this
                // install, or shown once already this session and therefore still wearing its badge.
                if (group.IsNew || seenThisSession.Contains(group.Key))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Draws the per-group consent checkboxes beneath a manifest-driven category and persists both
    /// the toggles and the seen-once flags, optionally badging a group the user has not been shown
    /// before with a "New" chip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seen-marking is the subtle part. A group arrives from <see cref="CategorySettingsView.Build"/>
    /// with <c>IsNew = true</c> until its persisted "seen" flag is set. The first frame we draw it we set
    /// that flag (one write), so every later frame's rebuild reports <c>IsNew = false</c> and this method
    /// stops writing for that group — the config is saved once per batch of newly-seen groups, never per
    /// frame (a per-frame save would be a real bug). Marking seen happens on <b>whichever surface drew
    /// the group</b>, wizard or settings: it records that the user has been shown it, and the wizard's
    /// consent step shows it just as plainly as the settings do.
    /// </para>
    /// <para>
    /// <c>seenThisSession</c> is a separate question — "is this group's badge currently on screen?" — and
    /// only the badge-drawing surface adds to it. The badge would otherwise blink out one frame after it
    /// appeared, since the persisted flag we just set makes the very next rebuild report the group as no
    /// longer new; remembering the key keeps it drawn for the rest of the session, while the persisted
    /// flag guarantees it is gone on the next load. A group first drawn by the WIZARD never enters that
    /// set, which is what leaves the settings screen badge-free for a user who has just finished setup:
    /// they have already seen every group there is.
    /// </para>
    /// </remarks>
    /// <param name="row">The category row whose groups are being drawn.</param>
    /// <param name="showNewChips">
    /// Whether an unseen group wears a "New" badge. False in the wizard, where every group is new by
    /// definition and a chip beside each of them says nothing.
    /// </param>
    private void DrawGroupCheckboxes(CategorySettingsRow row, bool showNewChips)
    {
        // Nothing to draw unless the server sent consent groups for this manifest-driven category.
        if (row.Groups is not { Count: > 0 } groups)
            return;

        // Past this point at least one group checkbox is going on screen — the fact the wizard's Finish
        // handler settles its consent on. See PluginSettings.SettleItemGroupConsent for what rides on it.
        if (!configuration.Settings.OnboardingComplete)
            wizardShowedGroups = true;

        // A further indent nests the group checkboxes beneath their category's description. Measured
        // the same way as the category column, inside the ItemInnerSpacing push DrawCategoryRows opened,
        // so it tracks the same spacing.
        var groupIndent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.Indent(groupIndent);

        // Collected while drawing, then persisted once after the loop. Null until the first genuinely
        // new group is seen, so a row whose groups are all already-seen writes nothing.
        List<string>? newlySeen = null;

        foreach (var group in groups)
        {
            var groupEnabled = group.Enabled;

            // Same `##key` identity trick as the category checkboxes above: the visible label is the
            // group's, but the widget's ImGui id comes from the unique group key, so two groups that
            // chose the same label never cross-wire their clicks.
            if (ImGui.Checkbox($"{group.Label}##group-{group.Key}", ref groupEnabled))
            {
                // The category and its groups have to agree, because neither can send anything without
                // the other. That rule lives in ManifestConsent, where it is unit-tested and names no
                // category; this window only reports the click.
                ManifestConsent.SetGroupConsent(row, group.Key, groupEnabled, configuration.Settings);
                configuration.Save();
            }

            // Drawing a group IS showing it to the user, so it is marked seen regardless of which
            // surface drew it — the wizard's consent step shows a group just as plainly as the
            // settings screen does.
            if (group.IsNew)
                (newlySeen ??= new List<string>()).Add(group.Key);

            if (!showNewChips)
                continue;

            // Remember that this group's badge went up, so it keeps drawing for the rest of the session
            // even though the seen-marking persisted after this loop makes the next rebuild report it
            // as un-new.
            if (group.IsNew)
                seenThisSession.Add(group.Key);

            // The badge shows for a group this install has never displayed, and for one whose badge went
            // up earlier this session. It is a small outlined chip with a leading star (see DrawChip),
            // so "New" reads as a compact badge beside the checkbox rather than another line of body
            // copy; Brand.Gold is the "shiny" accent used for highlights elsewhere.
            if (group.IsNew || seenThisSession.Contains(group.Key))
            {
                ImGui.SameLine();
                DrawChip(FontAwesomeIcon.Star, "New", Brand.Gold);
            }
        }

        // Persist the seen-once flags for every group that was new this frame, in a single save. Because
        // marking them seen makes the next rebuild report IsNew=false, this runs once per batch of
        // newly-seen groups rather than every frame.
        if (newlySeen is not null)
        {
            configuration.Settings.MarkItemGroupsSeen(newlySeen);
            configuration.Save();
        }

        ImGui.Unindent(groupIndent);
    }

    /// <summary>One checkbox that flips every collection at once.</summary>
    /// <remarks>
    /// <para>
    /// Shown checked only when everything is on, so clicking it always does the obvious thing: from
    /// "all on" it turns everything off, from anything else it turns everything on. It never names a
    /// category — it iterates whatever rows exist. A row the server has switched off is left out of
    /// both the reading and the writing, itself and its groups alike: that category uploads nothing
    /// whatever the boxes say, so this control leaves its consent exactly as the user last set it,
    /// ready to mean something again if the server switches it back on.
    /// </para>
    /// <para>
    /// "Everything" includes the per-group consent checkboxes nested under a manifest-driven row (see
    /// <see cref="DrawGroupCheckboxes"/>), both in what it writes and in whether it reads as checked.
    /// A category whose groups are all off uploads nothing at all, so a control promising "all
    /// collections" that left them off would be promising something it does not deliver — and would
    /// then keep showing itself unchecked, because a group somewhere is still off. The groups stay
    /// individually toggleable afterwards; this only sets a starting point.
    /// </para>
    /// <para>
    /// This says nothing about a group that arrives LATER. A group the server adds after this click
    /// has never appeared in any list the user has looked at, so it starts off and stays off until
    /// they tick it — <see cref="PluginSettings.IsItemGroupEnabled"/> is an allowlist, and only the
    /// groups on screen are ever written here.
    /// </para>
    /// </remarks>
    private void DrawSelectAll(IReadOnlyList<CategorySettingsRow> rows)
    {
        // Whether the box reads as ticked is a rule about consent, not about drawing, so it lives in
        // ManifestConsent with the rest of them and is unit-tested there.
        var allEnabled = ManifestConsent.AllConsentGiven(rows);

        if (ImGui.Checkbox("All collections##selectAll", ref allEnabled))
        {
            foreach (var row in rows)
            {
                if (row.ServerEnabled)
                    ManifestConsent.SetRowConsent(row, allEnabled, configuration.Settings);
            }

            configuration.Save();
        }

        ImGui.Spacing();
    }
}
