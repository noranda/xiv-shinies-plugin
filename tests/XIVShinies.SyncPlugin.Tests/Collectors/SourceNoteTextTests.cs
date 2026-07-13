using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// SourceNoteText is the source-note twin of CollectSkipReasons: it turns a (source key, scan status)
// pair into one short line for the settings window. Like that helper it is pure and Dalamud-free, so
// every line the window can draw is pinned down here rather than only being seen in game. The window's
// job then shrinks to "draw whatever non-null string this returns", with no source-name branch of its
// own — the same extensibility discipline the rest of the settings surface follows.
public class SourceNoteTextTests
{
    private static ItemSourceStatus Status(string state, int? count = null, int? total = null) =>
        new() { State = state, Count = count, Total = total };

    [Fact]
    public void Inventory_read_live_reports_that_it_was_read_live()
    {
        Assert.Equal(
            "Inventory: read live.",
            SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Live)));
    }

    [Fact]
    public void A_cached_saddlebag_says_it_came_from_cache_and_how_to_refresh_it()
    {
        Assert.Equal(
            "Saddlebag: from its cache — open it once in game to refresh.",
            SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Cached)));
    }

    [Fact]
    public void An_unscanned_saddlebag_says_it_is_not_scanned_yet_and_how_to_include_it()
    {
        Assert.Equal(
            "Saddlebag: not scanned yet — open it once in game to include it.",
            SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Unscanned)));
    }

    // The fraction is the load-bearing part of the retainer line: with fewer scanned than exist,
    // the hint targets the never-summoned rest, because they contribute nothing until summoned.
    [Fact]
    public void Cached_retainers_with_some_missing_render_the_fraction_and_target_the_rest()
    {
        Assert.Equal(
            "Retainers: 3/5 scanned from cache — summon the rest once at a Summoning Bell to include them.",
            SourceNoteText.Describe(
                SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 5)));
    }

    [Fact]
    public void Cached_retainers_all_scanned_render_the_full_fraction_with_a_refresh_hint()
    {
        Assert.Equal(
            "Retainers: 3/3 scanned from cache — summon a retainer to refresh it.",
            SourceNoteText.Describe(
                SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 3)));
    }

    // Without a known total (the game has not delivered the retainer list yet), the line falls
    // back to the scanned count alone rather than inventing a denominator.
    [Fact]
    public void Cached_retainers_without_a_total_render_the_scanned_count_alone()
    {
        Assert.Equal(
            "Retainers: 3 scanned from cache — summon each retainer to refresh it.",
            SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached, count: 3)));
    }

    [Fact]
    public void Cached_retainers_without_a_count_omit_the_number()
    {
        Assert.Equal(
            "Retainers: from cache — summon each retainer to refresh it.",
            SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached)));
    }

    [Fact]
    public void Unscanned_retainers_say_they_are_not_scanned_yet_and_how_to_include_them()
    {
        Assert.Equal(
            "Retainers: not scanned yet — summon each retainer once at a Summoning Bell to include them.",
            SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Unscanned)));
    }

    [Fact]
    public void A_loaded_armoire_says_its_contents_were_read()
    {
        Assert.Equal(
            "Armoire: loaded and read.",
            SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Loaded)));
    }

    [Fact]
    public void An_unscanned_armoire_says_it_is_not_opened_yet_and_how_to_include_it()
    {
        Assert.Equal(
            "Armoire: not opened yet — open it once in game to include it.",
            SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Unscanned)));
    }

    [Fact]
    public void A_cached_glamour_dresser_says_it_came_from_cache_and_how_to_refresh_it()
    {
        Assert.Equal(
            "Glamour Dresser: from its cache — open it once in game to refresh.",
            SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Cached)));
    }

    [Fact]
    public void An_unscanned_glamour_dresser_says_it_is_not_scanned_yet_and_how_to_include_it()
    {
        Assert.Equal(
            "Glamour Dresser: not scanned yet — open it once in game to include it.",
            SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Unscanned)));
    }

    // An unrecognized source key means nothing to draw: the window shows the note only when this
    // returns non-null, so a future source the copy set has not caught up with draws nothing rather
    // than a raw wire string.
    [Fact]
    public void An_unknown_source_key_returns_null()
    {
        Assert.Null(SourceNoteText.Describe("facewearCabinet", Status(SourceStates.Live)));
    }

    // A known source in a state this helper has no line for (for example an inventory that somehow
    // reported "unscanned") is likewise nothing to say — better silence than an unhandled combination.
    [Fact]
    public void A_known_source_in_an_unexpected_state_returns_null()
    {
        Assert.Null(SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Unscanned)));
    }

    [Fact]
    public void A_completely_unknown_state_returns_null()
    {
        Assert.Null(SourceNoteText.Describe(SourceKeys.Saddlebag, Status("teleported")));
    }
}
