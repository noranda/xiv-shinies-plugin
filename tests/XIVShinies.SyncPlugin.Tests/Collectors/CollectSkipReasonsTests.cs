using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Turns a collector's skip reason into advice for the settings window. Note what it switches on: a
// REASON, never a category. That is what lets the "open your Achievements window" hint exist without
// anyone writing `if (key == "achievements")` anywhere in the plugin.
//
// Every described reason is drawn after its category's display name, as "{DisplayName}: {phrase}" (see
// ReadStatusViewTests), so each string is asserted whole: it has to READ correctly in that position,
// not merely contain the right keyword.
public class CollectSkipReasonsTests
{
    // The hint the extensibility contract names explicitly as the thing that must NOT be a special
    // case in the settings UI.
    [Fact]
    public void An_unloaded_achievement_list_becomes_advice_the_user_can_act_on()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.AchievementListNotLoaded);

        Assert.Equal(
            "not read yet — open your Achievements window in game once, then press Sync now.", hint);
    }

    // The whole string is asserted, not just a keyword: a keyword check would still pass if two
    // messages were swapped, and would tell the user to log in when the manifest is what is
    // missing. The string names no category-specific noun — see CollectSkipReasons.Describe for
    // why the reason is shared.
    [Fact]
    public void A_missing_manifest_explains_that_we_are_waiting_on_the_server()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoRemoteConfig);

        Assert.Equal(
            "not read yet — waiting for XIV Shinies to say what to look for.", hint);
    }

    [Fact]
    public void An_unreadable_inventory_asks_the_user_to_log_in()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.InventoryUnavailable);

        Assert.Equal(
            "not read yet — log in to a character so your inventory can be read.", hint);
    }

    // A collection whose consent groups are all switched off looks up nothing at all. The advice has to
    // name the one thing that resolves it, because nothing else will: the plugin cannot tick the boxes
    // for the user, and a collection that quietly uploads nothing forever is the failure this line
    // exists to prevent.
    [Fact]
    public void A_collection_with_no_groups_enabled_asks_the_user_to_tick_one()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoItemGroupsEnabled);

        Assert.Equal(
            "not read — none of its groups are switched on. Tick at least one under Collections to " +
            "include it.",
            hint);
    }

    // The checkbox beside the category already says it is off; repeating it would be noise.
    [Fact]
    public void A_disabled_category_needs_no_explanation()
    {
        Assert.Null(CollectSkipReasons.Describe(CollectSkipReasons.Disabled));
    }

    // Bugs and transient game states are not things the user can act on, and the raw wire string
    // would mean nothing to them.
    [Theory]
    [InlineData(CollectSkipReasons.CollectorError)]
    [InlineData(CollectSkipReasons.SheetUnavailable)]
    public void A_reason_the_user_cannot_act_on_produces_no_advice(string reason)
    {
        Assert.Null(CollectSkipReasons.Describe(reason));
    }

    // A reason invented by a future collector must not surface a raw code like "facewear_locked".
    [Fact]
    public void An_unrecognized_reason_produces_no_advice_rather_than_its_raw_code()
    {
        Assert.Null(CollectSkipReasons.Describe("some_future_reason"));
    }
}
