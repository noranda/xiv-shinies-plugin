using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The settings half of the extensibility gate. The payload half is covered by CollectorRunnerTests.
// Together they enforce the project rule that adding a collection is one new ICollector class: if the
// settings window had a name-to-label table, or special-cased any category, "facewear" below would
// not survive the trip.
public class CategorySettingsViewTests
{
    // A category this plugin has never heard of, deliberately.
    private const string UnknownCategory = "facewear";

    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey, string displayName, string whatGetsSent)
        {
            CategoryKey = categoryKey;
            DisplayName = displayName;
            WhatGetsSent = whatGetsSent;
        }

        public string CategoryKey { get; }

        public string DisplayName { get; }

        public string WhatGetsSent { get; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    private static ICollector Fake(string key) =>
        new FakeCollector(key, $"{key} display", $"what {key} sends");

    private static PluginSettings OptedIn(params string[] categoryKeys)
    {
        var settings = new PluginSettings {MasterEnabled = true, OnboardingComplete = true};
        foreach (var key in categoryKeys)
            settings.SetCategoryEnabled(key, true);

        return settings;
    }

    private static ConfigResponse RemoteConfig(Dictionary<string, bool>? categories = null) => new()
    {
        Categories = categories ?? new Dictionary<string, bool>(),
        Enabled = true,
        Intervals = new ConfigIntervals {FullSyncMinutes = 30, UnlockDebounceSeconds = 5},
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
    };

    // THE GATE: a collector for an unknown category renders with its own copy, untouched.
    [Fact]
    public void A_collector_for_an_unknown_category_appears_in_the_settings_list()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        var row = Assert.Single(rows);
        Assert.Equal(UnknownCategory, row.Key);
        Assert.Equal("facewear display", row.DisplayName);
        Assert.Equal("what facewear sends", row.WhatGetsSent);
        Assert.True(row.UserEnabled);
    }

    [Fact]
    public void Rows_follow_registration_order()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake("b"), Fake("a")}, OptedIn(), RemoteConfig());

        Assert.Equal(new[] {"b", "a"}, rows.Select(row => row.Key));
    }

    // A fresh install has opted into nothing.
    [Fact]
    public void A_category_the_user_never_opted_into_is_off()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn(), RemoteConfig());

        Assert.False(rows[0].UserEnabled);
        Assert.False(rows[0].IsEffectivelyOn);
    }

    // The server's per-category kill switch. The user's own preference is preserved beneath it, so
    // flipping the switch back on restores what they chose rather than silently opting them out.
    [Fact]
    public void A_category_the_server_disabled_keeps_the_users_preference_but_is_not_effectively_on()
    {
        var config = RemoteConfig(new Dictionary<string, bool> {["a"] = false});

        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), config);

        Assert.True(rows[0].UserEnabled);
        Assert.False(rows[0].ServerEnabled);
        Assert.False(rows[0].IsEffectivelyOn);
    }

    // A config we could not fetch forbids nothing. Showing every category as server-disabled would be
    // a lie, and would match neither the collectors nor the upload gate.
    [Fact]
    public void Without_a_config_every_category_reads_as_server_enabled()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), remoteConfig: null);

        Assert.True(rows[0].ServerEnabled);
        Assert.True(rows[0].IsEffectivelyOn);
    }

    // A category the server has never heard of reads as enabled, so a plugin can ship a collector
    // before the server grows the matching switch.
    [Fact]
    public void A_category_the_server_does_not_know_reads_as_server_enabled()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(rows[0].ServerEnabled);
    }

    // How the "open your Achievements window once" hint reaches the UI without anyone naming
    // achievements. The collector reported a reason; the row carries it verbatim.
    [Fact]
    public void A_skip_reason_from_the_last_pass_rides_along_with_its_category()
    {
        var skipped = new Dictionary<string, string> {[UnknownCategory] = "list_not_loaded"};

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory), Fake("a")}, OptedIn(), RemoteConfig(), skipped);

        Assert.Equal("list_not_loaded", rows[0].SkipReason);
        Assert.Null(rows[1].SkipReason);
    }

    [Fact]
    public void Before_the_first_collection_pass_no_row_carries_a_skip_reason()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), RemoteConfig());

        Assert.Null(rows[0].SkipReason);
    }

    [Fact]
    public void No_collectors_produces_no_rows()
    {
        Assert.Empty(CategorySettingsView.Build(
            Array.Empty<ICollector>(), OptedIn(), RemoteConfig()));
    }
}
