using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Tests for item tally aggregation and possession emission. The explicit-zero rule: every
/// scanned manifest id emits an entry, even at count zero — a present entry is a reported
/// fact, an absent id carries no information. What the server does with a count is its call
/// per id (see docs/api-contract.md); the plugin's job is only to report honestly.
/// </summary>
public class ItemTallyTests
{
    [Fact]
    public void An_unheld_manifest_id_emits_an_explicit_zero()
    {
        var result = ItemTallies.BuildPossessions(
            manifest: new uint[] { 42 },
            live: new Dictionary<uint, ItemTally>(),
            cached: new Dictionary<uint, ItemTally>());

        var entry = Assert.Single(result);
        Assert.Equal(42u, entry.Id);
        Assert.Equal(0u, entry.Count);
        Assert.Null(entry.HqCount);            // zero optional counts are OMITTED, not written
        Assert.Null(entry.CollectableCount);
        Assert.True(entry.Fresh);              // the live scan ran; caches only ever ADD to a count
    }

    [Fact]
    public void Live_and_cached_counts_sum_and_a_cache_contribution_marks_the_entry_stale()
    {
        // 5 in the bags plus 300 on a retainer is 305 held; part of the number came from a cache,
        // so the entry is reported not-fresh.
        var live = new Dictionary<uint, ItemTally> { [42] = new(Nq: 5, Hq: 0, Collectable: 0) };
        var cached = new Dictionary<uint, ItemTally> { [42] = new(Nq: 300, Hq: 0, Collectable: 0) };

        var entry = Assert.Single(ItemTallies.BuildPossessions(
            manifest: new uint[] { 42 }, live: live, cached: cached));
        Assert.Equal(305u, entry.Count);
        Assert.False(entry.Fresh);
    }

    [Fact]
    public void Qualities_are_reported_separately_never_summed()
    {
        var live = new Dictionary<uint, ItemTally> { [42] = new(Nq: 1, Hq: 2, Collectable: 3) };

        var entry = Assert.Single(ItemTallies.BuildPossessions(
            manifest: new uint[] { 42 }, live: live, cached: new Dictionary<uint, ItemTally>()));
        Assert.Equal(1u, entry.Count);
        Assert.Equal(2u, entry.HqCount);
        Assert.Equal(3u, entry.CollectableCount);
    }

    [Fact]
    public void Id_zero_is_skipped_and_duplicate_manifest_ids_emit_one_entry()
    {
        // Id 0 matches the padding in the game's cached arrays, and the server rejects the whole
        // upload over a single invalid id. A duplicate manifest id would double-report the item.
        var live = new Dictionary<uint, ItemTally> { [42] = new(Nq: 1, Hq: 0, Collectable: 0) };

        var result = ItemTallies.BuildPossessions(
            manifest: new uint[] { 0, 42, 42 },
            live: live,
            cached: new Dictionary<uint, ItemTally>());

        var entry = Assert.Single(result);
        Assert.Equal(42u, entry.Id);
    }

    [Fact]
    public void Tallies_accumulate_per_quality()
    {
        var total = new ItemTally(1, 2, 3).Add(new ItemTally(10, 20, 30));

        Assert.Equal(new ItemTally(11, 22, 33), total);
    }

    [Fact]
    public void A_cached_only_item_reports_cached_counts_with_fresh_false()
    {
        // The live containers were scanned and readable (or the scan wouldn't have run), so a live
        // absence (item not found in any live container) is a real zero. A cache presence with
        // no live match means the count came from a previous scan, not this one.
        var cached = new Dictionary<uint, ItemTally> { [42] = new(Nq: 100, Hq: 20, Collectable: 0) };

        var entry = Assert.Single(ItemTallies.BuildPossessions(
            manifest: new uint[] { 42 },
            live: new Dictionary<uint, ItemTally>(),
            cached: cached));
        Assert.Equal(100u, entry.Count);
        Assert.Equal(20u, entry.HqCount);
        Assert.Null(entry.CollectableCount);  // zero collectables are omitted
        Assert.False(entry.Fresh);
    }

    [Fact]
    public void Mixed_quality_contributions_merge_per_quality_and_any_cache_share_marks_stale()
    {
        // Live holds NQ and HQ copies; only the collectables came from a cache. Each quality is
        // still reported from its own source — never blended — and the single cached quality is
        // enough to mark the whole entry not-fresh.
        var live = new Dictionary<uint, ItemTally> { [42] = new(Nq: 5, Hq: 2, Collectable: 0) };
        var cached = new Dictionary<uint, ItemTally> { [42] = new(Nq: 0, Hq: 0, Collectable: 3) };

        var entry = Assert.Single(ItemTallies.BuildPossessions(
            manifest: new uint[] { 42 }, live: live, cached: cached));
        Assert.Equal(5u, entry.Count);
        Assert.Equal(2u, entry.HqCount);
        Assert.Equal(3u, entry.CollectableCount);
        Assert.False(entry.Fresh);
    }

    [Fact]
    public void An_empty_manifest_emits_no_entries()
    {
        var result = ItemTallies.BuildPossessions(
            manifest: Array.Empty<uint>(),
            live: new Dictionary<uint, ItemTally>(),
            cached: new Dictionary<uint, ItemTally>());

        Assert.Empty(result);
    }

    [Fact]
    public void An_id_outside_the_manifest_is_never_reported()
    {
        // The server names the ids it wants; anything else the scan happened to tally stays
        // off the wire. Only the manifest drives the output.
        var live = new Dictionary<uint, ItemTally> { [99] = new(Nq: 5, Hq: 0, Collectable: 0) };

        var result = ItemTallies.BuildPossessions(
            manifest: new uint[] { 42 },
            live: live,
            cached: new Dictionary<uint, ItemTally>());

        var entry = Assert.Single(result);
        Assert.Equal(42u, entry.Id);
    }

    [Fact]
    public void Entries_come_out_in_manifest_order()
    {
        // Entries mirror the order of the manifest the server sent — a deterministic, obvious
        // order that makes uploads reproducible and logs easy to compare against the manifest.
        // Iterating one of the dictionaries instead would make the order arbitrary.
        var live = new Dictionary<uint, ItemTally>
        {
            [7] = new(Nq: 1, Hq: 0, Collectable: 0),
            [9] = new(Nq: 1, Hq: 0, Collectable: 0),
            [3] = new(Nq: 1, Hq: 0, Collectable: 0),
        };

        var result = ItemTallies.BuildPossessions(
            manifest: new uint[] { 7, 3, 9 },
            live: live,
            cached: new Dictionary<uint, ItemTally>());

        Assert.Equal(3, result.Count);
        Assert.Equal(7u, result[0].Id);
        Assert.Equal(3u, result[1].Id);
        Assert.Equal(9u, result[2].Id);
    }
}
