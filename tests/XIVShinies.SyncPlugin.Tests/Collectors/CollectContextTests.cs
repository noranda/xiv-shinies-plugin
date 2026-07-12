using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The item manifest is server-controlled and consumed on the FRAMEWORK thread (the item
// collector scans several containers per id), so its size must be bounded here — the one
// funnel every consumer reads it through. These tests pin that bound.
public class CollectContextTests
{
    private static ConfigResponse ConfigWith(IReadOnlyList<uint> manifest) => new()
    {
        Categories = new Dictionary<string, bool>(),
        Enabled = true,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = manifest,
        ManifestVersion = "abc123",
    };

    // Builds a ConfigResponse with named item groups. Each tuple is (groupKey, ids, legacy).
    private static ConfigResponse ConfigWithGroups(
        params (string key, uint[] ids, bool legacy)[] groups)
    {
        var itemManifestGroups = groups.Select(g => new ItemManifestGroup
        {
            Key = g.key,
            Label = $"Label for {g.key}",
            Ids = g.ids,
            Legacy = g.legacy,
        }).ToList();

        return new()
        {
            Categories = new Dictionary<string, bool>(),
            Enabled = true,
            Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
            ItemManifest = Array.Empty<uint>(),
            ItemManifestGroups = itemManifestGroups,
            ManifestVersion = "abc123",
        };
    }

    [Fact]
    public void The_manifest_is_empty_when_no_config_has_been_fetched()
    {
        var context = new CollectContext { RemoteConfig = null };

        Assert.Empty(context.ItemManifest);
    }

    [Fact]
    public void A_sane_manifest_passes_through_untouched()
    {
        var manifest = new uint[] { 10, 20, 30 };
        var context = new CollectContext { RemoteConfig = ConfigWith(manifest) };

        Assert.Same(manifest, context.ItemManifest);
    }

    // A hostile or broken backend could send millions of ids; each one costs several container
    // scans on the game's render thread. The cap turns "the game freezes" into "the tail of an
    // absurd manifest is ignored".
    [Fact]
    public void An_oversized_manifest_is_truncated_to_the_cap()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 500)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext { RemoteConfig = ConfigWith(oversized) };

        var bounded = context.ItemManifest;

        Assert.Equal(CollectContext.MaxManifestItems, bounded.Count);
        Assert.Equal(1u, bounded[0]);
        Assert.Equal((uint)CollectContext.MaxManifestItems, bounded[^1]);
    }

    [Fact]
    public void The_manifest_unions_only_enabled_groups()
    {
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("relic-proofs", new uint[] { 1, 2 }, true),
                ("relic-materials", new uint[] { 2, 3 }, false)),  // 2 overlaps on purpose
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs", "relic-materials" },
        };

        Assert.Equal(new uint[] { 1, 2, 3 }, context.ItemManifest);  // deduped, first-seen order
    }

    [Fact]
    public void The_manifest_excludes_disabled_groups()
    {
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("relic-proofs", new uint[] { 1, 2 }, true),
                ("relic-materials", new uint[] { 3 }, false)),
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs" },
        };

        Assert.Equal(new uint[] { 1, 2 }, context.ItemManifest);
    }

    [Fact]
    public void The_manifest_is_empty_when_no_group_is_enabled()
    {
        // Groups present, user opted into none: nothing to scan. Distinct from the null-groups
        // fallback below — consent is per group once groups exist.
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(("relic-proofs", new uint[] { 1 }, true)),
            EnabledItemGroupKeys = new HashSet<string>(),
        };

        Assert.Empty(context.ItemManifest);
    }

    [Fact]
    public void The_manifest_falls_back_to_the_flat_manifest_without_groups()
    {
        // A server without groups: the flat manifest IS the manifest, ungated by group consent
        // (the category toggle gates it, as it always has).
        var context = new CollectContext
        {
            RemoteConfig = ConfigWith(new uint[] { 1, 2 }),
            EnabledItemGroupKeys = new HashSet<string>(),
        };

        Assert.Equal(new uint[] { 1, 2 }, context.ItemManifest);
    }

    // The grouped path has its own cap: a hostile or broken backend could stuff millions of ids
    // into a single group, so the union stops collecting exactly at the cap and ignores the rest.
    [Fact]
    public void An_oversized_group_union_is_truncated_to_the_cap()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(("relic-proofs", oversized, true)),
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs" },
        };

        var bounded = context.ItemManifest;

        Assert.Equal(CollectContext.MaxManifestItems, bounded.Count);
        Assert.Equal(1u, bounded[0]);
        // The last kept id is the one at index MaxManifestItems - 1 of the source group —
        // the cut lands exactly at the cap, not one before or after it.
        Assert.Equal(oversized[CollectContext.MaxManifestItems - 1], bounded[^1]);
    }

    [Fact]
    public void The_enabled_group_list_contains_only_opted_in_groups()
    {
        // The collector needs the group view (not just the union) to stay honest about what it
        // scanned; the settings view needs it for checkboxes. One computed property serves both.
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("a", new uint[] { 1 }, false), ("b", new uint[] { 2 }, false)),
            EnabledItemGroupKeys = new HashSet<string> { "b" },
        };

        Assert.Equal(new[] { "b" }, context.EnabledGroups.Select(g => g.Key));
    }
}
