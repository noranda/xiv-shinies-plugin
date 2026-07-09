using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>GET /api/plugin/v1/config</c> — remote kill switches, sync cadence, and the
/// item manifest. The client must honor the kill switches even though the server enforces them
/// too; obeying them locally saves pointless round trips.
/// </summary>
public sealed record ConfigResponse
{
    /// <summary>Per-category kill switches. False means "do not collect or send this".</summary>
    public required ConfigCategories Categories { get; init; }

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
}

/// <summary>Per-category kill switches (true = enabled).</summary>
public sealed record ConfigCategories
{
    /// <summary>Whether achievement IDs may be collected and sent.</summary>
    public required bool Achievements { get; init; }

    /// <summary>Whether item possession counts may be collected and sent.</summary>
    public required bool Items { get; init; }

    /// <summary>Whether minion IDs may be collected and sent.</summary>
    public required bool Minions { get; init; }

    /// <summary>Whether mount IDs may be collected and sent.</summary>
    public required bool Mounts { get; init; }

    /// <summary>Whether completed quest IDs may be collected and sent.</summary>
    public required bool Quests { get; init; }
}

/// <summary>Server-chosen sync cadence.</summary>
public sealed record ConfigIntervals
{
    /// <summary>How often to run a full-sweep upload.</summary>
    public required int FullSyncMinutes { get; init; }

    /// <summary>How long to wait after an unlock event before uploading, to batch a burst.</summary>
    public required int UnlockDebounceSeconds { get; init; }
}
