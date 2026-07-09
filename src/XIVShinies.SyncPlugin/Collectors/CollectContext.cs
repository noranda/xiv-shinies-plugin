using System;
using System.Collections.Generic;
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
    /// <summary>The most recent <c>/config</c> response, or null when it has not been fetched.</summary>
    public ConfigResponse? RemoteConfig { get; init; }

    /// <summary>
    /// The only item IDs the plugin may check possession of. Empty when the server has not told us
    /// yet, in which case an item collector has nothing to do.
    /// </summary>
    // An expression-bodied property: recomputed on each read rather than stored.
    public IReadOnlyList<uint> ItemManifest => RemoteConfig?.ItemManifest ?? Array.Empty<uint>();
}
