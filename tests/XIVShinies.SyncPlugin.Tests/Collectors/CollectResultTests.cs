using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// CollectResult is the boundary every collector's output passes through, so the contract's
// "IDs are positive integers" rule is enforced here once rather than in each game-touching class.
public class CollectResultTests
{
    private static uint[] IdsOf(CollectResult result) =>
        result.Facts!.AsArray().GetValues<uint>().ToArray();

    // The game's sheets start with a blank row 0. A single zero on the wire makes the server reject
    // the ENTIRE upload, so it must never leave this method.
    [Fact]
    public void Ids_drops_zero_because_the_server_requires_positive_integers()
    {
        var result = CollectResult.Ids(new uint[] { 0, 5, 0, 7 });

        Assert.Equal(new uint[] { 5, 7 }, IdsOf(result));
    }

    [Fact]
    public void Ids_preserves_order_and_duplicates_of_real_ids()
    {
        var result = CollectResult.Ids(new uint[] { 7, 5, 7 });

        Assert.Equal(new uint[] { 7, 5, 7 }, IdsOf(result));
    }

    // "I read the sheet and nothing was unlocked" is a real fact, distinct from a skip.
    [Fact]
    public void An_empty_id_list_is_still_a_collected_fact()
    {
        var result = CollectResult.Ids(Array.Empty<uint>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Empty(result.Facts!.AsArray());
    }

    // ...and a list that was ONLY padding collapses to an empty array, not to a skip.
    [Fact]
    public void A_list_of_only_zeroes_becomes_an_empty_array_not_a_skip()
    {
        var result = CollectResult.Ids(new uint[] { 0, 0 });

        Assert.True(result.WasCollected);
        Assert.Empty(result.Facts!.AsArray());
    }

    [Fact]
    public void A_skip_carries_its_reason_and_no_facts()
    {
        var result = CollectResult.Skipped(CollectSkipReasons.AchievementListNotLoaded);

        Assert.False(result.WasCollected);
        Assert.Equal("achievement_list_not_loaded", result.SkipReason);
        Assert.Null(result.Facts);
    }

    // An item COUNT of zero is legitimate per the contract, so Items must not filter the way Ids
    // does. (In practice the collector only reports positives, but the boundary must not lie.)
    [Fact]
    public void Items_does_not_filter_and_preserves_a_zero_count()
    {
        var result = CollectResult.Items(
            new[] { new ItemPossession { Id = 7851, Count = 0, Fresh = false } });

        var item = result.Facts!.AsArray()[0]!.AsObject();
        Assert.Equal(7851u, item["id"]!.GetValue<uint>());
        Assert.Equal(0u, item["count"]!.GetValue<uint>());
    }

    [Fact]
    public void Items_with_source_notes_carries_them_alongside_the_facts()
    {
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
            ["saddlebag"] = new ItemSourceStatus { State = SourceStates.Unscanned },
        };
        var items = new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } };

        var result = CollectResult.Items(items, sourceNotes);

        Assert.True(result.WasCollected);
        Assert.NotNull(result.Facts);
        Assert.NotNull(result.SourceNotes);
        Assert.Equal(2, result.SourceNotes.Count);
        Assert.Equal(SourceStates.Live, result.SourceNotes["inventory"].State);
        Assert.Equal(SourceStates.Unscanned, result.SourceNotes["saddlebag"].State);
    }

    [Fact]
    public void Items_without_source_notes_has_null_source_notes()
    {
        var items = new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } };

        var result = CollectResult.Items(items);

        Assert.True(result.WasCollected);
        Assert.Null(result.SourceNotes);
    }

    [Fact]
    public void Sequences_carries_an_object_keyed_by_quest_id()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte> { [70562] = 3 });

        Assert.True(result.WasCollected);
        Assert.Equal(3, result.Facts!.AsObject()["70562"]!.GetValue<int>());
    }

    // "Every manifested quest was checked and none is in the journal" is a real fact, distinct
    // from a skip — the same collected-vs-skipped line the Ids and Items factories draw.
    [Fact]
    public void An_empty_sequence_map_is_still_a_collected_fact()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Empty(result.Facts!.AsObject());
    }

    // The same positive-integer defense Ids applies: the server rejects the whole upload over an
    // invalid id, and this boundary is the one funnel where no collector can forget the rule.
    [Fact]
    public void Sequences_drops_a_zero_quest_id()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte> { [0] = 2, [70562] = 3 });

        var facts = result.Facts!.AsObject();
        Assert.False(facts.ContainsKey("0"));
        Assert.Equal(3, facts["70562"]!.GetValue<int>());
    }
}
