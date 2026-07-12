using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests;

// PluginSettings holds every user-facing setting and is deliberately Dalamud-free, so the suite
// can construct it directly. Its persistence shell (Configuration) implements a Dalamud interface
// and therefore cannot be instantiated outside the game — that part is covered by in-game QA.
public class PluginSettingsTests
{
    [Fact]
    public void Defaults_send_nothing_until_the_user_opts_in()
    {
        var settings = new PluginSettings();

        // Explicit opt-in is a Dalamud compliance rule: a fresh install must upload nothing.
        Assert.False(settings.MasterEnabled);
        Assert.False(settings.OnboardingComplete);
        Assert.False(settings.CustomBackendAcknowledged);
        Assert.Equal(BackendUrl.Default, settings.BaseUrl);
        Assert.Equal(string.Empty, settings.Token);
    }

    [Fact]
    public void No_category_is_enabled_until_it_is_explicitly_chosen()
    {
        var settings = new PluginSettings();

        Assert.False(settings.IsCategoryEnabled("quests"));
        // An unknown key is simply "not opted in" — a future collector needs no settings migration.
        Assert.False(settings.IsCategoryEnabled("facewear"));
    }

    [Fact]
    public void Categories_are_toggled_by_key_so_new_collectors_need_no_settings_change()
    {
        var settings = new PluginSettings();

        settings.SetCategoryEnabled("quests", true);
        Assert.True(settings.IsCategoryEnabled("quests"));
        Assert.False(settings.IsCategoryEnabled("mounts"));

        settings.SetCategoryEnabled("quests", false);
        Assert.False(settings.IsCategoryEnabled("quests"));
    }

    [Fact]
    public void A_token_is_usable_only_when_it_is_well_formed()
    {
        var settings = new PluginSettings();
        Assert.False(settings.HasUsableToken());

        settings.Token = "nonsense";
        Assert.False(settings.HasUsableToken());

        settings.Token = "xvs_" + new string('a', 43);
        Assert.True(settings.HasUsableToken());
    }

    [Fact]
    public void No_item_group_is_enabled_until_it_is_explicitly_chosen()
    {
        var settings = new PluginSettings();

        Assert.False(settings.IsItemGroupEnabled("never-seen"));
        // An unknown key is simply "not opted in" — a future item group needs no settings migration.
        Assert.False(settings.IsItemGroupEnabled("future-category"));
    }

    [Fact]
    public void Unknown_group_key_reads_disabled_without_throwing_on_null_or_empty()
    {
        var settings = new PluginSettings();

        // Reading tolerates blank keys (returns false); writing one is always a caller bug.
        Assert.False(settings.IsItemGroupEnabled(null!));
        Assert.False(settings.IsItemGroupEnabled(""));
    }

    [Fact]
    public void Item_groups_are_toggled_by_key_so_new_groups_need_no_settings_change()
    {
        var settings = new PluginSettings();

        settings.SetItemGroupEnabled("cosmetics", true);
        Assert.True(settings.IsItemGroupEnabled("cosmetics"));
        Assert.False(settings.IsItemGroupEnabled("weapons"));

        settings.SetItemGroupEnabled("cosmetics", false);
        Assert.False(settings.IsItemGroupEnabled("cosmetics"));
    }

    [Fact]
    public void Setting_item_group_enabled_twice_does_not_duplicate_list_entry()
    {
        var settings = new PluginSettings();

        settings.SetItemGroupEnabled("cosmetics", true);
        settings.SetItemGroupEnabled("cosmetics", true);

        // The list should contain exactly one entry for "cosmetics".
        Assert.Single(settings.EnabledItemGroupKeys, "cosmetics");
    }

    [Fact]
    public void SetItemGroupEnabled_rejects_blank_key()
    {
        var settings = new PluginSettings();

        // Empty string should throw ArgumentException.
        Assert.Throws<ArgumentException>(() => settings.SetItemGroupEnabled("", true));

        // Null should throw ArgumentNullException, which derives from ArgumentException.
        Assert.Throws<ArgumentNullException>(() => settings.SetItemGroupEnabled(null!, true));
    }

    [Fact]
    public void Migration_with_items_consent_on_enables_exactly_legacy_groups()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "new", Label = "y", Ids = Array.Empty<uint>(), Legacy = false },
        };

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        Assert.True(changed);
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("new"));
        Assert.True(settings.ItemGroupConsentMigrated);
    }

    [Fact]
    public void Migration_with_items_consent_off_enables_nothing_but_still_completes()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "new", Label = "y", Ids = Array.Empty<uint>(), Legacy = false },
        };

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: false);

        Assert.True(changed);
        Assert.False(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("new"));
        Assert.True(settings.ItemGroupConsentMigrated);

        // Seen-marking is unconditional: a legacy group is not new to this user even when their
        // items consent was off, so it must never earn a "New" badge. The non-legacy group IS
        // new and stays unseen.
        Assert.True(settings.IsItemGroupSeen("old"));
        Assert.False(settings.IsItemGroupSeen("new"));
    }

    [Fact]
    public void Migration_runs_only_once()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };

        // First call should return true and mark the flag.
        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);
        Assert.True(changed);
        Assert.True(settings.ItemGroupConsentMigrated);

        // Second call should return false and change nothing.
        var groups2 = new[]
        {
            new ItemManifestGroup { Key = "different", Label = "y", Ids = Array.Empty<uint>(), Legacy = true },
        };
        var changed2 = settings.MigrateItemGroupConsent(groups2, itemsCategoryEnabled: true);
        Assert.False(changed2);
        // The original group should still be enabled, the new one should not have been added.
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("different"));
        // The early return must touch nothing — the seen list included.
        Assert.False(settings.IsItemGroupSeen("different"));
    }

    [Fact]
    public void Legacy_groups_are_marked_seen_by_migration()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "new", Label = "y", Ids = Array.Empty<uint>(), Legacy = false },
        };

        settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        // Legacy groups should be marked as seen — they are not new to this user.
        Assert.True(settings.IsItemGroupSeen("old"));
        // Non-legacy groups arriving at migration time are new — not seen.
        Assert.False(settings.IsItemGroupSeen("new"));
    }

    [Fact]
    public void Seen_tracking_marks_groups_and_tolerates_duplicates()
    {
        var settings = new PluginSettings();

        settings.MarkItemGroupsSeen(new[] { "a", "b" });

        Assert.True(settings.IsItemGroupSeen("a"));
        Assert.True(settings.IsItemGroupSeen("b"));
        Assert.False(settings.IsItemGroupSeen("c"));

        // Calling again with "a" should not duplicate the list entry.
        settings.MarkItemGroupsSeen(new[] { "a" });
        Assert.Single(settings.SeenItemGroupKeys, "a");
    }

    [Fact]
    public void Seen_tracking_is_best_effort_about_malformed_input()
    {
        var settings = new PluginSettings();

        // A null sequence is a no-op — seen keys come from server-supplied group data during UI
        // rendering, so malformed input must degrade gracefully rather than throw mid-draw.
        settings.MarkItemGroupsSeen(null!);
        Assert.Empty(settings.SeenItemGroupKeys);

        // A blank key in the middle of a batch is skipped; its valid neighbors are still marked.
        settings.MarkItemGroupsSeen(new[] { "a", "", "b" });
        Assert.True(settings.IsItemGroupSeen("a"));
        Assert.True(settings.IsItemGroupSeen("b"));
        Assert.Equal(2, settings.SeenItemGroupKeys.Count);
    }

    [Fact]
    public void Seen_reads_tolerate_blank_keys()
    {
        var settings = new PluginSettings();

        // Reading tolerates blank keys (returns false), mirroring IsItemGroupEnabled.
        Assert.False(settings.IsItemGroupSeen(null!));
        Assert.False(settings.IsItemGroupSeen(""));
    }

    [Fact]
    public void Migration_skips_a_blank_legacy_group_key_and_keeps_going()
    {
        var settings = new PluginSettings();

        // Group data comes from the server, so a malformed group must degrade gracefully: the
        // blank key is skipped while its valid sibling is still enabled and marked seen. Without
        // the skip, SetItemGroupEnabled would throw mid-migration and strand the run-once flag
        // with only part of the work done.
        var groups = new[]
        {
            new ItemManifestGroup { Key = "", Label = "broken", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        Assert.True(changed);
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.True(settings.IsItemGroupSeen("old"));
        Assert.Single(settings.EnabledItemGroupKeys, "old");
        Assert.Single(settings.SeenItemGroupKeys, "old");
    }

    [Fact]
    public void Migration_with_no_groups_still_completes_and_touches_nothing()
    {
        var settings = new PluginSettings();

        // An empty (non-null) groups list is a valid migration: the flag flips so it never runs
        // again, but there is nothing to enable or mark seen.
        var changed = settings.MigrateItemGroupConsent(
            Array.Empty<ItemManifestGroup>(), itemsCategoryEnabled: true);

        Assert.True(changed);
        Assert.True(settings.ItemGroupConsentMigrated);
        Assert.Empty(settings.EnabledItemGroupKeys);
        Assert.Empty(settings.SeenItemGroupKeys);
    }
}
