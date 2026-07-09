using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// What one collection pass cost the frame it ran in, ready to be reported.
/// </summary>
/// <remarks>
/// <para>
/// Collection runs on the game's framework thread, which has roughly 16.7ms to draw a frame at 60fps.
/// Every collector spends part of that, and this plugin is expected to grow more of them — so the
/// cost is measured and surfaced rather than assumed. A contributor who adds an expensive collector
/// sees its price the first time a sweep runs.
/// </para>
/// <para>
/// Pure, and separate from the orchestrator that logs it, for the usual reason: the orchestrator
/// holds Dalamud services and cannot be constructed outside the game, so anything left inside it is
/// untestable. The ordering and the budget threshold are both easy to get backwards, which is exactly
/// why they live somewhere a test can reach them.
/// </para>
/// </remarks>
public sealed record CollectionCost
{
    /// <summary>
    /// The cost above which a pass is a problem rather than a curiosity.
    /// </summary>
    /// <remarks>
    /// A 60fps frame is about 16.7ms, and the game itself needs most of it. 8ms is therefore already
    /// half the budget and past the point a player could feel the hitch. Deliberately not
    /// user-configurable: it is a developer-facing alarm, not a preference.
    /// </remarks>
    public static readonly TimeSpan FrameBudgetWarningThreshold = TimeSpan.FromMilliseconds(8);

    /// <summary>How long every collector in the pass took, together.</summary>
    public required TimeSpan Total { get; init; }

    /// <summary>True when the pass is slow enough to risk a visible stutter.</summary>
    public required bool OverBudget { get; init; }

    /// <summary>The per-collector breakdown, slowest first: <c>"quests 1.2ms, mounts 0.3ms"</c>.</summary>
    public required string Breakdown { get; init; }

    /// <summary>True when no collector ran, so there is nothing worth reporting.</summary>
    public required bool IsEmpty { get; init; }

    /// <summary>Summarizes what a collection pass cost.</summary>
    /// <remarks>
    /// Note this reads whatever category keys the collectors reported. It never asks for a category
    /// by name, and never orders by one — the ordering is by duration, so the name that appears first
    /// is the one worth acting on.
    /// </remarks>
    public static CollectionCost From(CollectionSnapshot snapshot)
    {
        var durations = snapshot.Durations;

        if (durations.Count == 0)
        {
            return new CollectionCost
            {
                Total = TimeSpan.Zero,
                OverBudget = false,
                Breakdown = string.Empty,
                IsEmpty = true,
            };
        }

        var total = TimeSpan.Zero;
        foreach (var duration in durations.Values)
            total += duration;

        var measured = new List<KeyValuePair<string, TimeSpan>>(durations);

        // A comparison delegate, like `arr.sort((a, b) => ...)` in JavaScript. Note the reversed
        // operands: `b.CompareTo(a)` sorts DESCENDING, where the usual `a.CompareTo(b)` sorts
        // ascending. Slowest first is the whole point — the first name in the line is the culprit.
        measured.Sort((a, b) => b.Value.CompareTo(a.Value));

        var parts = new List<string>(measured.Count);
        foreach (var (category, duration) in measured)
            parts.Add($"{category} {duration.TotalMilliseconds:F1}ms");

        return new CollectionCost
        {
            Total = total,
            OverBudget = total >= FrameBudgetWarningThreshold,
            Breakdown = string.Join(", ", parts),
            IsEmpty = false,
        };
    }
}
