using System;
using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Everything a collector may need from outside the game, handed to it for one collection pass.
/// </summary>
/// <remarks>
/// A context object rather than constructor arguments, because these values change between passes:
/// the server's config is re-fetched periodically. A collector that needs nothing from it simply
/// ignores it — which is what keeps the runner free of "does this collector need the manifest?"
/// branching.
/// </remarks>
public sealed record CollectContext
{
    /// <summary>
    /// The most items a manifest may ask about. The manifest is server-controlled and consumed on
    /// the FRAMEWORK thread — the item collector runs several container scans per id — so a
    /// hostile or broken backend sending millions of ids would otherwise freeze the game loop.
    /// The ceiling is far above any legitimate manifest (dozens of ids today, low thousands if
    /// every relic series ever shipped); if a real manifest ever approaches it, the right fix is
    /// spreading the scan across frames, not raising this number.
    /// </summary>
    public const int MaxManifestItems = 5000;

    /// <summary>The most recent <c>/config</c> response, or null when it has not been fetched.</summary>
    public ConfigResponse? RemoteConfig { get; init; }

    /// <summary>
    /// The user's per-group opt-ins, keyed by group Key (stable consent identifier). Only
    /// consulted when the config carries groups; when groups are null, the flat manifest
    /// is ungated by group consent (only the category-level toggle gates it).
    /// </summary>
    public IReadOnlySet<string> EnabledItemGroupKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The subset of the server's item manifest groups whose key is in
    /// <see cref="EnabledItemGroupKeys"/>, in the server's original order. Empty when groups
    /// are null (flat manifest path) or when no groups are enabled. Collectors use this to
    /// report which groups they actually scanned; settings UI uses it to populate checkboxes.
    /// </summary>
    public IReadOnlyList<ItemManifestGroup> EnabledGroups
    {
        get
        {
            var groups = RemoteConfig?.ItemManifestGroups;
            if (groups is null)
                return Array.Empty<ItemManifestGroup>();

            return groups
                .Where(g => EnabledItemGroupKeys.Contains(g.Key))
                .ToList();
        }
    }

    /// <summary>
    /// The only item IDs the plugin may check possession of, truncated to
    /// <see cref="MaxManifestItems"/>. Empty when the server has not told us yet, in which case
    /// an item collector has nothing to do. When the server sends groups, this is the union of
    /// enabled groups; when groups are null, this is the flat manifest (older server fallback).
    /// </summary>
    // When groups are present, we union their ids (with dedup) instead of using the flat manifest.
    // When groups are null, we fall back to the flat manifest path unchanged.
    // Recomputed on each read rather than stored; collectors read it once per pass.
    public IReadOnlyList<uint> ItemManifest
    {
        get
        {
            // If no config has been fetched yet, nothing to scan.
            if (RemoteConfig is null)
                return Array.Empty<uint>();

            // When the server sends groups, the manifest is the union of enabled groups.
            // Otherwise, fall back to the flat manifest (older server, no groups).
            var groups = RemoteConfig.ItemManifestGroups;
            if (groups is not null)
            {
                return GetGroupUnionManifest();
            }

            // Flat manifest path (older server or new server with no groups): use exactly the
            // same logic as before — return the flat manifest, truncating if needed.
            var manifest = RemoteConfig.ItemManifest;
            if (manifest.Count <= MaxManifestItems)
                return manifest;

            // The hostile path only: copy the sane prefix and ignore the rest.
            var bounded = new uint[MaxManifestItems];
            for (var i = 0; i < MaxManifestItems; i++)
                bounded[i] = manifest[i];

            return bounded;
        }
    }

    /// <summary>
    /// Union the ids from all enabled groups into a single manifest, with deduplication and
    /// truncation to <see cref="MaxManifestItems"/>.
    /// </summary>
    /// <remarks>
    /// Two groups may legitimately name the same item id (e.g., a seasonal item that appears in
    /// multiple collection tracks). A duplicate entry would double-report it; the dedup guard
    /// ensures each id appears once in the manifest, in first-seen order.
    /// </remarks>
    private IReadOnlyList<uint> GetGroupUnionManifest()
    {
        var result = new List<uint>();
        var seen = new HashSet<uint>();

        foreach (var group in EnabledGroups)
        {
            foreach (var id in group.Ids)
            {
                // Only add if we haven't seen this id before (dedup).
                if (seen.Add(id))
                {
                    result.Add(id);
                    // Stop once we've collected enough ids.
                    if (result.Count >= MaxManifestItems)
                        return result;
                }
            }
        }

        return result;
    }
}
