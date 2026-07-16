using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The glamour-dresser outfit mapping is the pure part of resolving stored outfits into their
// pieces, so it is unit-tested here; reading the game's dresser cache is verified by in-game QA.
// Two behaviours are pinned: Build turns sheet rows into a set-id -> piece-array lookup, and
// StoredPieces reads a slot's unlock bits to say which of those pieces are actually in the outfit.
public class MirageSetIndexTests
{
    // A stored outfit slot names its SET item id; the dresser cache does not spell out the pieces
    // inside, so the index carries every slot's piece (in column order) to expand it later.
    [Fact]
    public void Maps_each_set_to_its_pieces_in_column_order()
    {
        var index = MirageSetIndex.Build(new[]
        {
            (SetItemId: 30u, PieceItemIds: new uint[] { 101, 102, 103, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 40u, PieceItemIds: new uint[] { 201, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        });

        Assert.Equal(new uint[] { 101, 102, 103, 0, 0, 0, 0, 0, 0, 0, 0 }, index[30]);
        Assert.Equal(new uint[] { 201, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, index[40]);
        Assert.Equal(2, index.Count);
    }

    // The whole feature rests on bit i lining up with piece-array index i, so Build must NEVER
    // compact a row's array. A piece in the last slot (Ring) with every earlier slot empty has to
    // stay at index 10; compacting it to index 0 would map the Ring bit to the wrong piece. This is
    // the load-bearing test — it fails the moment Build starts stripping interior zeros.
    [Fact]
    public void Preserves_interior_zero_columns()
    {
        var ringOnly = new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 555 };

        var index = MirageSetIndex.Build(new[] { (SetItemId: 50u, PieceItemIds: ringOnly) });

        Assert.Equal(ringOnly, index[50]);
        // The stored array must be 11 wide with the piece at the Ring position, not a 1-element array.
        Assert.Equal(555u, index[50][10]);
    }

    // A sheet row with no set id, or one whose slots are all empty, is padding — not a real outfit.
    [Fact]
    public void Skips_rows_with_no_set_id_or_no_pieces()
    {
        var index = MirageSetIndex.Build(new[]
        {
            (SetItemId: 0u, PieceItemIds: new uint[] { 101, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 60u, PieceItemIds: new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 70u, PieceItemIds: new uint[] { 301, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        });

        Assert.Single(index);
        Assert.Equal(301u, index[70][0]);
    }

    // Deterministic rather than throwing, should the sheet ever list a set id twice.
    [Fact]
    public void Keeps_the_first_row_when_a_set_appears_more_than_once()
    {
        var index = MirageSetIndex.Build(new[]
        {
            (SetItemId: 80u, PieceItemIds: new uint[] { 401, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 80u, PieceItemIds: new uint[] { 999, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        });

        Assert.Equal(401u, index[80][0]);
    }

    [Fact]
    public void An_empty_sheet_yields_an_empty_index()
    {
        Assert.Empty(MirageSetIndex.Build(Array.Empty<(uint, uint[])>()));
    }

    // No bits set means the outfit is empty (every piece withdrawn), so nothing is stored.
    [Fact]
    public void No_bits_set_yields_nothing()
    {
        var pieces = new uint[] { 101, 102, 103, 0, 0, 0, 0, 0, 0, 0, 0 };

        Assert.Empty(MirageSetIndex.StoredPieces(pieces, 0));
    }

    // Bit i selects the piece at column i. Checked across every column so a bit-order slip anywhere
    // in the 11 slots is caught, not just at the ends.
    [Fact]
    public void Each_single_bit_selects_its_own_column()
    {
        var pieces = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        for (var column = 0; column < 11; column++)
        {
            var bits = (ushort)(1 << column);

            Assert.Equal(new[] { pieces[column] }, MirageSetIndex.StoredPieces(pieces, bits).ToArray());
        }
    }

    // A partly-stored outfit: two pieces in, the rest withdrawn.
    [Fact]
    public void Multiple_bits_select_their_pieces()
    {
        var pieces = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var bits = (ushort)((1 << 1) | (1 << 3)); // OffHand + Body

        Assert.Equal(new uint[] { 11, 13 }, MirageSetIndex.StoredPieces(pieces, bits).ToArray());
    }

    // A fully-stored outfit selects every non-empty column.
    [Fact]
    public void All_eleven_bits_select_every_non_empty_piece()
    {
        var pieces = new uint[] { 10, 0, 12, 0, 14, 0, 16, 0, 18, 0, 20 };

        var stored = MirageSetIndex.StoredPieces(pieces, 0x7FF).ToArray();

        Assert.Equal(new uint[] { 10, 12, 14, 16, 18, 20 }, stored);
    }

    // Only 11 slots exist; a stray high bit (from padding or a wider field) must not read past the
    // piece array or invent a piece.
    [Fact]
    public void Bits_above_the_slot_count_are_ignored()
    {
        var pieces = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var bits = unchecked((ushort)0xF800); // bits 11..15 set, all above the 11 slots

        Assert.Empty(MirageSetIndex.StoredPieces(pieces, bits));
    }

    // A set bit over an empty column contributes nothing — the outfit has no piece there. (Armor
    // outfits leave, e.g., the weapon slots empty, so this is the common case, not an edge.)
    [Fact]
    public void A_set_bit_over_an_empty_column_is_skipped()
    {
        var pieces = new uint[] { 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var bits = (ushort)((1 << 0) | (1 << 1)); // MainHand (empty) + OffHand (present)

        Assert.Equal(new uint[] { 11 }, MirageSetIndex.StoredPieces(pieces, bits).ToArray());
    }

    // A piece array shorter than the 11 slots (a defensive guard, since Build always emits 11-wide
    // rows) must still never be read past its end, even when a high bit points at a missing slot.
    [Fact]
    public void A_bit_past_the_end_of_a_short_array_reads_nothing()
    {
        var pieces = new uint[] { 10, 11 }; // only two slots present

        // Bit 5 would select a sixth slot that does not exist in this array.
        Assert.Empty(MirageSetIndex.StoredPieces(pieces, 1 << 5));
        // The bits that DO fall inside the array still resolve normally.
        Assert.Equal(new uint[] { 10, 11 }, MirageSetIndex.StoredPieces(pieces, 0b11).ToArray());
    }

    // Two columns can name the same item (e.g. a matched ring pair). Each stored slot is a separate
    // physical copy, so the id is yielded once per set bit — the tally sums them into a count of two.
    [Fact]
    public void A_piece_shared_by_two_columns_is_yielded_once_per_stored_slot()
    {
        var pieces = new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 500, 500 }; // Bracelets + Ring, same id
        var bits = (ushort)((1 << 9) | (1 << 10));

        Assert.Equal(new uint[] { 500, 500 }, MirageSetIndex.StoredPieces(pieces, bits).ToArray());
    }
}
