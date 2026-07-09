using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// What a collection pass cost the frame it ran in. Extracted from the orchestrator precisely because
// the two things it does — order by duration, and compare against a threshold — are both trivial to
// get backwards, and the orchestrator cannot be unit-tested.
public class CollectionCostTests
{
    private static CollectionSnapshot Snapshot(params (string Category, double Milliseconds)[] durations)
    {
        var measured = new Dictionary<string, TimeSpan>();
        foreach (var (category, milliseconds) in durations)
            measured[category] = TimeSpan.FromMilliseconds(milliseconds);

        return new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>(),
            Skipped = new Dictionary<string, string>(),
            Durations = measured,
        };
    }

    [Fact]
    public void A_pass_where_nothing_ran_has_nothing_to_report()
    {
        var cost = CollectionCost.From(Snapshot());

        Assert.True(cost.IsEmpty);
        Assert.Equal(TimeSpan.Zero, cost.Total);
        Assert.False(cost.OverBudget);
        Assert.Equal(string.Empty, cost.Breakdown);
    }

    [Fact]
    public void The_total_is_the_sum_of_every_collector()
    {
        var cost = CollectionCost.From(Snapshot(("quests", 1.5), ("mounts", 0.5)));

        Assert.Equal(TimeSpan.FromMilliseconds(2), cost.Total);
        Assert.False(cost.IsEmpty);
    }

    // Slowest first: the first name in the log line is the one worth acting on. Alphabetical order
    // would put "mounts" first here, so this test fails against the obvious wrong implementation.
    [Fact]
    public void The_breakdown_names_the_slowest_collector_first()
    {
        var cost = CollectionCost.From(Snapshot(("mounts", 0.5), ("quests", 4.0), ("minions", 2.0)));

        Assert.Equal("quests 4.0ms, minions 2.0ms, mounts 0.5ms", cost.Breakdown);
    }

    [Fact]
    public void A_cheap_pass_is_not_over_budget()
    {
        Assert.False(CollectionCost.From(Snapshot(("quests", 1.0))).OverBudget);
    }

    // The threshold is inclusive. Exactly at the budget is already too slow to ignore.
    [Fact]
    public void A_pass_exactly_at_the_threshold_is_over_budget()
    {
        var atThreshold = CollectionCost.FrameBudgetWarningThreshold.TotalMilliseconds;

        Assert.True(CollectionCost.From(Snapshot(("quests", atThreshold))).OverBudget);
    }

    // The budget is on the TOTAL, not on any single collector. Many cheap collectors can overrun a
    // frame just as surely as one expensive one — which is the whole reason this measurement exists,
    // since the plugin is expected to grow more of them.
    [Fact]
    public void Many_cheap_collectors_can_overrun_the_budget_together()
    {
        var cost = CollectionCost.From(
            Snapshot(("a", 3.0), ("b", 3.0), ("c", 3.0)));

        Assert.True(cost.OverBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(9), cost.Total);
    }

    // Half a 60fps frame (16.7ms). A regression that loosened this to, say, a whole frame would let a
    // visible stutter through unreported.
    [Fact]
    public void The_budget_threshold_is_half_a_sixty_fps_frame()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(8), CollectionCost.FrameBudgetWarningThreshold);
    }
}
