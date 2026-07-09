using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The extensibility gate. Everything here uses a FAKE collector announcing a category this plugin
// has never heard of ("facewear"). If any of these tests need changing to add a real collection,
// the runner has grown a category-name branch and the contract is broken.
public class CollectorRunnerTests
{
    private const string UnknownCategory = "facewear";

    // A collector is just a category key plus a Collect() that returns facts or a skip reason.
    // It touches no game service, which is exactly why the runner is unit-testable.
    private sealed class FakeCollector : ICollector
    {
        private readonly Func<CollectResult> collect;

        public FakeCollector(string categoryKey, Func<CollectResult> collect)
        {
            CategoryKey = categoryKey;
            this.collect = collect;
        }

        public string CategoryKey { get; }

        public int CollectCallCount { get; private set; }

        public CollectContext? LastContext { get; private set; }

        public CollectResult Collect(CollectContext context)
        {
            CollectCallCount++;
            LastContext = context;
            return collect();
        }
    }

    private static FakeCollector Collecting(string key, params uint[] ids) =>
        new(key, () => CollectResult.Ids(ids));

    private static PluginSettings OptedIn(params string[] categoryKeys)
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };
        foreach (var key in categoryKeys)
            settings.SetCategoryEnabled(key, true);

        return settings;
    }

    private static ConfigResponse RemoteConfig(bool enabled = true, Dictionary<string, bool>? categories = null) => new()
    {
        Categories = categories ?? new Dictionary<string, bool>(),
        Enabled = enabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
    };

    // THE extensibility gate: a category nobody wrote code for flows straight through.
    [Fact]
    public void A_collector_for_an_unknown_category_flows_through_untouched()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal(42u, snapshot.Collections[UnknownCategory].AsArray()[0]!.GetValue<uint>());
        Assert.Empty(snapshot.Skipped);
    }

    [Fact]
    public void A_category_the_user_never_opted_into_is_omitted()
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, settings, RemoteConfig());

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal(CollectSkipReasons.Disabled, snapshot.Skipped[UnknownCategory]);
    }

    [Fact]
    public void The_master_switch_off_collects_nothing()
    {
        var settings = OptedIn(UnknownCategory);
        settings.MasterEnabled = false;

        var collector = Collecting(UnknownCategory, 42);
        var snapshot = CollectorRunner.Run(new[] { collector }, settings, RemoteConfig());

        Assert.Empty(snapshot.Collections);
        // Never even asked the game for the facts.
        Assert.Equal(0, collector.CollectCallCount);
    }

    [Fact]
    public void Nothing_is_collected_before_onboarding_completes()
    {
        var settings = OptedIn(UnknownCategory);
        settings.OnboardingComplete = false;

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, settings, RemoteConfig());

        Assert.Empty(snapshot.Collections);
    }

    [Fact]
    public void The_remote_global_kill_switch_collects_nothing()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) },
            OptedIn(UnknownCategory),
            RemoteConfig(enabled: false));

        Assert.Empty(snapshot.Collections);
    }

    [Fact]
    public void A_remote_per_category_kill_switch_omits_only_that_category()
    {
        var categories = new Dictionary<string, bool> { [UnknownCategory] = false, ["quests"] = true };

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42), Collecting("quests", 7) },
            OptedIn(UnknownCategory, "quests"),
            RemoteConfig(categories: categories));

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.True(snapshot.Collections.ContainsKey("quests"));
    }

    // Without a fetched config we cannot know the remote switches; the server enforces them anyway
    // (it strips disabled categories and answers 503 when the global switch is off).
    [Fact]
    public void A_missing_remote_config_falls_back_to_the_local_toggles()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), remoteConfig: null);

        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
    }

    // A collector that could not read its source omits the key and explains why. The UI surfaces
    // the reason; the runner never interprets it.
    [Fact]
    public void A_collector_that_skips_omits_its_key_and_reports_the_reason()
    {
        var collector = new FakeCollector(
            UnknownCategory, () => CollectResult.Skipped("list_not_loaded"));

        var snapshot = CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal("list_not_loaded", snapshot.Skipped[UnknownCategory]);
    }

    // "Read it, and it was empty" is a legitimate fact and must survive as [] — distinct from
    // "could not read it", which is absence. This is the monotonic-write rule at the source.
    [Fact]
    public void An_empty_but_readable_category_is_sent_as_an_empty_array()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Empty(snapshot.Collections[UnknownCategory].AsArray());
    }

    // A misbehaving collector must never abort the snapshot or reach the game as an exception.
    [Fact]
    public void A_collector_that_throws_is_isolated_and_the_others_still_run()
    {
        var exploding = new FakeCollector(UnknownCategory, () => throw new InvalidOperationException("boom"));
        var healthy = Collecting("quests", 7);

        var snapshot = CollectorRunner.Run(
            new ICollector[] { exploding, healthy }, OptedIn(UnknownCategory, "quests"), RemoteConfig());

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal(CollectSkipReasons.CollectorError, snapshot.Skipped[UnknownCategory]);
        Assert.True(snapshot.Collections.ContainsKey("quests"));
    }

    // Collectors that need outside data (the item manifest) get it from the context, so the runner
    // never has to ask "is this the items collector?".
    [Fact]
    public void The_context_carries_the_remote_config_and_item_manifest_to_every_collector()
    {
        var remote = RemoteConfig();
        var collector = Collecting(UnknownCategory, 1);

        CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), remote);

        Assert.Same(remote, collector.LastContext!.RemoteConfig);
        Assert.Empty(collector.LastContext.ItemManifest);
    }

    [Fact]
    public void The_item_manifest_is_empty_when_no_config_has_been_fetched()
    {
        var collector = Collecting(UnknownCategory, 1);

        CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), remoteConfig: null);

        Assert.Null(collector.LastContext!.RemoteConfig);
        Assert.Empty(collector.LastContext.ItemManifest);
    }

    [Fact]
    public void Items_facts_ride_alongside_id_lists_with_no_special_casing()
    {
        var items = new FakeCollector("items", () => CollectResult.Items(
            new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } }));

        var snapshot = CollectorRunner.Run(
            new ICollector[] { items, Collecting("quests", 7) },
            OptedIn("items", "quests"),
            RemoteConfig());

        Assert.Equal(7851u, snapshot.Collections["items"].AsArray()[0]!["id"]!.GetValue<uint>());
        Assert.Equal(7u, snapshot.Collections["quests"].AsArray()[0]!.GetValue<uint>());
    }
}
