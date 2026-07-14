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
        public FakeCollector(
            string categoryKey, string displayName, string whatGetsSent, bool usesItemManifest = false)
        {
            CategoryKey = categoryKey;
            DisplayName = displayName;
            WhatGetsSent = whatGetsSent;
            UsesItemManifest = usesItemManifest;
        }

        public string CategoryKey { get; }

        public string DisplayName { get; }

        public string WhatGetsSent { get; }

        public bool UsesItemManifest { get; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    private static ICollector Fake(string key) =>
        new FakeCollector(key, $"{key} display", $"what {key} sends");

    // A collector that announces itself as manifest-driven, the same way ItemCollector does. Used to
    // prove group rows attach via self-description rather than a check on the category's name.
    private static ICollector FakeManifestDriven(string key) =>
        new FakeCollector(key, $"{key} display", $"what {key} sends", usesItemManifest: true);

    private static PluginSettings OptedIn(params string[] categoryKeys)
    {
        var settings = new PluginSettings {MasterEnabled = true, OnboardingComplete = true};
        foreach (var key in categoryKeys)
            settings.SetCategoryEnabled(key, true);

        return settings;
    }

    private static ConfigResponse RemoteConfig(
        Dictionary<string, bool>? categories = null,
        IReadOnlyList<ItemManifestGroup>? itemManifestGroups = null) => new()
    {
        Categories = categories ?? new Dictionary<string, bool>(),
        Enabled = true,
        Intervals = new ConfigIntervals {FullSyncMinutes = 30, UnlockDebounceSeconds = 5},
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
        ItemManifestGroups = itemManifestGroups,
    };

    private static ItemManifestGroup Group(string key, string label) => new()
    {
        Key = key,
        Label = label,
        Ids = Array.Empty<uint>(),
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

    // A manifest-driven collector paired with a config that carries groups gets one row per group,
    // in the server's own order, carrying that group's own key and label.
    [Fact]
    public void A_manifest_driven_collector_gets_one_group_row_per_manifest_group_in_server_order()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("glamour-weapons", "Glamour weapons"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        // The collector's self-reported manifest flag rides through to the row. It is the seam the views
        // key their manifest-driven behavior on: ReadStatusView suppresses such a collection's
        // read-status line while a container line stands in for it.
        Assert.True(rows[0].UsesItemManifest);

        var groups = Assert.Single(rows).Groups;
        Assert.NotNull(groups);
        Assert.Equal(2, groups!.Count);
        Assert.Equal("glamour-weapons", groups[0].Key);
        Assert.Equal("Glamour weapons", groups[0].Label);
        Assert.Equal("relic-tools", groups[1].Key);
        Assert.Equal("Relic tools", groups[1].Label);
    }

    // Enabled mirrors the user's per-group opt-in (an allowlist, so an unknown group reads as off).
    // IsNew mirrors whether the settings UI has shown that key before (an unknown group reads as new).
    [Fact]
    public void Group_rows_carry_the_users_enabled_and_seen_state_per_group()
    {
        var settings = OptedIn();
        settings.SetItemGroupEnabled("glamour-weapons", true);
        settings.MarkItemGroupsSeen(new[] {"glamour-weapons"});
        // "relic-tools" is left untouched: never enabled, never marked seen.

        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("glamour-weapons", "Glamour weapons"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, settings, config);
        var groups = rows[0].Groups!;

        Assert.True(groups[0].Enabled);
        Assert.False(groups[0].IsNew);

        Assert.False(groups[1].Enabled);
        Assert.True(groups[1].IsNew);
    }

    // No groups in the config means nothing for the UI to draw, even for a collector that announces
    // itself as manifest-driven — Groups is null, not an empty list, so the window can tell "no groups
    // yet" apart from "server sent a group list with nothing in it".
    [Fact]
    public void A_manifest_driven_collector_has_no_group_rows_when_the_config_carries_no_groups()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn(), RemoteConfig());

        Assert.Null(rows[0].Groups);
    }

    // The other side of the null-vs-empty distinction: a group list that is PRESENT but empty
    // yields an empty (non-null) row list. Callers checking null get "the server sent groups",
    // even though there is nothing in them to draw.
    [Fact]
    public void An_empty_group_list_in_the_config_yields_empty_group_rows_not_null()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")},
            OptedIn(),
            RemoteConfig(itemManifestGroups: Array.Empty<ItemManifestGroup>()));

        Assert.NotNull(rows[0].Groups);
        Assert.Empty(rows[0].Groups!);
    }

    // The flag gates the feature, not the presence of groups in the config. A collector that never
    // announced itself as manifest-driven gets no group rows even when the config carries some.
    [Fact]
    public void A_collector_that_is_not_manifest_driven_never_gets_group_rows()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("glamour-weapons", "Glamour weapons")});

        var rows = CategorySettingsView.Build(new[] {Fake("items")}, OptedIn(), config);

        // Not manifest-driven: the flag is false, so no group rows attach — the server's groups belong to
        // whichever collection announced itself, and this one did not.
        Assert.False(rows[0].UsesItemManifest);
        Assert.Null(rows[0].Groups);
    }

    // A blank group key is malformed server data that no downstream path can handle: consent reads
    // treat it as off, seen-marking skips it (a forever-"New" badge that would re-save the config
    // every frame), and the consent write throws. The view drops it at the boundary; its healthy
    // siblings still flow.
    [Fact]
    public void A_group_with_a_blank_key_is_dropped_and_its_siblings_survive()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("", "Broken group"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        var group = Assert.Single(rows[0].Groups!);
        Assert.Equal("relic-tools", group.Key);
    }

    // THE GATE, extended: a manifest-driven collector for a category this plugin has never heard of
    // still gets group rows built from the config, proving group attachment is gated on the
    // self-reported flag rather than on any category name.
    [Fact]
    public void A_manifest_driven_collector_for_an_unknown_category_still_gets_group_rows()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("mystery-group", "Mystery group")});

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven(UnknownCategory)}, OptedIn(UnknownCategory), config);

        var groups = Assert.Single(rows).Groups;
        Assert.NotNull(groups);
        var group = Assert.Single(groups!);
        Assert.Equal("mystery-group", group.Key);
    }
}
