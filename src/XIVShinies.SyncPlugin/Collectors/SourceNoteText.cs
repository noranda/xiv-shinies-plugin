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
/// note. Both keep the same discipline: the window prints whatever non-null string it is handed and
/// never branches on which source or category produced it, so a new source can surface a note just by
/// reporting a status the copy set below already covers.
/// </para>
/// <para>
/// The copy leans on a simple split. When the source was read this pass (inventory live, armoire
/// loaded) the line just states that, calmly — there is nothing for the user to do. When the source
/// came from a stale cache the line adds a gentle refresh hint, and when it was never opened the line
/// says so and tells the user the one in-game action that would fold it in. The plugin cannot open
/// these containers itself, so every actionable line ends with the step the user must take.
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
    public static string? Describe(string sourceKey, ItemSourceStatus status) =>
        (sourceKey, status.State) switch
        {
            // Read live this pass — nothing for the user to do, so no follow-up hint.
            (SourceKeys.Inventory, SourceStates.Live) =>
                "Inventory: read live.",

            // Cached sources are real but possibly stale; the hint is a gentle "reopen to refresh".
            (SourceKeys.Saddlebag, SourceStates.Cached) =>
                "Saddlebag: from its cache — open it once in game to refresh.",

            // Never opened, so it contributes nothing yet; the hint is the one action that includes it.
            (SourceKeys.Saddlebag, SourceStates.Unscanned) =>
                "Saddlebag: not scanned yet — open it once in game to include it.",

            // Retainers carry counts: how many were read from cache and, when the game can say, how
            // many exist. The fraction is the load-bearing part — "3/5 scanned" tells the user two
            // retainers contribute nothing yet, which a bare "3" would hide.
            (SourceKeys.Retainers, SourceStates.Cached) =>
                DescribeCachedRetainers(status.Count, status.Total),

            (SourceKeys.Retainers, SourceStates.Unscanned) =>
                "Retainers: not scanned yet — summon each retainer once at a Summoning Bell to include them.",

            // The armoire's "loaded" state is its live-equivalent: the game fetches its contents when
            // the player first opens it each session, and once loaded it is current.
            (SourceKeys.Armoire, SourceStates.Loaded) =>
                "Armoire: loaded and read.",

            (SourceKeys.Armoire, SourceStates.Unscanned) =>
                "Armoire: not opened yet — open it once in game to include it.",

            (SourceKeys.GlamourDresser, SourceStates.Cached) =>
                "Glamour Dresser: from its cache — open it once in game to refresh.",

            (SourceKeys.GlamourDresser, SourceStates.Unscanned) =>
                "Glamour Dresser: not scanned yet — open it once in game to include it.",

            // Unknown source key, or a known source in a state with no line above: say nothing.
            _ => null,
        };

    // Both counts are optional and degrade gracefully: with a total the line shows the fraction
    // ("3/5 scanned") and tailors the hint to whether anything is missing; with only the scanned
    // count it shows that number alone; with neither it drops the numbers rather than printing a
    // bare "0" or an empty gap.
    private static string DescribeCachedRetainers(int? count, int? total) =>
        (count, total) switch
        {
            // Some retainers have never been summoned — the hint targets the missing ones.
            ({ } scanned, { } known) when scanned < known =>
                $"Retainers: {scanned}/{known} scanned from cache — summon the rest once at a " +
                "Summoning Bell to include them.",

            // Everything the character has is in the cache; the only action left is refreshing.
            ({ } scanned, { } known) =>
                $"Retainers: {scanned}/{known} scanned from cache — summon a retainer to refresh it.",

            ({ } scanned, null) =>
                $"Retainers: {scanned} scanned from cache — summon each retainer to refresh it.",

            _ => "Retainers: from cache — summon each retainer to refresh it.",
        };
}
