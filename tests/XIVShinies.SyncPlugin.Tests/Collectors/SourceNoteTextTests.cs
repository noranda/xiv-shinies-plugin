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

    // "read." is the one phrase every healthy source uses, container or collection alike (see
    // ReadStatusViewTests) — a reader scanning the panel should not have to work out whether three
    // different green phrasings mean three different things.
    [Fact]
    public void A_live_inventory_reports_that_it_was_read()
    {
        var note = SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Live));

        Assert.Equal("Inventory: read.", note!.Text);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    [Fact]
    public void A_cached_saddlebag_says_it_came_from_cache_and_how_to_refresh_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Cached));

        Assert.Equal(
            "Saddlebag: read from cache — open it once in game to refresh as needed.", note!.Text);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    [Fact]
    public void An_unscanned_saddlebag_says_it_is_not_scanned_yet_and_how_to_include_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Unscanned));

        Assert.Equal("Saddlebag: not scanned yet — open it once in game to include it.", note!.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // The fraction is the load-bearing part of the retainer line: with fewer scanned than exist,
    // the hint targets the never-summoned rest, because they contribute nothing until summoned.
    //
    // Missing, not Cached: a retainer that has never been summoned hides its ENTIRE contents from the
    // plugin, exactly like a saddlebag that has never been opened — so the counts are a floor rather
    // than a stale total, and summoning the rest is an action that genuinely changes the answer.
    [Fact]
    public void Cached_retainers_with_some_missing_render_the_fraction_and_target_the_rest()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 5));

        Assert.Equal(
            "Retainers: 3/5 read from cache — summon the rest once at a Summoning Bell to include them.",
            note!.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    [Fact]
    public void Cached_retainers_all_scanned_render_the_full_fraction_with_a_refresh_hint()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 3));

        Assert.Equal(
            "Retainers: 3/3 read from cache — summon a retainer to refresh as needed.",
            note!.Text);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    // The cache remembers every retainer it has ever read, while the total is what the character has
    // now — dismiss one and the first number outgrows the second. The fraction is clamped so the line
    // never claims an impossible "4/3".
    [Fact]
    public void Cached_retainers_never_report_more_read_than_the_character_has()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 4, total: 3));

        Assert.Equal(
            "Retainers: 3/3 read from cache — summon a retainer to refresh as needed.",
            note!.Text);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    // Without a known total the line reports the count it does have and names the action that
    // reveals whether any retainer is missing from it: opening the list, which is what teaches the
    // game how many retainers the character owns.
    [Fact]
    public void Cached_retainers_without_a_total_render_the_scanned_count_alone()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached, count: 3));

        Assert.Equal(
            "Retainers: 3 read from cache — open the retainer list at a Summoning Bell to check for " +
            "any not yet read.",
            note!.Text);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    [Fact]
    public void Cached_retainers_without_a_count_omit_the_number()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached));

        Assert.Equal(
            "Retainers: read from cache — open the retainer list at a Summoning Bell to check for " +
            "any not yet read.",
            note!.Text);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    [Fact]
    public void Unscanned_retainers_say_they_are_not_scanned_yet_and_how_to_include_them()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Unscanned));

        Assert.Equal(
            "Retainers: not scanned yet — summon each retainer once at a Summoning Bell to include them.",
            note!.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // The armoire's wire state is "loaded" rather than "live", but loaded IS current — so it carries the
    // Live tone and the same "read." phrasing as every other healthy source. "Loaded" is the game's
    // vocabulary; the player's line never uses it.
    [Fact]
    public void A_loaded_armoire_reports_that_it_was_read()
    {
        var note = SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Loaded));

        Assert.Equal("Armoire: read.", note!.Text);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    [Fact]
    public void An_unscanned_armoire_says_it_is_not_opened_yet_and_how_to_include_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Unscanned));

        Assert.Equal("Armoire: not opened yet — open it once in game to include it.", note!.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    [Fact]
    public void A_cached_glamour_dresser_says_it_came_from_cache_and_how_to_refresh_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Cached));

        Assert.Equal(
            "Glamour Dresser: read from cache — open it once in game to refresh as needed.", note!.Text);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    [Fact]
    public void An_unscanned_glamour_dresser_says_it_is_not_scanned_yet_and_how_to_include_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Unscanned));

        Assert.Equal("Glamour Dresser: not scanned yet — open it once in game to include it.", note!.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
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
