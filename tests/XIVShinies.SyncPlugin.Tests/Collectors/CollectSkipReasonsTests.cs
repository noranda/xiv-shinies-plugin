using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Turns a collector's skip reason into advice for the settings window. Note what it switches on: a
// REASON, never a category. That is what lets the "open your Achievements window" hint exist without
// anyone writing `if (key == "achievements")` anywhere in the plugin.
public class CollectSkipReasonsTests
{
    // The hint the extensibility contract names explicitly as the thing that must NOT be a special
    // case in the settings UI.
    [Fact]
    public void An_unloaded_achievement_list_becomes_advice_the_user_can_act_on()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.AchievementListNotLoaded);

        Assert.NotNull(hint);
        Assert.Contains("Achievements window", hint);
    }

    // Assert a distinguishing phrase, not merely that something came back: swapping two messages
    // would otherwise pass, and would tell the user to log in when the manifest is what is missing.
    [Fact]
    public void A_missing_item_manifest_explains_that_we_are_waiting_on_the_server()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoRemoteConfig);

        Assert.NotNull(hint);
        Assert.Contains("items to look for", hint);
    }

    [Fact]
    public void An_unreadable_inventory_asks_the_user_to_log_in()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.InventoryUnavailable);

        Assert.NotNull(hint);
        Assert.Contains("Log in", hint);
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
