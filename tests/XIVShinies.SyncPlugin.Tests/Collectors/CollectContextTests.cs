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
}
