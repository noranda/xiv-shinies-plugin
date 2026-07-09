using System.Collections.Generic;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>Skip reasons the runner itself produces. Collectors may return their own.</summary>
public static class CollectSkipReasons
{
    /// <summary>The user or the server switched this category off.</summary>
    public const string Disabled = "disabled";

    /// <summary>The collector threw. Its facts are omitted; the rest of the snapshot proceeds.</summary>
    public const string CollectorError = "collector_error";

    /// <summary>The game data sheet could not be loaded.</summary>
    public const string SheetUnavailable = "sheet_unavailable";

    /// <summary>
    /// The server has not told us which items it cares about yet, so we cannot know what to look
    /// for. Distinct from "the manifest is empty", which means there is genuinely nothing to check.
    /// </summary>
    public const string NoRemoteConfig = "no_remote_config";

    /// <summary>The inventory is not readable — usually because no character is logged in.</summary>
    public const string InventoryUnavailable = "inventory_unavailable";

    /// <summary>
    /// The achievements list has never been requested from the server this session, so the game
    /// cannot answer which achievements are complete. The user fixes this by opening their
    /// Achievements window once; the settings UI turns this reason into that hint.
    /// </summary>
    public const string AchievementListNotLoaded = "achievement_list_not_loaded";
}

/// <summary>
/// What a collector produced: either facts, or a reason it could not read them.
/// </summary>
/// <remarks>
/// The distinction matters enormously. Facts — even an <b>empty</b> list — mean "I read the source,
/// and this is what was there". A skip means "I could not read the source", which omits the
/// category from the upload entirely. The server treats absence as "no information" and never as
/// "the collection is empty", so a skip can never erase anything.
/// </remarks>
public sealed record CollectResult
{
    // A private constructor forces callers through the named factory methods below, so a result
    // can never be built in a nonsensical state (both facts and a skip reason, or neither).
    private CollectResult()
    {
    }

    /// <summary>Why the source could not be read, or null when facts were collected.</summary>
    public string? SkipReason { get; private init; }

    /// <summary>The collected facts as JSON, or null when the collector skipped.</summary>
    public JsonNode? Facts { get; private init; }

    /// <summary>True when facts were read (possibly an empty list).</summary>
    public bool WasCollected => SkipReason is null && Facts is not null;

    /// <summary>The source could not be read; omit this category from the upload.</summary>
    /// <param name="reason">
    /// A short, stable, machine-readable reason (for example <c>"achievement_list_not_loaded"</c>).
    /// The UI maps it to a hint; nothing else interprets it.
    /// </param>
    public static CollectResult Skipped(string reason) => new() { SkipReason = reason };

    /// <summary>Facts for a category that is a plain list of unlocked or completed IDs.</summary>
    /// <remarks>
    /// Zero is dropped. The server requires <b>positive</b> integers, and the game's data sheets
    /// begin with a blank row 0 used as padding. A single zero would fail validation and cause the
    /// server to reject the <b>entire upload</b> — every category, not just this one. Filtering here
    /// rather than in each collector means no collector, present or future, can forget.
    /// (This does not touch <see cref="Items"/>: an item <i>count</i> of zero is legitimate.)
    /// </remarks>
    public static CollectResult Ids(IReadOnlyList<uint> ids)
    {
        var positiveIds = new List<uint>(ids.Count);
        foreach (var id in ids)
        {
            if (id != 0)
                positiveIds.Add(id);
        }

        return new() { Facts = SyncFacts.Ids(positiveIds) };
    }

    /// <summary>Facts for the <c>items</c> category, which carries objects rather than IDs.</summary>
    public static CollectResult Items(IReadOnlyList<ItemPossession> items) =>
        new() { Facts = SyncFacts.Items(items) };
}
