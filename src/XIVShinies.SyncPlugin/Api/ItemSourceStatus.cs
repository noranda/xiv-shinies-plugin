namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// How one storage source was read during an item scan. Describes a physical location where items
/// can be stored: the inventory, saddlebag, retainers, armoire, or glamour dresser.
/// </summary>
/// <remarks>
/// A <c>record</c> is a reference type (like a class) whose equality the compiler rewrites to
/// compare by value — two instances with the same fields are equal, the way two plain objects
/// with the same contents are "equal" under a deep comparison in JS. The <c>required</c> keyword
/// means State must be set whenever the record is created.
/// </remarks>
public sealed record ItemSourceStatus
{
    /// <summary>
    /// One of the <see cref="SourceStates"/> wire strings, describing whether this source was read
    /// directly from the game (live), from a local cache (cached), never before opened (unscanned),
    /// or is persistently loaded (loaded). Required.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Source-specific detail, omitted from JSON when null. For retainers: the number of retainers
    /// whose contents were cached (not live). For other sources, unused.
    /// </summary>
    public int? Count { get; init; }

    /// <summary>
    /// How many of the source exist in total, when the game can say — omitted from JSON when null.
    /// For retainers: the character's retainer count, so a reader can tell "3 of 5 scanned" apart
    /// from "3 of 3". A count only; nothing that identifies an individual retainer travels.
    /// </summary>
    public int? Total { get; init; }
}

/// <summary>
/// The wire strings for <see cref="ItemSourceStatus.State"/>, named as machine-readable constants
/// so typos are compile errors, not silent bugs.
/// </summary>
public static class SourceStates
{
    /// <summary>Read directly from game memory this pass — the most current information available.</summary>
    public const string Live = "live";

    /// <summary>
    /// Read from the game's local cache — populated when the player last opened the source, so
    /// the data is real but possibly stale.
    /// </summary>
    public const string Cached = "cached";

    /// <summary>
    /// The source's cache has never been populated — the player has never opened it. The source
    /// contributes no counts, and this status is how the server learns that a low total may be
    /// a floor rather than the truth.
    /// </summary>
    public const string Unscanned = "unscanned";

    /// <summary>
    /// The armoire's contents are loaded and readable — the game fetches them from the server
    /// the first time the player opens the armoire each session.
    /// </summary>
    public const string Loaded = "loaded";

    /// <summary>
    /// The game never exposes this source to plugins at all — no live read, no cache, and no
    /// player action that changes that. Distinct from <see cref="Unscanned"/>, which the player
    /// can resolve by opening the source once.
    /// </summary>
    /// <remarks>
    /// Local-only: this status exists so the settings window can say why such a source is absent
    /// from the counts, and <see cref="Sync.SyncPayloadBuilder"/> drops it from the upload — a
    /// source that can never carry counts tells the server nothing about how to judge them.
    /// </remarks>
    public const string Unreadable = "unreadable";
}

/// <summary>
/// The wire keys naming each storage source, kept as constants to prevent typos and to keep
/// serialization predictable.
/// </summary>
/// <remarks>
/// <para>
/// Source-keyed, not category-keyed: any collector may report on any source, and nothing downstream
/// branches on which collector said it. A skip reason describes a category and is thus category-keyed
/// (the achievements collector reports "achievement list not loaded"), but a source note describes
/// a physical storage location and is thus source-keyed. This distinction keeps the extension
/// contract clean: a future collector adding a new category can still say "my items came from the
/// armoire" by using the same <see cref="Armoire"/> key.
/// </para>
/// <para>
/// The constant VALUES are already camelCase and reach the wire verbatim — no serializer policy
/// rewrites them. Only the C# constant NAMES follow PascalCase, per .NET convention.
/// </para>
/// </remarks>
public static class SourceKeys
{
    /// <summary>
    /// The containers read live each pass: bags, equipped gear, the armoury chest, and crystals.
    /// </summary>
    public const string Inventory = "inventory";

    /// <summary>
    /// The character's currencies, read live each pass. Covers both places the game keeps them: the
    /// Currency inventory container (the common currencies on the in-game Currency tab) and the
    /// currency subsystem that tracks the rest (scrips, Bicolor Gemstones, ventures, and so on).
    /// </summary>
    public const string Currencies = "currencies";

    /// <summary>The chocobo saddlebag, including the premium half.</summary>
    public const string Saddlebag = "saddlebag";

    /// <summary>The player's own retainers — their bags and equipped gear.</summary>
    public const string Retainers = "retainers";

    /// <summary>The armoire (cabinet) — long-term storage for select gear the game curates.</summary>
    public const string Armoire = "armoire";

    /// <summary>The glamour dresser — gear stored away for glamour use.</summary>
    public const string GlamourDresser = "glamourDresser";

    /// <summary>
    /// Housing mannequins — gear displayed on them. Always reported as
    /// <see cref="SourceStates.Unreadable"/>: the game fetches a mannequin's contents only while
    /// the player interacts with it inside the house and never caches them, so no plugin can read
    /// them from anywhere. The key exists so the settings panel can say so; it never travels.
    /// </summary>
    public const string Mannequins = "mannequins";
}
