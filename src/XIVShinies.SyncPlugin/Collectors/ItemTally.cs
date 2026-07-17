using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>Per-quality possession counts for one item id.</summary>
/// <remarks>
/// <para>
/// A <c>readonly record struct</c> is a small immutable value type copied by value on
/// assignment (no heap allocation) — perfect for a tally that lives briefly inside one
/// collection pass. The positional syntax declares the three properties in one line.
/// It is like a frozen plain object in JavaScript, but stack-allocated.
/// </para>
/// <para>
/// The three qualities (NQ, HQ, Collectable) are tracked separately because the server
/// applies different rules per quality; the plugin never sums them, only reports each.
/// </para>
/// </remarks>
public readonly record struct ItemTally(uint Nq, uint Hq, uint Collectable)
{
    /// <summary>Combines two tallies of the same item, quality by quality.</summary>
    /// <remarks>
    /// Used to sum live and cached counts for an item. An empty tally (all zeros) added
    /// to any tally returns the original tally; this is the identity operation.
    /// </remarks>
    public ItemTally Add(ItemTally other) =>
        new(Nq + other.Nq, Hq + other.Hq, Collectable + other.Collectable);

    /// <summary>True when no copies were seen in any quality.</summary>
    /// <remarks>
    /// An all-qualities-zero check. <see cref="ItemTallies.BuildPossessions"/> applies it
    /// to the cached tally to compute freshness: any cache contribution, in any quality,
    /// marks the entry stale — even when live containers also hold copies.
    /// </remarks>
    public bool IsEmpty => Nq == 0 && Hq == 0 && Collectable == 0;
}

/// <summary>Turns scan tallies into wire entries, applying the explicit-zero rule.</summary>
/// <remarks>
/// <para>
/// The explicit-zero rule (see <c>docs/api-contract.md</c>): an entry PRESENT — even with
/// count 0 — is a reported fact for that id; an id ABSENT from the list was not scanned and
/// carries no information. What a count means is the server's call per id (relic proofs act
/// only on positive counts; count-tracked ids read the number as the current total). The
/// per-id distinction is what makes a zero trustworthy at all: reaching this method means
/// the live containers were readable (an unreadable inventory skips the whole pass), and
/// caches only ever ADD to a count — a cache can never turn a real zero into something else.
/// </para>
/// <para>
/// This method emits an entry for every valid manifest id (skipping zero and duplicates),
/// with one exception: an id in the server's omit-when-unseen set that NO source resolved is
/// left out entirely. Those ids are the content-bound currencies (see
/// <see cref="Api.ConfigResponse.ItemOmitWhenUnseenIds"/>): the game only exposes their
/// counts inside their content, so out-of-zone their absence means "not visible from here"
/// rather than "owns none" — and per the explicit-zero rule, the honest report for
/// no-information is no entry.
/// </para>
/// </remarks>
public static class ItemTallies
{
    /// <summary>One <see cref="ItemPossession"/> per manifest id — zeros included.</summary>
    /// <param name="manifest">The server-controlled list of valid item ids to report on.</param>
    /// <param name="live">
    /// Counts from containers read directly this pass (bags, equipped gear, armoury chest).
    /// Missing entries are treated as empty tallies (all qualities zero).
    /// </param>
    /// <param name="cached">
    /// Counts from the game's local caches (armoire, glamour dresser, saddlebags, retainers)
    /// — may be stale, and any contribution here marks the entry not fresh. Missing entries
    /// are treated as empty tallies.
    /// </param>
    /// <param name="omitWhenUnseen">
    /// The server's omit-when-unseen id set. An id in it that appears in NEITHER tally gets no
    /// entry instead of the explicit zero; an id either tally resolved — any count, from any
    /// source — reports normally. Null (an older server, or none configured) omits nothing.
    /// </param>
    /// <returns>
    /// A list of possessions in manifest order, one per valid manifest id. Every entry is
    /// fresh if the cache contributed nothing to it; stale if the cache contributed any
    /// quality. A zero entry (all qualities zero) is fresh and emitted with Count only
    /// (HqCount and CollectableCount omitted).
    /// </returns>
    /// <remarks>
    /// Call with named arguments — the two dictionaries share a type, and transposing them
    /// silently inverts every Fresh flag.
    /// </remarks>
    public static IReadOnlyList<ItemPossession> BuildPossessions(
        IReadOnlyList<uint> manifest,
        IReadOnlyDictionary<uint, ItemTally> live,
        IReadOnlyDictionary<uint, ItemTally> cached,
        IReadOnlySet<uint>? omitWhenUnseen = null)
    {
        // Pre-sized to the manifest length — the common case is every id valid and unique.
        var result = new List<ItemPossession>(manifest.Count);
        var seen = new HashSet<uint>();

        foreach (var id in manifest)
        {
            // Skip id 0 (padding in game arrays) and duplicate manifest entries.
            if (id == 0 || !seen.Add(id))
                continue;

            // Look up both sources, defaulting to empty tallies if not found.
            // "Resolved" is PRESENCE in a tally, not a nonzero count: the sources only record
            // ids they actually saw, so TryGetValue answering false on both means no source
            // laid eyes on this id at all this pass.
            var liveResolved = live.TryGetValue(id, out var liveTally);
            var cachedResolved = cached.TryGetValue(id, out var cachedTally);

            // The omit-when-unseen exception (see the class remarks): for a content-bound
            // currency the game is not currently exposing, silence is the honest report — an
            // explicit zero would overwrite the real count the server already holds.
            if (!liveResolved && !cachedResolved && omitWhenUnseen?.Contains(id) == true)
                continue;

            // Combine live and cached counts quality by quality.
            var total = liveTally.Add(cachedTally);

            // An entry is fresh only if the cache contributed nothing (i.e., the live scan
            // ran and found what we're reporting, or found nothing and we're reporting zero).
            // If the cache added any quality, the count came from a previous scan, not this one.
            var fresh = cachedTally.IsEmpty;

            // Build the wire entry: Count is NQ only (the server decides HQ requirements).
            // HQ and Collectable counts are omitted (null) when zero (per the DTO contract).
            var entry = new ItemPossession
            {
                Id = id,
                Count = total.Nq,
                HqCount = total.Hq == 0 ? null : total.Hq,
                CollectableCount = total.Collectable == 0 ? null : total.Collectable,
                Fresh = fresh,
            };

            result.Add(entry);
        }

        return result;
    }
}
