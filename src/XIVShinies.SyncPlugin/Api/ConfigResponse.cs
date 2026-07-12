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
    /// The manifest split into named consent groups, or null when the server does not send them.
    /// Null means: fall back to <see cref="ItemManifest"/> as one implicit group covered by the
    /// existing items consent — an older server is a supported peer.
    /// </summary>
    // NOT `required`: a required property makes deserialization of older configs throw,
    // and an older server is a supported peer, not an error.
    public IReadOnlyList<ItemManifestGroup>? ItemManifestGroups { get; init; }

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

/// <summary>
/// One named slice of the item manifest, carrying its own user consent.
/// </summary>
/// <remarks>
/// <para>
/// The plugin never hardcodes a group key and interprets exactly one flag: <see cref="Legacy"/>.
/// Everything else (which ids, what the group means) is the server's business.
/// </para>
/// <para>
/// <see cref="Key"/>, <see cref="Label"/>, and <see cref="Ids"/> are <c>required</c> on purpose:
/// one malformed group fails the whole <c>/config</c> deserialization rather than being partially
/// trusted — the same all-or-nothing stance every other required config field takes. The plugin
/// then keeps its last known config until the next poll.
/// </para>
/// </remarks>
public sealed record ItemManifestGroup
{
    /// <summary>Stable consent identifier. A server-side RENAME is a new group (re-consent).</summary>
    public required string Key { get; init; }

    /// <summary>User-facing label, shown beside the group's opt-in checkbox.</summary>
    public required string Label { get; init; }

    /// <summary>The item ids this group asks about.</summary>
    public required IReadOnlyList<uint> Ids { get; init; }

    /// <summary>
    /// True when this group's scope was already covered by pre-group items consent — the
    /// one-time migration opts existing users into exactly these groups and nothing else.
    /// </summary>
    public bool Legacy { get; init; }
}
