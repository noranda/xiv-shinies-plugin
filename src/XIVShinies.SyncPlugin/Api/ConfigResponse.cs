using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>GET /api/plugin/v1/config</c> — remote kill switches, sync cadence, and the
/// item manifest. The client must honor the kill switches even though the server enforces them
/// too; obeying them locally saves pointless round trips.
/// </summary>
public sealed record ConfigResponse
{
    /// <summary>
    /// Per-category kill switches keyed by category (<c>"quests"</c>, …). False means "do not
    /// collect or send this". A dictionary rather than named properties, so a collector can look
    /// up its own switch by its <c>CategoryKey</c> without anyone branching on category names.
    /// </summary>
    public required Dictionary<string, bool> Categories { get; init; }

    /// <summary>The global kill switch. False means stop uploading entirely.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Server-chosen sync cadence.</summary>
    public required ConfigIntervals Intervals { get; init; }

    /// <summary>The only item IDs the plugin should check possession of.</summary>
    public required IReadOnlyList<uint> ItemManifest { get; init; }

    /// <summary>
    /// A content hash of the manifest, not a counter — compare it for equality only. When it is
    /// unchanged the plugin can skip re-scanning the inventory.
    /// </summary>
    public required string ManifestVersion { get; init; }

    /// <summary>
    /// Whether the server permits this category right now.
    /// </summary>
    /// <remarks>
    /// A category the server has never heard of reads as <b>enabled</b>. That lets a plugin ship a
    /// new collector before the server grows the matching switch: the server strips payload keys it
    /// does not recognize, so sending one costs a few bytes and breaks nothing. Defaulting to
    /// disabled instead would silently withhold facts until both sides shipped in lockstep.
    /// </remarks>
    public bool IsCategoryEnabled(string categoryKey) =>
        !Categories.TryGetValue(categoryKey, out var enabled) || enabled;
}

/// <summary>Server-chosen sync cadence.</summary>
public sealed record ConfigIntervals
{
    /// <summary>How often to run a full-sweep upload.</summary>
    public required int FullSyncMinutes { get; init; }

    /// <summary>How long to wait after an unlock event before uploading, to batch a burst.</summary>
    public required int UnlockDebounceSeconds { get; init; }
}
