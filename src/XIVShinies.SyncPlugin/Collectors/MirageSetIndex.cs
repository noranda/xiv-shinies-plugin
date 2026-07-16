using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Maps a glamour-dresser outfit's set item ID to the pieces inside it, and reads a slot's unlock
/// bits to say which of those pieces are currently stored.
/// </summary>
/// <remarks>
/// <para>
/// A dresser slot holding an outfit records the outfit's <b>set item ID</b> (a
/// <c>MirageStoreSetItem</c> row), not the IDs of the pieces inside it. To count a stored piece the
/// collector must expand that set ID into its pieces — the game answers "which pieces?" only through
/// the sheet, and "which are actually stored right now?" only through a per-slot bit field.
/// </para>
/// <para>
/// Both steps are pure — <see cref="Build"/> takes plain (set ID, piece array) rows and
/// <see cref="StoredPieces"/> takes a piece array plus a <see cref="ushort"/> of bits — so both are
/// unit-tested, while reading the game sheet and the dresser cache that supply them is left to the
/// collector.
/// </para>
/// </remarks>
public static class MirageSetIndex
{
    // A MirageStoreSetItem row has one column per equipment slot; the outfit's unlock bits use one
    // bit per column in the same order, so a piece array is always this wide and bit i names slot i.
    private const int SlotCount = 11;

    /// <summary>Builds a set item ID → piece IDs lookup, one array per outfit in column order.</summary>
    /// <param name="rows">
    /// Every outfit row, as (its set item ID, its <see cref="SlotCount"/> piece IDs). Empty slots are
    /// ID 0 and must keep their position — <see cref="StoredPieces"/> pairs each unlock bit with the
    /// array index of the same number, so compacting the array would misalign every later piece.
    /// </param>
    public static IReadOnlyDictionary<uint, uint[]> Build(
        IEnumerable<(uint SetItemId, uint[] PieceItemIds)> rows)
    {
        var index = new Dictionary<uint, uint[]>();

        foreach (var (setItemId, pieceItemIds) in rows)
        {
            // Row 0 and rows whose slots are all empty are padding in the game's sheet. Rows with
            // SOME empty slots are kept verbatim, zeros and all — the positions are load-bearing.
            if (setItemId == 0 || AllEmpty(pieceItemIds))
                continue;

            // TryAdd keeps the first row for a set ID and ignores later duplicates, rather than
            // throwing the way the indexer would. Should the sheet ever list a set twice, the
            // lowest-numbered row wins deterministically.
            index.TryAdd(setItemId, pieceItemIds);
        }

        return index;
    }

    /// <summary>Yields the piece IDs a stored outfit currently holds, per its unlock bits.</summary>
    /// <param name="pieceItemIds">The outfit's pieces in column order (from <see cref="Build"/>).</param>
    /// <param name="unlockBits">
    /// One bit per slot: bit <c>i</c> set means slot <c>i</c>'s piece is stored in the outfit. A
    /// piece can be withdrawn from an outfit individually, which clears its bit, so this is what
    /// keeps the expansion honest about a partly-emptied outfit.
    /// </param>
    public static IEnumerable<uint> StoredPieces(uint[] pieceItemIds, ushort unlockBits)
    {
        // `yield return` makes this a lazy sequence — the C# equivalent of a JS `function*` generator.
        // Each piece is produced as the caller enumerates, one at a time, rather than being collected
        // into a list up front; the caller's foreach drives the loop.

        // Bounded by both the array length and the slot count: a stray high bit (padding, or a field
        // wider than the 11 real slots) can never read past the pieces or invent a slot.
        var slots = pieceItemIds.Length < SlotCount ? pieceItemIds.Length : SlotCount;

        for (var slot = 0; slot < slots; slot++)
        {
            var stored = (unlockBits & (1 << slot)) != 0;

            // An empty slot (ID 0) contributes nothing even when its bit is set — armor outfits leave
            // the weapon slots empty, so this is the ordinary case, not a rare one.
            if (stored && pieceItemIds[slot] != 0)
                yield return pieceItemIds[slot];
        }
    }

    // A row is padding when every one of its slots is empty; a row with any real piece is kept.
    private static bool AllEmpty(uint[] pieceItemIds)
    {
        foreach (var id in pieceItemIds)
        {
            if (id != 0)
                return false;
        }

        return true;
    }
}
