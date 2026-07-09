using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Maps an item ID to the armoire ("Cabinet") row that holds it.
/// </summary>
/// <remarks>
/// The game answers "is this in the armoire?" by <b>Cabinet row ID</b>, not by item ID, so a lookup
/// table is needed to go from the item IDs the server asks about to the ID the game understands.
/// Building it is pure — it takes plain (cabinetId, itemId) pairs — so it is unit-tested, while
/// reading the game sheet that supplies those pairs is left to the collector.
/// </remarks>
public static class ArmoireIndex
{
    /// <summary>Builds an item ID → Cabinet row ID lookup.</summary>
    /// <param name="rows">Every armoire row, as (its own ID, the item it stores).</param>
    public static IReadOnlyDictionary<uint, uint> Build(IEnumerable<(uint CabinetId, uint ItemId)> rows)
    {
        var index = new Dictionary<uint, uint>();

        foreach (var (cabinetId, itemId) in rows)
        {
            // Row 0 and rows with no item are padding in the game's sheet, not real entries.
            if (itemId == 0)
                continue;

            // TryAdd keeps the first row for an item and ignores later duplicates, rather than
            // throwing the way the indexer would. Should the sheet ever list an item twice, the
            // lowest-numbered row wins deterministically.
            index.TryAdd(itemId, cabinetId);
        }

        return index;
    }
}
