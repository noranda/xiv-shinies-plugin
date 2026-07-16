using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The upload log's table body: one row per upload, newest first — when, what triggered each,
/// the outcome, and what was sent per category.
/// </summary>
/// <remarks>
/// <para>
/// Rendered entirely from <see cref="UploadLogEntry"/> data plus the collectors' own display
/// names, captured once at construction — no category-name branches, so a new collector appears
/// here for free. The strings are built per frame, which is affordable only because the log
/// section draws while expanded; do not copy this pattern into an always-visible path.
/// </para>
/// <para>
/// Wrapped text inside the table breaks at each CELL's edge — which wrap position 0 already
/// means, because tables clamp the content region per column. That is why every wrapped call
/// below passes a null wrap edge even when the table sits inside a card: the card-wide limit
/// would let one cell's text run underneath its neighbor.
/// </para>
/// </remarks>
internal sealed class UploadLogTable
{
    // Category key → the display name its collector declared. Built once: the collector list is
    // fixed for the plugin's lifetime.
    private readonly Dictionary<string, string> categoryNames = new();

    public UploadLogTable(IReadOnlyList<ICollector> collectors)
    {
        foreach (var collector in collectors)
            categoryNames[collector.CategoryKey] = collector.DisplayName;
    }

    /// <summary>Draws the table.</summary>
    /// <param name="history">The uploads to render, newest first.</param>
    /// <param name="innerRight">The enclosing card's inner right edge, where the table stops.</param>
    public void Draw(IReadOnlyList<UploadLogEntry> history, float innerRight)
    {
        // The metadata columns adapt to the room available: on a roomy table, When and Trigger
        // each render on one line; on a narrow one, the date stacks over the time and the
        // trigger stacks word by word. The stacking is explicit lines, never ImGui text
        // wrapping — a wrap would happily break inside "7/11/2026" or "login" the moment the
        // column got narrower than the word. Both candidate widths are measured from the rows
        // actually shown, so the columns are always exactly as wide as their widest line.
        // Seeded with the header labels: a column exactly as wide as "login" clips its own
        // "Trigger" heading.
        var whenOneLine = ImGui.CalcTextSize("When").X;
        var whenStacked = whenOneLine;
        var triggerOneLine = ImGui.CalcTextSize("Trigger").X;
        var triggerStacked = triggerOneLine;
        foreach (var entry in history)
        {
            // "d"/"t" are the short date and short time in the user's own locale.
            var at = entry.At.ToLocalTime();
            var date = at.ToString("d");
            var time = at.ToString("t");
            whenOneLine = Math.Max(whenOneLine, ImGui.CalcTextSize($"{date} {time}").X);
            whenStacked = Math.Max(
                whenStacked, Math.Max(ImGui.CalcTextSize(date).X, ImGui.CalcTextSize(time).X));

            var trigger = UploadLogText.TriggerText(entry.Trigger);
            triggerOneLine = Math.Max(triggerOneLine, ImGui.CalcTextSize(trigger).X);
            foreach (var word in trigger.Split(' '))
                triggerStacked = Math.Max(triggerStacked, ImGui.CalcTextSize(word).X);
        }

        // Compact when one-line metadata would leave too little room for Outcome and Sent.
        var tableWidth = innerRight - ImGui.GetCursorPosX();
        var compact =
            tableWidth < whenOneLine + triggerOneLine + (360f * ImGuiHelpers.GlobalScale);

        var whenWidth = (compact ? whenStacked : whenOneLine) + (2f * ImGuiHelpers.GlobalScale);
        var triggerWidth =
            (compact ? triggerStacked : triggerOneLine) + (2f * ImGuiHelpers.GlobalScale);

        // Roomier cells than ImGui's default: CellPadding is the gap between a cell's border and
        // its content, pushed as a style variable scoped to this table.
        using var cellPadding = ImRaii.PushStyle(
            ImGuiStyleVar.CellPadding,
            new Vector2(8f * ImGuiHelpers.GlobalScale, 6f * ImGuiHelpers.GlobalScale));

        // Fixed columns for the short facts, the stretchy remainder for per-category detail, row
        // striping for scanability. The explicit outer width stops the table at the card's inner
        // edge — left alone it would stretch to the window and punch through the card's padding.
        using var table = ImRaii.Table(
            "uploadLogTable",
            4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
            new Vector2(innerRight - ImGui.GetCursorPosX(), 0f));

        if (!table.Success)
            return;

        // Metadata columns fixed at their measured width (one-line or stacked, chosen above);
        // Outcome and Sent stretch over whatever remains. Sent gets the lion's share: Outcome's
        // usual content is one short word, and the long failure phrases can wrap — the
        // per-category counts are the dense part.
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, whenWidth);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthFixed, triggerWidth);
        ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Sent", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableHeadersRow();

        // The table's cells are data the user came here to read, so they draw in the normal text
        // color. Muted is used in exactly one place below — the "·" that separates the per-category
        // counts, which is punctuation rather than content.
        var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var muted = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

        for (var index = 0; index < history.Count; index++)
        {
            var entry = history[index];

            // Counts that differ from this category's last appearance in the log light up gold —
            // "something new arrived since the previous upload".
            var changed = UploadLogDiff.ChangedCategories(history, index);

            ImGui.TableNextRow();

            // Short date + short time in the user's own locale — one line, or the date stacked
            // over the time in compact mode. Explicit lines: neither piece can ever be broken.
            ImGui.TableNextColumn();
            var at = entry.At.ToLocalTime();
            if (compact)
            {
                ImGui.TextUnformatted(at.ToString("d"));
                ImGui.TextUnformatted(at.ToString("t"));
            }
            else
            {
                ImGui.TextUnformatted($"{at.ToString("d")} {at.ToString("t")}");
            }

            ImGui.TableNextColumn();
            var triggerText = UploadLogText.TriggerText(entry.Trigger);
            if (compact)
            {
                foreach (var word in triggerText.Split(' '))
                    ImGui.TextUnformatted(word);
            }
            else
            {
                ImGui.TextUnformatted(triggerText);
            }

            // The outcome (plus retry/attempt qualifiers) in a color that matches it: green for
            // accepted, red for anything the user must look at, and the normal text color for a
            // self-healing deferral — neither good news nor bad, but still the row's headline, so
            // it is not dimmed. A validation rejection's detail — the server saying which field it
            // hated — follows in the same normal color, so the row itself explains the failure.
            ImGui.TableNextColumn();
            var statusColor = UploadLogText.IsSuccess(entry.Status) ? Widgets.SuccessColor
                : UploadLogText.IsDeferral(entry.Status)
                    ? textColor
                    : Widgets.ErrorColor;
            Widgets.DrawWrapped(UploadLogText.OutcomeText(entry), statusColor, null);

            if (entry.Detail is { } detail)
                Widgets.DrawWrapped(detail, ImGuiCol.Text, null);

            // Mixed colors inside one flowing line need the span helper, exactly like the
            // wizard intro's gold website name. A null span color means the normal text color,
            // which is what the counts themselves use — they are the densest data in the table.
            // A changed category says so in words as well as gold: the color alone is subtle, and
            // "the count is the same but the contents differ" is invisible without it. A
            // manifest-driven category gets the server's proof answer instead of "(changed)"
            // (UploadLogCategory explains why a content diff cannot carry that signal); its note
            // is gold only when steps were proved, since "proof pending" is not good news. Only
            // the separator between categories is muted.
            ImGui.TableNextColumn();
            // The proof note comes from the entry (the server answers per upload, not per
            // category), so it is computed once out here and the same note draws beside each
            // manifest-driven category the entry carries.
            var proof = UploadLogText.ProofText(entry);
            var sent = new List<(string Text, Vector4? Color)>(entry.Categories.Count * 2);
            foreach (var category in entry.Categories)
            {
                if (sent.Count > 0)
                    sent.Add(("·", muted));

                var label = $"{DisplayNameFor(category.Key)} {category.Count:N0}";
                Vector4? color = null;

                if (category.UsesItemManifest)
                {
                    if (proof is not null)
                    {
                        label += $" ({proof})";
                        color = entry.ProvenSteps > 0 ? Brand.Gold : null;
                    }
                }
                else if (changed.Contains(category.Key))
                {
                    label += " (changed)";
                    color = Brand.Gold;
                }

                sent.Add((label, color));
            }

            Widgets.DrawWrappedSpans(sent.ToArray());

            if (entry.Skipped.Count > 0)
            {
                var skipped = new List<string>(entry.Skipped.Count);
                foreach (var key in entry.Skipped.Keys)
                    skipped.Add(DisplayNameFor(key));
                Widgets.DrawWrapped(
                    $"Could not read: {string.Join(", ", skipped)}", ImGuiCol.Text, null);
            }
        }
    }

    /// <summary>The display name a category's collector declared, or the raw key as a fallback.</summary>
    private string DisplayNameFor(string categoryKey) =>
        categoryNames.TryGetValue(categoryKey, out var name) ? name : categoryKey;
}
