using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Enforces the contract's payload caps client-side, so an over-cap category is truncated rather
// than costing the whole upload a 400. Pure: it never reads the game or the network.
public class PayloadCapsTests
{
    // Builds an id-list array (numbers) with the given count, as SyncFacts.Ids would produce.
    private static JsonArray IdArray(int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
            array.Add(JsonValue.Create((uint)(i + 1)));

        return array;
    }

    // Builds an entry-list array (objects) with the given count, as SyncFacts.Items would produce.
    private static JsonArray EntryArray(int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            array.Add(new JsonObject
            {
                ["id"] = i + 1,
                ["count"] = 1,
                ["fresh"] = true,
            });
        }

        return array;
    }

    [Fact]
    public void An_id_array_over_the_cap_is_truncated_to_exactly_the_cap()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(PayloadCaps.MaxIdsPerCategory + 1),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(PayloadCaps.MaxIdsPerCategory, ((JsonArray)bounded["quests"]).Count);
        Assert.Single(dropped);
    }

    [Fact]
    public void An_id_array_over_the_cap_names_the_category_and_the_number_dropped()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(PayloadCaps.MaxIdsPerCategory + 1),
        };

        var (_, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(
            $"quests: dropped 1 entries over the contract cap of {PayloadCaps.MaxIdsPerCategory}",
            Assert.Single(dropped));
    }

    [Fact]
    public void An_entry_array_over_the_cap_is_truncated_to_exactly_the_cap()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["items"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 1),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(PayloadCaps.MaxEntriesPerCategory, ((JsonArray)bounded["items"]).Count);
        Assert.Single(dropped);
    }

    [Fact]
    public void An_entry_array_over_the_cap_reports_the_drop()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["items"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 1),
        };

        var (_, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(
            $"items: dropped 1 entries over the contract cap of {PayloadCaps.MaxEntriesPerCategory}",
            Assert.Single(dropped));
    }

    [Fact]
    public void A_compliant_payload_comes_back_with_no_report_entries()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(5),
            ["items"] = EntryArray(5),
        };

        var (_, dropped) = PayloadCaps.Bound(collections);

        Assert.Empty(dropped);
    }

    [Fact]
    public void A_compliant_payload_is_returned_by_the_same_dictionary_instance()
    {
        // No truncation happened, so nothing needed to be rebuilt: bounding a compliant payload
        // must not allocate a new dictionary, and its values must be the very same JsonNode
        // instances that were passed in (no needless copying).
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(5),
        };

        var (bounded, _) = PayloadCaps.Bound(collections);

        Assert.Same(collections, bounded);
        Assert.Same(collections["quests"], bounded["quests"]);
    }

    [Fact]
    public void An_empty_array_passes_through_untouched()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = new JsonArray(),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Empty(dropped);
        Assert.Same(collections["quests"], bounded["quests"]);
    }

    [Fact]
    public void A_non_array_category_value_passes_through_untouched()
    {
        // The caps are count limits on arrays — any other JSON shape a category carries (the
        // questSequences object, for example) is not this class's business and rides through
        // untouched.
        var collections = new Dictionary<string, JsonNode>
        {
            ["future"] = new JsonObject { ["someFlag"] = true },
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Empty(dropped);
        Assert.Same(collections, bounded);
        Assert.Same(collections["future"], bounded["future"]);
    }

    [Fact]
    public void Multiple_over_cap_categories_are_each_truncated_with_their_own_report_line()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(PayloadCaps.MaxIdsPerCategory + 2),
            ["items"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 3),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(PayloadCaps.MaxIdsPerCategory, ((JsonArray)bounded["quests"]).Count);
        Assert.Equal(PayloadCaps.MaxEntriesPerCategory, ((JsonArray)bounded["items"]).Count);
        Assert.Equal(2, dropped.Count);
        Assert.Contains(
            $"quests: dropped 2 entries over the contract cap of {PayloadCaps.MaxIdsPerCategory}",
            dropped);
        Assert.Contains(
            $"items: dropped 3 entries over the contract cap of {PayloadCaps.MaxEntriesPerCategory}",
            dropped);
    }
}
