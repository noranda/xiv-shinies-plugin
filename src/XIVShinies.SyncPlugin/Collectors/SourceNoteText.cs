using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns a per-source scan status into one short line for the settings window, or null when there is
/// nothing worth saying.
/// </summary>
/// <remarks>
/// <para>
/// This is the source-note twin of <see cref="CollectSkipReasons.Describe"/>. That helper maps a
/// category's skip <i>reason</i> to advice; this one maps a storage source's scan <i>status</i> to a
/// <see cref="SourceNote"/> — the line, and the tone that colors it. Both keep the same discipline:
/// what they hand back is printed as given, and nothing downstream branches on which source or category
/// produced it, so a new source can surface a note just by reporting a status the copy set below already
/// covers.
/// </para>
/// <para>
/// The tone carries the copy's meaning in three parts. <see cref="SourceTone.Live"/> is a source read
/// during this pass, stated calmly because there is nothing for the user to do.
/// <see cref="SourceTone.Cached"/> is a source whose contents the game only remembers — a healthy
/// resting state for the containers that work that way, so the line offers an optional refresh rather
/// than raising an alarm. <see cref="SourceTone.Missing"/> is a source contributing nothing at all yet,
/// and the plugin cannot open a container itself, so every one of those lines ends with the in-game
/// action that folds it in.
/// </para>
/// <para>
/// An unrecognized source key, or a known source in a state this set has no line for, returns null.
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
            // Read this pass — nothing for the user to do, so no follow-up hint. "read." is the single
            // phrase every healthy source uses, container or collection alike (ReadStatusView reuses
            // it for collections), so one green line means the same thing everywhere in the panel.
            (SourceKeys.Inventory, SourceStates.Live) =>
                new SourceNote { Text = "Inventory: read.", Tone = SourceTone.Live },

            // Cached is this source's healthy resting state: the game never exposes a live read of
            // the saddlebag, so nothing is wrong here. The line names the one action that refreshes
            // it, as an option rather than a demand.
            (SourceKeys.Saddlebag, SourceStates.Cached) =>
                new SourceNote
                {
                    Text = "Saddlebag: read from cache — open it once in game to refresh as needed.",
                    Tone = SourceTone.Cached,
                },

            // Never opened, so it contributes nothing yet; the hint is the one action that includes it.
            (SourceKeys.Saddlebag, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Text = "Saddlebag: not scanned yet — open it once in game to include it.",
                    Tone = SourceTone.Missing,
                },

            // Retainers carry counts: how many were read from cache and, when the game can say, how
            // many exist. The fraction is the load-bearing part — "3/5 read" tells the user two
            // retainers contribute nothing yet, which a bare "3" would hide. The tone varies with
            // those counts rather than with the state alone, so this arm delegates both.
            (SourceKeys.Retainers, SourceStates.Cached) =>
                DescribeCachedRetainers(status.Count, status.Total),

            (SourceKeys.Retainers, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Text = "Retainers: not scanned yet — summon each retainer once at a Summoning Bell " +
                        "to include them.",
                    Tone = SourceTone.Missing,
                },

            // The armoire's "loaded" state is its live-equivalent: the game fetches its contents when
            // the player first opens it each session, and once loaded it is current. "Loaded" is the
            // game's own vocabulary and stays out of the player's line: a loaded armoire is a source
            // that was read, so it says exactly what every other read source says.
            (SourceKeys.Armoire, SourceStates.Loaded) =>
                new SourceNote { Text = "Armoire: read.", Tone = SourceTone.Live },

            (SourceKeys.Armoire, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Text = "Armoire: not opened yet — open it once in game to include it.",
                    Tone = SourceTone.Missing,
                },

            // Same as the saddlebag above: Cached is the glamour dresser's normal resting state, not
            // a problem, so the hint offers an optional refresh.
            (SourceKeys.GlamourDresser, SourceStates.Cached) =>
                new SourceNote
                {
                    Text = "Glamour Dresser: read from cache — open it once in game to refresh as needed.",
                    Tone = SourceTone.Cached,
                },

            (SourceKeys.GlamourDresser, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Text = "Glamour Dresser: not scanned yet — open it once in game to include it.",
                    Tone = SourceTone.Missing,
                },

            // Unknown source key, or a known source in a state with no line above: say nothing.
            _ => null,
        };

    // Both counts are optional and degrade gracefully: with a total the line shows the fraction
    // ("3/5 read") and tailors the hint to whether anything is missing; with only the scanned
    // count it shows that number alone; with neither it drops the numbers rather than printing a
    // bare "0" or an empty gap.
    //
    // The tone depends on the counts, not on the state alone, which is why this hands back a whole
    // note — text and tone together. A never-summoned retainer hides its ENTIRE contents from the
    // plugin, exactly like a saddlebag that has never been opened, so it is Missing, not Cached: the
    // counts the user sees are a floor, not a total, and summoning the rest changes the answer.
    private static SourceNote DescribeCachedRetainers(int? count, int? total) =>
        (count, total) switch
        {
            // Some retainers have never been summoned, so they contribute nothing at all — the hint
            // targets exactly those.
            ({ } scanned, { } known) when scanned < known =>
                new SourceNote
                {
                    Text = $"Retainers: {scanned}/{known} read from cache — summon the rest once " +
                        "at a Summoning Bell to include them.",
                    Tone = SourceTone.Missing,
                },

            // Everything the character has is already in the cache — the healthy resting state, not
            // a gap to close, so the hint is an optional refresh and the tone is Cached.
            //
            // The fraction is clamped because the two numbers come from different places: the cache
            // remembers every retainer it has ever read, while the count of retainers is what the
            // character has now. Dismiss one and the cache still holds it, which would otherwise print
            // an impossible "4/3" at the user.
            ({ } scanned, { } known) =>
                new SourceNote
                {
                    Text = $"Retainers: {Math.Min(scanned, known)}/{known} read from cache — summon a " +
                        "retainer to refresh as needed.",
                    Tone = SourceTone.Cached,
                },

            // The game only reports how many retainers the character owns once their list has been
            // fetched at a Summoning Bell, so until then a complete cache and a partial one look the
            // same. These two arms stay Cached — claiming Missing on a guess would nag a user who has
            // already summoned every retainer they own — and the hint names the cheap action that
            // settles the question, which is opening the list rather than summoning anyone.
            ({ } scanned, null) =>
                new SourceNote
                {
                    Text = $"Retainers: {scanned} read from cache — open the retainer list at a " +
                        "Summoning Bell to check for any not yet read.",
                    Tone = SourceTone.Cached,
                },

            _ => new SourceNote
            {
                Text = "Retainers: read from cache — open the retainer list at a Summoning Bell to " +
                    "check for any not yet read.",
                Tone = SourceTone.Cached,
            },
        };
}

/// <summary>
/// How trustworthy a source note's information is, so the settings window can color it without
/// knowing which source or scan state produced it.
/// </summary>
/// <remarks>
/// Three tones cover every note <see cref="SourceNoteText.Describe"/> can return: <see cref="Live"/>
/// for a source read fresh this pass, <see cref="Cached"/> for one whose numbers are real but possibly
/// stale, and <see cref="Missing"/> for one that has contributed nothing at all yet. The window maps
/// each tone to a color with a switch of its own — a switch on this enum, never on a source name or
/// wire state string, which is what keeps a future source's notes colorable without touching the
/// window's drawing code.
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
/// One source-note line for the settings window: the text to print and the tone that colors it.
/// </summary>
/// <remarks>
/// A note carries its own tone, which keeps the window a pure printer: it draws exactly the string
/// and color it is handed, with no branch of its own on which source or scan state is behind either.
/// </remarks>
public sealed record SourceNote
{
    /// <summary>The line to draw, already written for a player (never a raw wire string).</summary>
    public required string Text { get; init; }

    /// <summary>How trustworthy this note's information is, for the window to color it by.</summary>
    public required SourceTone Tone { get; init; }
}
