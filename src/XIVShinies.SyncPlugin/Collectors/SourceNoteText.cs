using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns a per-source scan status into one <see cref="SourceNote"/> for the settings window, or null
/// when there is nothing worth saying.
/// </summary>
/// <remarks>
/// <para>
/// This is the source-note twin of <see cref="CollectSkipReasons.Describe"/>. That helper maps a
/// category's skip <i>reason</i> to advice; this one maps a storage source's scan <i>status</i> to a
/// note — the copy, and the tone that colors it. Both keep the same discipline: what they hand back
/// is rendered as given, and nothing downstream branches on which source or category produced it, so
/// a new source can surface a note just by reporting a status the copy set below already covers.
/// </para>
/// <para>
/// The tone carries the note's meaning, and it also decides which of the note's two forms the window
/// renders (see <see cref="SourceNote"/>). <see cref="SourceTone.Live"/> is a source read during this
/// pass — healthy, nothing for the user to do, so it compresses into a small chip whose optional
/// <see cref="SourceNote.Detail"/> can name what the source covers. <see cref="SourceTone.Cached"/> is
/// a source whose contents the game only remembers — a healthy resting state for the containers that
/// work that way, so it is a chip too, and the optional refresh action lives in its hover detail
/// rather than raising an alarm. <see cref="SourceTone.Missing"/> is a source contributing nothing at
/// all yet, and the plugin cannot open a container itself — so the note is a full line whose visible
/// <see cref="SourceNote.Text"/> ends with the in-game action that folds the source in. Hover may
/// hide optional information; it must never hide a required action.
/// </para>
/// <para>
/// An unrecognized source key, or a known source in a state this set has no note for, returns null.
/// A raw wire string like <c>"unscanned"</c> means nothing to a player, so silence beats leaking it —
/// exactly as <see cref="CollectSkipReasons.Describe"/> swallows reasons it has no advice for.
/// </para>
/// </remarks>
public static class SourceNoteText
{
    /// <summary>Describes one source's scan status, or returns null when there is nothing to say.</summary>
    /// <param name="sourceKey">One of the <see cref="SourceKeys"/> wire keys naming the storage source.</param>
    /// <param name="status">That source's scan status from the most recent item pass.</param>
    // A tuple switch reads the (source, state) pair like a small table: each arm is one recognized
    // combination the item collector actually produces, and the final `_ => null` covers every
    // unknown key and every unexpected state at once. This mirrors CollectSkipReasons.Describe's
    // single-switch shape.
    public static SourceNote? Describe(string sourceKey, ItemSourceStatus status) =>
        (sourceKey, status.State) switch
        {
            // Read this pass — nothing for the user to do, so this is a chip. The detail names
            // exactly which containers travel under the one "inventory" key, so a user wondering
            // "is my equipped weapon counted?" can find out by hovering.
            (SourceKeys.Inventory, SourceStates.Live) =>
                new SourceNote
                {
                    Label = "Inventory",
                    Tone = SourceTone.Live,
                    Detail = "Bags, equipped gear, the armoury chest, and crystals — read directly " +
                        "this pass.",
                },

            // Currencies are read in the same live pass as the inventory, but they are their own
            // source: part of them lives outside any container, in the game's currency subsystem.
            // The detail says what the key covers in a player's vocabulary.
            (SourceKeys.Currencies, SourceStates.Live) =>
                new SourceNote
                {
                    Label = "Currencies",
                    Tone = SourceTone.Live,
                    Detail = "Gil, tomestones, scrips, and the game's other currencies — read " +
                        "directly this pass.",
                },

            // Cached is this source's healthy resting state: the game never exposes a live read of
            // the saddlebag, so nothing is wrong here and the note is a chip. The refresh action is
            // optional, so it lives in the hover detail — which also names the premium half, since
            // both halves travel under this one key.
            (SourceKeys.Saddlebag, SourceStates.Cached) =>
                new SourceNote
                {
                    Label = "Saddlebag",
                    Tone = SourceTone.Cached,
                    Detail = "Read from cache, including the premium saddlebag — open it once in " +
                        "game to refresh as needed.",
                },

            // Never opened, so it contributes nothing yet. The one action that includes it must stay
            // visible, so this is a full line, never a chip.
            (SourceKeys.Saddlebag, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Saddlebag",
                    Tone = SourceTone.Missing,
                    Text = "Saddlebag: not scanned yet — open it once in game to include it.",
                },

            // Retainers carry counts: how many were read from cache and, when the game can say, how
            // many exist. Whether that is healthy (a chip) or a gap (a line with the action) depends
            // on those counts rather than on the state alone, so this arm delegates the whole note.
            (SourceKeys.Retainers, SourceStates.Cached) =>
                DescribeCachedRetainers(status.Count, status.Total),

            (SourceKeys.Retainers, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Retainers",
                    Tone = SourceTone.Missing,
                    Text = "Retainers: not scanned yet — summon each retainer once at a Summoning " +
                        "Bell to include them.",
                },

            // The armoire's "loaded" state is its live-equivalent: the game fetches its contents when
            // the player first opens it each session, and once loaded it is current. "Loaded" is the
            // game's own vocabulary and stays out of the player's sight: a loaded armoire is simply a
            // healthy source, so it is the same green chip as every other read source.
            (SourceKeys.Armoire, SourceStates.Loaded) =>
                new SourceNote { Label = "Armoire", Tone = SourceTone.Live },

            (SourceKeys.Armoire, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Armoire",
                    Tone = SourceTone.Missing,
                    Text = "Armoire: not opened yet — open it once in game to include it.",
                },

            // Same as the saddlebag above: Cached is the glamour dresser's normal resting state, not
            // a problem, so the optional refresh action lives in the hover detail.
            (SourceKeys.GlamourDresser, SourceStates.Cached) =>
                new SourceNote
                {
                    Label = "Glamour Dresser",
                    Tone = SourceTone.Cached,
                    Detail = "Read from cache — open it once in game to refresh as needed.",
                },

            (SourceKeys.GlamourDresser, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Glamour Dresser",
                    Tone = SourceTone.Missing,
                    Text = "Glamour Dresser: not scanned yet — open it once in game to include it.",
                },

            // Unknown source key, or a known source in a state with no note above: say nothing.
            _ => null,
        };

    // Both counts are optional and degrade gracefully: with a total the chip's label shows the
    // fraction ("Retainers 3/3"); with only the scanned count it shows that number alone; with
    // neither it is a bare "Retainers" rather than a "0" or an empty gap.
    //
    // The tone — and with it the note's whole form — depends on the counts, not on the state alone,
    // which is why this hands back a whole note. A never-summoned retainer hides its ENTIRE contents
    // from the plugin, exactly like a saddlebag that has never been opened, so it is Missing, not
    // Cached: the counts the user sees are a floor, not a total, and summoning the rest changes the
    // answer — that action must stay visible, so the partial case is a full line.
    private static SourceNote DescribeCachedRetainers(int? count, int? total) =>
        (count, total) switch
        {
            // Some retainers have never been summoned, so they contribute nothing at all — the
            // visible text carries the fraction and targets exactly those.
            ({ } scanned, { } known) when scanned < known =>
                new SourceNote
                {
                    Label = "Retainers",
                    Tone = SourceTone.Missing,
                    Text = $"Retainers: {scanned}/{known} read from cache — summon the rest once " +
                        "at a Summoning Bell to include them.",
                },

            // Everything the character has is already in the cache — the healthy resting state, so a
            // chip. The fraction stays in the label (it is information, not an action), while the
            // optional refresh hint lives in the hover detail.
            //
            // The fraction is clamped because the two numbers come from different places: the cache
            // remembers every retainer it has ever read, while the count of retainers is what the
            // character has now. Dismiss one and the cache still holds it, which would otherwise show
            // an impossible "4/3".
            ({ } scanned, { } known) =>
                new SourceNote
                {
                    Label = $"Retainers {Math.Min(scanned, known)}/{known}",
                    Tone = SourceTone.Cached,
                    Detail = "Each retainer's bags and equipped gear, read from cache — summon a " +
                        "retainer to refresh as needed.",
                },

            // The game only reports how many retainers the character owns once their list has been
            // fetched at a Summoning Bell, so until then a complete cache and a partial one look the
            // same. These two arms stay Cached — claiming Missing on a guess would nag a user who has
            // already summoned every retainer they own — and the detail names the cheap action that
            // settles the question, which is opening the list rather than summoning anyone.
            ({ } scanned, null) =>
                new SourceNote
                {
                    Label = $"Retainers {scanned}",
                    Tone = SourceTone.Cached,
                    Detail = "Each retainer's bags and equipped gear, read from cache — open the " +
                        "retainer list at a Summoning Bell to check for any not yet read.",
                },

            _ => new SourceNote
            {
                Label = "Retainers",
                Tone = SourceTone.Cached,
                Detail = "Each retainer's bags and equipped gear, read from cache — open the " +
                    "retainer list at a Summoning Bell to check for any not yet read.",
            },
        };
}

/// <summary>
/// How trustworthy a source note's information is, so the settings window can render it without
/// knowing which source or scan state produced it.
/// </summary>
/// <remarks>
/// Three tones cover every note <see cref="SourceNoteText.Describe"/> can return: <see cref="Live"/>
/// for a source read fresh this pass, <see cref="Cached"/> for one whose numbers are real but possibly
/// stale, and <see cref="Missing"/> for one that has contributed nothing at all yet. The window maps
/// each tone to a color, an icon, and a form (Live and Cached notes render as compact chips, Missing
/// notes as full lines — see <see cref="SourceNote"/>) with switches of its own — switches on this
/// enum, never on a source name or wire state string, which is what keeps a future source's notes
/// renderable without touching the window's drawing code.
/// </remarks>
public enum SourceTone
{
    /// <summary>Read directly this pass (or, for the armoire, loaded and current) — the healthy state.</summary>
    Live,

    /// <summary>Real data from a local cache, but possibly stale since the source was last opened.</summary>
    Cached,

    /// <summary>Never opened this session, so the source has contributed nothing yet.</summary>
    Missing,
}

/// <summary>
/// One source note for the settings window's "Reading from:" panel, carrying everything both of its
/// renderable forms need: the compact chip (healthy sources) and the full line (sources needing an
/// in-game action).
/// </summary>
/// <remarks>
/// <para>
/// A note carries its own copy and tone, which keeps the window a pure printer: it renders exactly
/// what it is handed, with no branch of its own on which source or scan state is behind a note. The
/// tone picks the form — <see cref="SourceTone.Live"/> and <see cref="SourceTone.Cached"/> notes draw
/// as chips, <see cref="SourceTone.Missing"/> notes as full lines.
/// </para>
/// <para>
/// The split between the optional properties encodes one design rule: <b>hover may hide optional
/// information, never a required action.</b> <see cref="Detail"/> is hover-only and therefore only
/// ever carries copy the user can live without seeing; <see cref="Text"/> is always visible and is
/// where a required action belongs. Every Missing note the builders produce carries a
/// <see cref="Text"/> (a test sweeps the whole copy set to enforce it); the window still degrades
/// safely if one ever does not, by drawing the note as a chip instead.
/// </para>
/// </remarks>
public sealed record SourceNote
{
    /// <summary>The source's short display name — the chip form's entire visible text.</summary>
    public required string Label { get; init; }

    /// <summary>How trustworthy this note's information is; also selects the note's rendered form.</summary>
    public required SourceTone Tone { get; init; }

    /// <summary>
    /// The full-line form's sentence, already written for a player (never a raw wire string). Present
    /// on every Missing note, because the in-game action it names must stay visible.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Optional hover copy for the chip form: what the source covers, or an optional refresh action.
    /// Never the only home of a required action.
    /// </summary>
    public string? Detail { get; init; }
}
