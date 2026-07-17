using System.Collections.Generic;
using System.Reflection;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// SourceNoteText is the source-note twin of CollectSkipReasons: it turns a (source key, scan status)
// pair into one note for the settings window. Like that helper it is pure and Dalamud-free, so every
// piece of copy the window can show is pinned down here rather than only being seen in game. The
// window's job then shrinks to "render whatever non-null note this returns", with no source-name
// branch of its own — the same extensibility discipline the rest of the settings surface follows.
//
// A note has two renderable forms, and the tone decides which the window uses. Live and Cached notes
// render as a compact chip: Label is the chip's text, and Detail — when present — is optional hover
// copy. Missing notes render as a full line: Text carries the sentence, including the in-game action
// that fixes the gap, which must stay visible rather than hide behind a hover. That rule is pinned as
// an invariant at the bottom of this file: every Missing note carries a Text.
public class SourceNoteTextTests
{
    private static ItemSourceStatus Status(string state, int? count = null, int? total = null) =>
        new() { State = state, Count = count, Total = total };

    // A healthy live source is a chip: the green check says "read", so the note carries no sentence.
    // The detail names exactly which containers the inventory chip speaks for — bags, equipped gear,
    // the armoury chest, crystals — so a user wondering "is my equipped weapon counted?" can find out
    // by hovering rather than by asking.
    [Fact]
    public void A_live_inventory_is_a_chip_naming_the_containers_it_covers()
    {
        var note = SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Live));

        Assert.Equal("Inventory", note!.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Bags, equipped gear, the armoury chest, and crystals — read directly this pass.",
            note.Detail);
    }

    // Currencies are scanned alongside the inventory (the Currency container plus the game's
    // currency subsystem), so they get a chip of their own — without it the panel would imply
    // currencies are never read. The detail also discloses the one gap in that coverage: the
    // game only exposes a content-bound currency (Occult Crescent's pieces, for example) while
    // the character is inside that content, so those counts sync from in-zone visits.
    [Fact]
    public void Live_currencies_are_a_chip_naming_what_they_cover_and_the_content_bound_gap()
    {
        var note = SourceNoteText.Describe(SourceKeys.Currencies, Status(SourceStates.Live));

        Assert.Equal("Currencies", note!.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Gil, tomestones, scrips, and the game's other currencies — read directly this " +
            "pass. Currencies bound to a specific piece of content, like Occult Crescent's " +
            "pieces, can only be read while inside it.",
            note.Detail);
    }

    // Cached is the saddlebag's healthy resting state, so it is a chip too; the optional refresh hint
    // moves into the hover detail. The premium half is named there — it travels under this same key.
    [Fact]
    public void A_cached_saddlebag_is_a_chip_with_the_refresh_hint_in_its_detail()
    {
        var note = SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Cached));

        Assert.Equal("Saddlebag", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Read from cache, including the premium saddlebag — open it once in game to refresh " +
            "as needed.",
            note.Detail);
    }

    // A never-opened source contributes nothing, and only the user can open it — so the note is a
    // full line whose visible text names the action, never a chip that hides it behind a hover.
    [Fact]
    public void An_unscanned_saddlebag_is_a_line_naming_the_action_that_includes_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Unscanned));

        Assert.Equal("Saddlebag", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("Saddlebag: not scanned yet — open it once in game to include it.", note.Text);
    }

    // The fraction is the load-bearing part of the partial-retainers line: with fewer scanned than
    // exist, the visible text targets the never-summoned rest, because they contribute nothing until
    // summoned.
    //
    // Missing, not Cached: a retainer that has never been summoned hides its ENTIRE contents from the
    // plugin, exactly like a saddlebag that has never been opened — so the counts are a floor rather
    // than a stale total, and summoning the rest is an action that genuinely changes the answer.
    [Fact]
    public void Cached_retainers_with_some_missing_are_a_line_with_the_fraction_targeting_the_rest()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 5));

        Assert.Equal("Retainers", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal(
            "Retainers: 3/5 read from cache — summon the rest once at a Summoning Bell to include them.",
            note.Text);
    }

    // With every retainer read the source is healthy, so it compresses into a chip. The fraction
    // stays visible in the chip's own label — it is information, not an action — while the optional
    // refresh hint moves into the hover detail.
    [Fact]
    public void Cached_retainers_all_scanned_are_a_chip_carrying_the_fraction_in_its_label()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 3));

        Assert.Equal("Retainers 3/3", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Each retainer's bags and equipped gear, read from cache — summon a retainer to " +
            "refresh as needed.",
            note.Detail);
    }

    // The cache remembers every retainer it has ever read, while the total is what the character has
    // now — dismiss one and the first number outgrows the second. The fraction is clamped so the chip
    // never claims an impossible "4/3".
    [Fact]
    public void Cached_retainers_never_report_more_read_than_the_character_has()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 4, total: 3));

        Assert.Equal("Retainers 3/3", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    // Without a known total the chip shows the count it does have, and the detail names the action
    // that reveals whether any retainer is missing from it: opening the list, which is what teaches
    // the game how many retainers the character owns.
    [Fact]
    public void Cached_retainers_without_a_total_carry_the_scanned_count_alone_in_the_label()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached, count: 3));

        Assert.Equal("Retainers 3", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Each retainer's bags and equipped gear, read from cache — open the retainer list at " +
            "a Summoning Bell to check for any not yet read.",
            note.Detail);
    }

    [Fact]
    public void Cached_retainers_without_a_count_omit_the_number_from_the_label()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached));

        Assert.Equal("Retainers", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Each retainer's bags and equipped gear, read from cache — open the retainer list at " +
            "a Summoning Bell to check for any not yet read.",
            note.Detail);
    }

    [Fact]
    public void Unscanned_retainers_are_a_line_naming_the_action_that_includes_them()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Unscanned));

        Assert.Equal("Retainers", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal(
            "Retainers: not scanned yet — summon each retainer once at a Summoning Bell to include them.",
            note.Text);
    }

    // The armoire's wire state is "loaded" rather than "live", but loaded IS current — so it carries
    // the Live tone and renders as the same green chip as every other healthy source. "Loaded" is the
    // game's vocabulary; the player never sees it.
    [Fact]
    public void A_loaded_armoire_is_a_healthy_chip()
    {
        var note = SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Loaded));

        Assert.Equal("Armoire", note!.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Null(note.Detail);
    }

    [Fact]
    public void An_unscanned_armoire_is_a_line_naming_the_action_that_includes_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Unscanned));

        Assert.Equal("Armoire", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("Armoire: not opened yet — open it once in game to include it.", note.Text);
    }

    [Fact]
    public void A_cached_glamour_dresser_is_a_chip_with_the_refresh_hint_in_its_detail()
    {
        var note = SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Cached));

        Assert.Equal("Glamour Dresser", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal("Read from cache — open it once in game to refresh as needed.", note.Detail);
    }

    [Fact]
    public void An_unscanned_glamour_dresser_is_a_line_naming_the_action_that_includes_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Unscanned));

        Assert.Equal("Glamour Dresser", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("Glamour Dresser: not scanned yet — open it once in game to include it.", note.Text);
    }

    // An unrecognized source key means nothing to show: the window renders the note only when this
    // returns non-null, so a future source the copy set has not caught up with shows nothing rather
    // than a raw wire string.
    [Fact]
    public void An_unknown_source_key_returns_null()
    {
        Assert.Null(SourceNoteText.Describe("facewearCabinet", Status(SourceStates.Live)));
    }

    // A known source in a state this helper has no note for (for example an inventory that somehow
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

    // THE INVARIANT the window's rendering rule leans on: a Missing note renders as a full line whose
    // visible text names the in-game action — so every Missing note must carry a Text. Hover may hide
    // optional info (a Detail); it must never hide a required action. Swept across every combination
    // of known source key, known state, and representative retainer counts, so a future arm added to
    // SourceNoteText cannot forget its sentence without failing here.
    [Fact]
    public void Every_missing_note_carries_the_full_line_text_its_action_lives_in()
    {
        foreach (var sourceKey in WireConstantsOf(typeof(SourceKeys)))
        {
            foreach (var state in WireConstantsOf(typeof(SourceStates)))
            {
                // The count shapes the retainer arms' answer, so each (key, state) pair is tried with
                // every count shape the collector can produce; other sources ignore the numbers.
                foreach (var (count, total) in new (int?, int?)[] { (null, null), (3, 5), (3, 3), (3, null) })
                {
                    var note = SourceNoteText.Describe(sourceKey, Status(state, count, total));

                    if (note is { Tone: SourceTone.Missing })
                        Assert.False(string.IsNullOrEmpty(note.Text));
                }
            }
        }
    }

    // Reads every public const string off a wire-constants class (SourceKeys, SourceStates) via
    // reflection, so the invariant sweep above automatically covers any constant added later.
    private static IEnumerable<string> WireConstantsOf(System.Type constantsClass)
    {
        foreach (var field in constantsClass.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is string value)
                yield return value;
        }
    }
}
