using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// The facts being uploaded, keyed by category (<c>"quests"</c>, <c>"items"</c>, …).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dictionary rather than one named property per category, on purpose. A collector announces
    /// its own <c>CategoryKey</c> and contributes its facts under it, so adding a new collection is
    /// one new collector class — nothing here changes. Naming the categories in this type would
    /// force every caller that places a result to branch on the category name, which is exactly
    /// what the extensibility contract forbids.
    /// </para>
    /// <para>
    /// <b>A category that could not be read must simply be absent from this dictionary</b>, never
    /// present with an empty array. Absence means "not read this time"; an empty array means "read,
    /// and it was empty". That distinction is the monotonic-write rule.
    /// </para>
    /// <para>
    /// The values are <see cref="JsonNode"/> rather than <c>object</c> because the shapes differ
    /// per category — id-lists are arrays of numbers, <c>items</c> is an array of objects — and a
    /// JsonNode serializes exactly as built, with no polymorphism surprises.
    /// </para>
    /// </remarks>
    public required Dictionary<string, JsonNode> Collections { get; init; }
}

/// <summary>
/// Builds the <see cref="JsonNode"/> values that collectors place into
/// <see cref="SyncRequest.Collections"/>.
/// </summary>
public static class SyncFacts
{
    /// <summary>Facts for a category that is a plain list of unlocked/completed IDs.</summary>
    // SerializeToNode turns a value into an in-memory JSON tree rather than a string, so it can be
    // dropped into a larger document and serialized once, later.
    public static JsonNode Ids(IReadOnlyList<uint> ids) =>
        JsonSerializer.SerializeToNode(ids, ApiJson.Options)!;

    /// <summary>Facts for the <c>items</c> category, which carries objects rather than IDs.</summary>
    public static JsonNode Items(IReadOnlyList<ItemPossession> items) =>
        JsonSerializer.SerializeToNode(items, ApiJson.Options)!;
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
