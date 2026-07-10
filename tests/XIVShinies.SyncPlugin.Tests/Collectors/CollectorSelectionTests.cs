using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Picks the collectors an upload runs. Pure: no game, no network. The category keys used here are
// deliberately ones the plugin does not ship, so a name branch sneaking into the selection logic
// would fail these tests rather than pass them by coincidence.
public class CollectorSelectionTests
{
    private const string Facewear = "facewear";
    private const string Orchestrion = "orchestrion";

    // Implements the Dalamud-free ICollector, so constructing it never loads a game assembly.
    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey) => CategoryKey = categoryKey;

        public string CategoryKey { get; }

        public string DisplayName => CategoryKey;

        public string WhatGetsSent => $"Facts about {CategoryKey}.";

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    private static IReadOnlyList<ICollector> Collectors() =>
        new ICollector[] {new FakeCollector(Facewear), new FakeCollector(Orchestrion)};

    private static string[] KeysOf(IEnumerable<ICollector> collectors) =>
        collectors.Select(collector => collector.CategoryKey).ToArray();

    // Null means "full sweep". This is the login, interval, and manual path.
    [Fact]
    public void A_null_category_set_selects_every_collector()
    {
        var selected = CollectorSelection.For(Collectors(), categories: null);

        Assert.Equal(new[] {Facewear, Orchestrion}, KeysOf(selected));
    }

    // The unlock path. Selecting one collector too many would date a collection the player did not
    // just acquire, and the server never revises an acquisition date.
    [Fact]
    public void A_category_set_selects_only_the_matching_collectors()
    {
        var selected = CollectorSelection.For(Collectors(), new HashSet<string> {Orchestrion});

        Assert.Equal(new[] {Orchestrion}, KeysOf(selected));
    }

    // A key for a collection this build no longer ships must be ignored, never throw.
    [Fact]
    public void An_unrecognized_category_matches_nothing_rather_than_throwing()
    {
        var selected = CollectorSelection.For(Collectors(), new HashSet<string> {"triple_triad"});

        Assert.Empty(selected);
    }

    // An empty set is not the same as null. Null is "everything"; empty is "nothing".
    [Fact]
    public void An_empty_category_set_selects_nothing()
    {
        Assert.Empty(CollectorSelection.For(Collectors(), new HashSet<string>()));
    }

    [Fact]
    public void Selecting_from_no_collectors_yields_nothing()
    {
        Assert.Empty(CollectorSelection.For(new List<ICollector>(), categories: null));
    }
}
