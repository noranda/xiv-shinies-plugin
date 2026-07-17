using System;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The armoire lookup is one of the pure parts of item collection (alongside MirageSetIndex and
// ItemTallies), so it is unit-tested here. Reading the game's containers is verified by in-game QA.
public class ArmoireIndexTests
{
    [Fact]
    public void Maps_each_item_to_the_row_that_stores_it()
    {
        // Both elements must name their fields, or C# infers an unnamed tuple for the whole array.
        var index = ArmoireIndex.Build(
            new[] { (CabinetId: 5u, ItemId: 7851u), (CabinetId: 9u, ItemId: 7852u) });

        Assert.Equal(5u, index[7851]);
        Assert.Equal(9u, index[7852]);
        Assert.Equal(2, index.Count);
    }

    // The game's sheet is padded with rows that reference no item.
    [Fact]
    public void Ignores_rows_that_hold_no_item()
    {
        var index = ArmoireIndex.Build(new[] { (0u, 0u), (1u, 0u), (2u, 7851u) });

        Assert.Single(index);
        Assert.Equal(2u, index[7851]);
    }

    // Deterministic rather than throwing, should the sheet ever list an item twice.
    [Fact]
    public void Keeps_the_first_row_when_an_item_appears_more_than_once()
    {
        var index = ArmoireIndex.Build(new[] { (3u, 7851u), (8u, 7851u) });

        Assert.Equal(3u, index[7851]);
    }

    [Fact]
    public void An_empty_sheet_yields_an_empty_index()
    {
        Assert.Empty(ArmoireIndex.Build(Array.Empty<(uint, uint)>()));
    }
}
