using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The one-time group migration carries a user's existing consent onto the server's legacy groups, and it
// has to know whose consent that is. Asking the collectors — rather than naming the category — is what
// keeps the sync orchestrator free of category names, which the extensibility contract requires.
public class ManifestConsentTests
{
    // A category this plugin has never heard of — the gate below rides on it.
    private const string UnknownCategory = "facewear";

    // The Dalamud-free stand-in for a real collector: it answers the same self-description questions (its
    // key, and whether it is manifest-driven) without loading a game assembly.
    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey, bool usesItemManifest)
        {
            CategoryKey = categoryKey;
            UsesItemManifest = usesItemManifest;
        }

        public string CategoryKey { get; }

        public string DisplayName => $"{CategoryKey} display";

        public string WhatGetsSent => $"what {CategoryKey} sends";

        public bool UsesItemManifest { get; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] { 1 });
    }

    [Fact]
    public void A_manifest_driven_category_the_user_opted_into_is_found()
    {
        var settings = new PluginSettings();
        settings.SetCategoryEnabled("items", true);

        var collectors = new ICollector[]
        {
            new FakeCollector("quests", usesItemManifest: false),
            new FakeCollector("items", usesItemManifest: true),
        };

        Assert.True(ManifestConsent.AnyManifestCategoryEnabled(collectors, settings));
    }

    // A collection that is not driven by the manifest has no groups to migrate consent onto, so its
    // opt-in says nothing about the item groups.
    [Fact]
    public void A_category_that_is_not_manifest_driven_does_not_count()
    {
        var settings = new PluginSettings();
        settings.SetCategoryEnabled("quests", true);

        var collectors = new ICollector[]
        {
            new FakeCollector("quests", usesItemManifest: false),
            new FakeCollector("items", usesItemManifest: true),
        };

        Assert.False(ManifestConsent.AnyManifestCategoryEnabled(collectors, settings));
    }

    // A manifest-driven category the user never opted into gives no consent to carry over.
    [Fact]
    public void A_manifest_driven_category_the_user_left_off_gives_nothing_to_carry_over()
    {
        var collectors = new ICollector[] { new FakeCollector("items", usesItemManifest: true) };

        Assert.False(ManifestConsent.AnyManifestCategoryEnabled(collectors, new PluginSettings()));
    }

    // THE GATE: a manifest-driven collection this plugin has never heard of answers for its own consent,
    // exactly as the shipped one does. A category-name check in the orchestrator would fail this test,
    // which is the whole point of it.
    [Fact]
    public void An_unknown_manifest_driven_category_answers_for_its_own_consent()
    {
        var settings = new PluginSettings();
        settings.SetCategoryEnabled(UnknownCategory, true);

        var collectors = new ICollector[] { new FakeCollector(UnknownCategory, usesItemManifest: true) };

        Assert.True(ManifestConsent.AnyManifestCategoryEnabled(collectors, settings));
    }

    [Fact]
    public void No_collectors_at_all_means_no_consent_to_carry_over()
    {
        Assert.False(ManifestConsent.AnyManifestCategoryEnabled(
            Array.Empty<ICollector>(), new PluginSettings()));
    }

    // --- The category/group agreement rules -------------------------------------------------
    // A category and its groups cannot send anything without each other, so the two checkboxes are
    // written together. These rules decide what the user actually consented to, which is why they live
    // here and not in the draw code.

    private static ItemGroupRow GroupRow(string key, bool enabled = false) =>
        new() { Key = key, Label = $"{key} label", Enabled = enabled, IsNew = false };

    private static CategorySettingsRow Row(
        string key,
        IReadOnlyList<ItemGroupRow>? groups = null,
        bool userEnabled = false,
        bool serverEnabled = true) => new()
    {
        Key = key,
        DisplayName = $"{key} display",
        WhatGetsSent = $"what {key} sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        UsesItemManifest = groups is not null,
        Groups = groups,
    };

    // Ticking a category ticks the groups beneath it: those groups ARE what the category sends, and a
    // ticked category with every group off would upload nothing at all.
    [Fact]
    public void Ticking_a_category_ticks_every_group_the_user_can_see_under_it()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });

        ManifestConsent.SetRowConsent(row, enabled: true, settings);

        Assert.True(settings.IsCategoryEnabled("items"));
        Assert.True(settings.IsItemGroupEnabled("proofs"));
        Assert.True(settings.IsItemGroupEnabled("materials"));
    }

    [Fact]
    public void Unticking_a_category_unticks_its_groups_with_it()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });
        ManifestConsent.SetRowConsent(row, enabled: true, settings);

        ManifestConsent.SetRowConsent(row, enabled: false, settings);

        Assert.False(settings.IsCategoryEnabled("items"));
        Assert.Empty(settings.EnabledItemGroupKeys);
    }

    // Only the groups the user is looking at are written. A group that is not on the row — one the server
    // added after this list was built — is left exactly as it was, neither granted nor revoked.
    [Fact]
    public void Only_the_groups_on_the_row_are_ever_written()
    {
        var settings = new PluginSettings();
        settings.SetItemGroupEnabled("some-other-collections-group", true);

        var row = Row("items", new[] { GroupRow("proofs") });

        ManifestConsent.SetRowConsent(row, enabled: true, settings);
        Assert.True(settings.IsItemGroupEnabled("proofs"));

        ManifestConsent.SetRowConsent(row, enabled: false, settings);
        Assert.False(settings.IsItemGroupEnabled("proofs"));

        // Untouched throughout: it was never on the row.
        Assert.True(settings.IsItemGroupEnabled("some-other-collections-group"));
    }

    // The ordinary collection: no manifest, no groups, just a checkbox. Writing its consent must not
    // invent group keys for it.
    [Fact]
    public void A_category_with_no_groups_writes_only_itself()
    {
        var settings = new PluginSettings();

        ManifestConsent.SetRowConsent(Row("quests"), enabled: true, settings);

        Assert.True(settings.IsCategoryEnabled("quests"));
        Assert.Empty(settings.EnabledItemGroupKeys);
    }

    // Turning a group ON turns its category on with it: a group's ids are only ever scanned as part of
    // its category's pass, so a group without its category is a consent that could never be honored.
    [Fact]
    public void Ticking_a_group_ticks_its_category()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });

        ManifestConsent.SetGroupConsent(row, "proofs", enabled: true, settings);

        Assert.True(settings.IsCategoryEnabled("items"));
        Assert.True(settings.IsItemGroupEnabled("proofs"));
        Assert.False(settings.IsItemGroupEnabled("materials"));
    }

    // Turning the LAST group off turns the category off. This is what makes "category on, every group
    // off" — a collection that presents itself as enabled and uploads nothing — unreachable by hand.
    [Fact]
    public void Unticking_the_last_group_unticks_its_category()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs", enabled: true) }, userEnabled: true);
        settings.SetCategoryEnabled("items", true);
        settings.SetItemGroupEnabled("proofs", true);

        ManifestConsent.SetGroupConsent(row, "proofs", enabled: false, settings);

        Assert.False(settings.IsCategoryEnabled("items"));
    }

    // Unticking one of several leaves the category on: it still has a group to scan, and narrowing the
    // selection group by group is exactly what these checkboxes are for.
    [Fact]
    public void Unticking_one_of_several_groups_leaves_the_category_on()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });
        ManifestConsent.SetRowConsent(row, enabled: true, settings);

        ManifestConsent.SetGroupConsent(row, "materials", enabled: false, settings);

        Assert.True(settings.IsCategoryEnabled("items"));
        Assert.True(settings.IsItemGroupEnabled("proofs"));
        Assert.False(settings.IsItemGroupEnabled("materials"));
    }

    // --- The "all collections" box ----------------------------------------------------------

    [Fact]
    public void All_collections_reads_as_ticked_only_when_every_row_and_group_is_on()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: true),
            Row("items", new[] { GroupRow("proofs", enabled: true) }, userEnabled: true),
        };

        Assert.True(ManifestConsent.AllConsentGiven(rows));
    }

    // The state of every fresh install: the collections are there and the user has ticked none of them.
    [Fact]
    public void All_collections_reads_as_unticked_while_a_category_is_off()
    {
        var rows = new[] { Row("quests", userEnabled: false), Row("mounts", userEnabled: true) };

        Assert.False(ManifestConsent.AllConsentGiven(rows));
    }

    // A category whose groups are all off uploads nothing, so a box promising "all collections" must not
    // read as ticked while one of them is off.
    [Fact]
    public void All_collections_reads_as_unticked_while_a_group_is_off()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: true),
            Row("items", new[] { GroupRow("proofs", enabled: true), GroupRow("materials") },
                userEnabled: true),
        };

        Assert.False(ManifestConsent.AllConsentGiven(rows));
    }

    // A box seeded true and then shown nothing would render ticked while nothing whatsoever is on — the
    // one reading a consent control must never give.
    [Fact]
    public void All_collections_never_reads_as_ticked_when_there_is_nothing_to_tick()
    {
        Assert.False(ManifestConsent.AllConsentGiven(Array.Empty<CategorySettingsRow>()));

        var serverDisabledOnly = new[] { Row("quests", userEnabled: false, serverEnabled: false) };
        Assert.False(ManifestConsent.AllConsentGiven(serverDisabledOnly));
    }

    // A row the server switched off is not the user's to answer for, and its own control is drawn greyed
    // out — so it neither holds the box unticked nor gets written by it.
    [Fact]
    public void A_server_disabled_row_does_not_hold_the_all_collections_box_unticked()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: true),
            Row("items", new[] { GroupRow("proofs") }, userEnabled: false, serverEnabled: false),
        };

        Assert.True(ManifestConsent.AllConsentGiven(rows));
    }

    // --- "The server asked, and the user has not agreed to answer" --------------------------
    // The rule that decides whether a manifest-driven collection is scanned at all. It lives here, and
    // not in the collector that acts on it, because that collector reads game memory and cannot be
    // constructed outside the game — while this decision is pure, and shipping it wrong would mean the
    // plugin quietly telling the user it read a collection it never looked at.

    private static CollectContext ContextWith(
        IReadOnlyList<ItemManifestGroup>? groups, params string[] enabledGroupKeys) =>
        new()
        {
            RemoteConfig = new ConfigResponse
            {
                Categories = new Dictionary<string, bool>(),
                Enabled = true,
                Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
                ItemManifest = Array.Empty<uint>(),
                ItemManifestGroups = groups,
                ManifestVersion = "abc123",
            },
            EnabledItemGroupKeys = new HashSet<string>(enabledGroupKeys),
        };

    private static ItemManifestGroup Group(string key) =>
        new() { Key = key, Label = $"{key} label", Ids = new uint[] { 1, 2 } };

    [Fact]
    public void Groups_offered_and_none_enabled_means_there_is_nothing_to_scan()
    {
        var context = ContextWith(new[] { Group("proofs"), Group("materials") });

        Assert.True(ManifestConsent.GroupsOfferedButNoneEnabled(context));
    }

    [Fact]
    public void One_enabled_group_is_enough_to_scan()
    {
        var context = ContextWith(new[] { Group("proofs"), Group("materials") }, "materials");

        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(context));
    }

    // A server that offers no groups at all gates the manifest on the category checkbox alone, so there
    // is no group consent to be missing. Distinct from the case above: nothing was asked of the user.
    [Fact]
    public void A_server_that_offers_no_groups_asks_nothing_of_the_user()
    {
        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(ContextWith(groups: null)));
        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(
            ContextWith(Array.Empty<ItemManifestGroup>())));
    }

    // No config means the plugin does not yet know what to look for, which is a different miss with its
    // own line in the settings window (CollectSkipReasons.NoRemoteConfig).
    [Fact]
    public void No_config_at_all_is_not_a_consent_problem()
    {
        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(new CollectContext()));
    }
}
