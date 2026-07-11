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
    /// The only item IDs the plugin may check possession of, truncated to
    /// <see cref="MaxManifestItems"/>. Empty when the server has not told us yet, in which case
    /// an item collector has nothing to do.
    /// </summary>
    // An expression-bodied getter would fit on one line, but the truncation branch earns a body.
    // Recomputed on each read rather than stored; collectors read it once per pass.
    public IReadOnlyList<uint> ItemManifest
    {
        get
        {
            var manifest = RemoteConfig?.ItemManifest;
            if (manifest is null)
                return Array.Empty<uint>();

            if (manifest.Count <= MaxManifestItems)
                return manifest;

            // The hostile path only: copy the sane prefix and ignore the rest.
            var bounded = new uint[MaxManifestItems];
            for (var i = 0; i < MaxManifestItems; i++)
                bounded[i] = manifest[i];

            return bounded;
        }
    }
}
