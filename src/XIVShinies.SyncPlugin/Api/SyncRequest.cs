using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>What caused this upload. Serialized as a lowercase string per the contract.</summary>
public enum SyncTrigger
{
    /// <summary>The periodic full-sweep safety net.</summary>
    Interval,

    /// <summary>A full sweep shortly after the character finished loading.</summary>
    Login,

    /// <summary>The user pressed "Sync now".</summary>
    Manual,

    /// <summary>A real-time unlock event fired in game.</summary>
    Unlock,
}

/// <summary>
/// The body of <c>POST /api/plugin/v1/sync</c> — a collection snapshot for one character.
/// </summary>
// A `record` is a class built for holding data: the compiler writes value-based equality and a
// readable ToString for you, and `with` expressions let you copy-and-tweak
// (`request with { Trigger = ... }`), much like object spread `{...obj, trigger: x}` in JS.
// `required` forces the caller to set the property in the object initializer — the compiler
// refuses to build the object otherwise. `init` means it can only be set at construction, never
// reassigned afterwards.
public sealed record SyncRequest
{
    /// <summary>SHA-256 of the character's ContentId, lowercase hex. The raw id never travels.</summary>
    public required string CharacterContentIdHash { get; init; }

    /// <summary>Used only for first-upload binding and to render a friendly 403.</summary>
    public required string CharacterName { get; init; }

    /// <summary>The character's home world name.</summary>
    public required string HomeWorld { get; init; }

    /// <summary>This plugin's version string.</summary>
    public required string PluginVersion { get; init; }

    /// <summary>
    /// Optional. The <c>manifestVersion</c> from <c>/config</c> that the items list was built
    /// against. Null here means the key is omitted from the JSON entirely.
    /// </summary>
    public string? ManifestVersion { get; init; }

    /// <summary>What prompted this upload.</summary>
    public required SyncTrigger Trigger { get; init; }

    /// <summary>The facts being uploaded. Every category inside is optional.</summary>
    public required SyncCollections Collections { get; init; }
}

/// <summary>
/// The per-category facts. Every property is nullable on purpose: <b>null means "not read this
/// time"</b> and is omitted from the JSON, whereas an empty array means "read, and it was empty".
/// The server treats both as carrying no facts, but only absence is safe when a category could
/// not be read (for example, the achievements list was never opened).
/// </summary>
public sealed record SyncCollections
{
    /// <summary>Unlocked achievement IDs.</summary>
    public IReadOnlyList<uint>? Achievements { get; init; }

    /// <summary>Unlocked minion IDs.</summary>
    public IReadOnlyList<uint>? Minions { get; init; }

    /// <summary>Unlocked mount IDs.</summary>
    public IReadOnlyList<uint>? Mounts { get; init; }

    /// <summary>Completed quest IDs (Excel row ids, which equal the server's quest ids).</summary>
    public IReadOnlyList<uint>? Quests { get; init; }

    /// <summary>Possession counts for the items the server asked about in its manifest.</summary>
    public IReadOnlyList<ItemPossession>? Items { get; init; }
}

/// <summary>How many of a manifest item the character possesses.</summary>
public sealed record ItemPossession
{
    /// <summary>The item ID, taken from the server's manifest.</summary>
    // `uint` is an unsigned 32-bit integer — it cannot be negative. There is no JS equivalent
    // (all JS numbers are doubles); using it here encodes the contract's "positive integer"
    // constraint in the type itself rather than relying on a runtime check.
    public required uint Id { get; init; }

    /// <summary>How many are held. Zero is meaningful: it proves nothing, but it is valid.</summary>
    public required uint Count { get; init; }

    /// <summary>
    /// False when the count came from a cache rather than a live container read. The server
    /// deliberately ignores this — a stale positive is still a positive — but it is reported
    /// honestly.
    /// </summary>
    public required bool Fresh { get; init; }
}
