using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XIVShinies.SyncPlugin.Api;

// The game has two different things called "Cabinet": a memory struct (the armoire itself) and an
// Excel sheet (the list of what the armoire can hold). An alias keeps them apart.
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports how many of the server's requested items the local character possesses.
/// </summary>
/// <remarks>
/// <para>
/// Only the items in the server's manifest are ever looked at. A count matters twice over:
/// possession of a relic-stage item proves that step server-side, and for items the website tracks
/// by number (materials, currencies, tomestones) the count itself feeds a running total. A stale
/// positive is still useful for the proof case — an item that <i>was</i> there proves the step
/// happened even after it has been consumed — which is why cached sources are consulted at all and
/// why any cache contribution is reported honestly as not fresh.
/// </para>
/// <para>
/// Live containers (bags, equipped gear, the whole armoury chest, crystals, and currency) are read
/// directly this pass. The cache-backed sources — the armoire, the glamour dresser, the saddlebags,
/// and the player's own <b>retainers</b> — are read only once the player has opened each of them,
/// and the game keeps a local copy the plugin can read afterwards. The counts from every source are
/// <b>summed</b>: an item held in two places contributes from both. Summing (rather than a
/// live-then-cache fallback) is what lets a count-tracked total reflect copies parked on a retainer
/// or in the saddlebag in addition to those carried live. Relic weapons and tools are commonly
/// parked on a retainer, so omitting them would under-report an item the player owns.
/// </para>
/// <para>
/// Only the local player's own storage is read; no other character's data is touched, and no
/// retainer ID ever leaves the process.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and cannot
/// be unit-tested. The pure logic it builds on — <see cref="ArmoireIndex"/> (the armoire lookup) and
/// <see cref="ItemTallies.BuildPossessions"/> (turning the tallies into wire entries) — is covered by
/// tests; the container reads themselves are verified by in-game QA.
/// </para>
/// </remarks>
// `unsafe` allows raw pointers. FFXIVClientStructs maps the game's own memory layout, so its
// Instance() methods hand back pointers into the live game rather than managed objects. C# normally
// forbids this; the keyword is the explicit opt-in. There is no JS equivalent whatsoever.
public sealed unsafe class ItemCollector : ICollector
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    // Built on first use and reused. The sheet never changes while the game is running.
    private IReadOnlyDictionary<uint, uint>? armoireIndex;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    // Every live container walked once per pass. Ordering is cosmetic — each slot is matched against
    // the manifest independently — but grouping reads clearly: the four carried bags, the equipped
    // set, every armoury chest the game defines (we walk them all so nothing the manifest asks about
    // can hide in a slot we skipped — e.g. a soul crystal or an old belt), then crystals and
    // currency. `ArmoryFeets` and `ArmoryEar` keep the game's own historical spellings; verified
    // against FFXIVClientStructs rather than assumed.
    private static readonly InventoryType[] LiveContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
        InventoryType.Crystals,
        InventoryType.Currency,
    };

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy. Passed in from the registry rather than
    /// hardcoded here, so that every category is described in exactly one file.
    /// </param>
    /// <param name="dataManager">Dalamud's game data accessor, used to read the armoire sheet.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public ItemCollector(CategoryInfo info, IDataManager dataManager, IFramework framework)
    {
        this.info = info;
        this.dataManager = dataManager;
        this.framework = framework;
    }

    /// <inheritdoc/>
    public string CategoryKey => info.Key;

    /// <inheritdoc/>
    public string DisplayName => info.DisplayName;

    /// <inheritdoc/>
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        // Everything below dereferences raw game memory. Reading it off the framework thread races
        // the game's own writes, and the resulting access violation cannot be caught — so refuse.
        GameThread.EnsureFrameworkThread(framework, nameof(ItemCollector));

        // Without a config we do not know which items the server cares about. That is "could not
        // read", not "found nothing" — so skip rather than send an empty list.
        if (context.RemoteConfig is null)
            return CollectResult.Skipped(CollectSkipReasons.NoRemoteConfig);

        // The server offers consent groups and the user has switched all of them off. Nothing may be
        // looked for, so nothing is looked at — and a collection nothing was read for must say so
        // rather than report an empty set of facts, which would read as "your inventory was checked
        // and you own none of these". The settings window turns this reason into the one action that
        // resolves it (see CollectSkipReasons.Describe). The rule itself is pure, and lives in
        // ManifestConsent so it can be unit-tested; this class cannot be, as it reads game memory.
        if (ManifestConsent.GroupsOfferedButNoneEnabled(context))
            return CollectResult.Skipped(CollectSkipReasons.NoItemGroupsEnabled);

        // Read the computed manifest once: it is recomputed on each property read, and the scan below
        // must see one stable list.
        var manifest = context.ItemManifest;

        // An empty manifest is a real answer: the server asked about nothing, so we found nothing.
        // No source notes are attached here on purpose — with nothing to look for we never walk a
        // container or consult a cache, so claiming any source was "live" or "unscanned" would
        // describe a scan that never happened.
        if (manifest.Count == 0)
            return CollectResult.Items(Array.Empty<ItemPossession>());

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return CollectResult.Skipped(CollectSkipReasons.InventoryUnavailable);

        // The set of ids the manifest asks about, built once so each slot walked below is an O(1)
        // membership test. A HashSet also collapses any duplicate manifest entries, so a source that
        // holds exactly one copy (armoire, glamour dresser) is never counted twice.
        var manifestIds = new HashSet<uint>(manifest);

        // Two parallel tallies, keyed by item id: what the live containers hold, and what the caches
        // remember. They are combined per quality by ItemTallies.BuildPossessions at the end.
        var live = new Dictionary<uint, ItemTally>();
        var cached = new Dictionary<uint, ItemTally>();

        // Per-source scan status for the upload. Built from the same gating flags the scan itself
        // reads, so the note always matches what was actually consulted.
        var sourceNotes = new Dictionary<string, ItemSourceStatus>();

        // LIVE TALLY — walk every live container exactly once.
        foreach (var type in LiveContainers)
            TallyLiveContainer(inventory, type, manifestIds, live);

        // LIVE TALLY (continued) — currencies the container walk cannot see. The walk above covers
        // InventoryType.Currency (the common currencies on the in-game Currency tab), but the game
        // tracks a second class of "currency" items in CurrencyManager instead — its buckets include
        // scrips, Bicolor Gemstones, Trophy Crystals, ventures, and Island Sanctuary materials (per
        // the FFXIVClientStructs docs). Which subsystem holds a given id is the game's business and
        // not always guessable, so this simply resolves whatever the walk missed, by item id.
        TallyCurrencyFallback(manifestIds, live);

        // Reaching this point means the inventory manager was readable and every live container was
        // walked, so the inventory source is genuinely live this pass. The currency-manager fallback
        // above reads the same current game memory, so its counts are live too; whether it ran at all
        // does not change this note (a null manager leaves the walk's results standing).
        sourceNotes[SourceKeys.Inventory] = new ItemSourceStatus { State = SourceStates.Live };

        // CACHED TALLY — the armoire, glamour dresser, saddlebags, and retainers, plus their notes.
        BuildCachedTallies(manifestIds, cached, sourceNotes);

        // ItemTallies applies the explicit-zero rule (one entry per valid manifest id) and the
        // freshness rule (any cache contribution marks the entry not fresh). Named arguments are
        // mandatory: the two dictionaries share a type, and transposing them would invert every
        // Fresh flag silently.
        return CollectResult.Items(
            ItemTallies.BuildPossessions(manifest: manifest, live: live, cached: cached),
            sourceNotes);
    }

    // Walks one live container once, adding every occupied slot whose id is in the manifest to the
    // live tally with its quality routed to the right bucket.
    private static void TallyLiveContainer(
        InventoryManager* inventory,
        InventoryType type,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> live)
    {
        // `->` dereferences a pointer and reads a member — the pointer equivalent of `.`.
        var container = inventory->GetInventoryContainer(type);

        // A container the game has not allocated (null) or not yet populated (IsLoaded == false)
        // holds nothing we can trust; skip it rather than walk uninitialised memory.
        if (container is null || !container->IsLoaded)
            return;

        // GetSize() is the slot count for THIS container. Walking each slot once makes the whole live
        // scan cost O(total slots) — a few hundred across every bag and armoury chest — regardless of
        // how many ids the manifest asks about. The previous design called GetInventoryItemCount once
        // per manifest id, which re-walked the containers for every id: O(ids x containers). Walking
        // once and matching against a HashSet costs the same whether the manifest holds ten ids or a
        // thousand.
        var size = container->GetSize();
        for (var slotIndex = 0; slotIndex < size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);

            // A null slot within GetSize() should not happen, but game memory is read defensively: a
            // bad dereference here would take the whole process down uncatchably.
            if (slot is null)
                continue;

            // The raw base item id, read straight from the struct field. Deliberately NOT
            // GetItemId(), whose virtual form applies the high-quality offset to the id — the
            // manifest lists base ids, so the offset form would never match. Quality is read
            // separately below.
            var id = slot->ItemId;

            // An empty slot stores id 0 as padding, never a real item. Skip it before the manifest
            // test, since a malformed manifest could otherwise list 0 and match the padding.
            if (id == 0 || !manifestIds.Contains(id))
                continue;

            // Quantity is an int field; an occupied slot is always at least 1, but guard the cast so
            // a torn read can never wrap a negative into an enormous uint.
            var quantity = slot->Quantity;
            if (quantity <= 0)
                continue;

            // IsHighQuality()/IsCollectable() read the item's flags for us — no bit maths needed.
            AccumulateLive(live, id, (uint)quantity, slot->IsHighQuality(), slot->IsCollectable());
        }
    }

    // Resolves manifest ids the container walk could not see by consulting CurrencyManager, the
    // game's tracker for currencies that are not stored in an inventory container. Its documented
    // buckets include scrips, Bicolor Gemstones, Trophy Crystals, ventures, and Island Sanctuary
    // materials — but the split between the Currency container and these buckets is the game's
    // internal choice, not a rule to rely on (a hunt seal, for example, sits in a bucket). Whatever
    // the walk already tallied is intentionally skipped here; see the guard below.
    //
    // SAFETY — never query a count for an id the manager does not track. CurrencyManager keeps its
    // currencies in three buckets (ItemBucket, SpecialItemBucket, ContentItemBucket per the
    // FFXIVClientStructs docs); its GetItemCount takes an arbitrary item id and its behaviour for an id
    // that is in NO bucket is not documented, so we treat it as unsafe to call blind. HasItem is the
    // membership probe — the FFXIVClientStructs summary states it "Checks if the item is in any
    // bucket" — so every GetItemCount call is gated behind a HasItem check that returned true. We never
    // hand an untracked id to the count method on a "probably returns 0" assumption.
    //
    // DOUBLE-COUNT — the fallback only consults ids ABSENT from the live tally (`!live.ContainsKey`).
    // A currency that lives in the Currency container was already tallied by the walk and would be
    // double-counted if added again; skipping ids the walk found is how the two reads stay disjoint.
    // (Currencies never appear in the cache-backed sources, so there is no overlap with `cached`.)
    //
    // This ADDS counts the walk cannot see; it never invents one. An id that is neither in a container
    // nor tracked by the manager stays absent from every tally, and BuildPossessions still emits the
    // honest explicit zero for it from the manifest — absence here means "no live copy found", not a
    // guessed value.
    private static void TallyCurrencyFallback(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> live)
    {
        // Like the other game-memory singletons, a null instance means the subsystem is not available
        // this pass. Skip the fallback entirely and leave the container walk's results untouched — the
        // manager being briefly unreadable is not a reason to drop what we could already see.
        var currency = CurrencyManager.Instance();
        if (currency is null)
            return;

        // Iterate the deduped manifest id set, not every currency the game knows: the loop is bounded
        // by how many ids the manifest asks about (tiny), preserving the walk-once cost discipline. We
        // do not enumerate the manager's buckets.
        foreach (var id in manifestIds)
        {
            // id 0 is never a real item; an id the walk already found is left to the walk's tally so it
            // is not counted twice.
            if (id == 0 || live.ContainsKey(id))
                continue;

            // Membership probe FIRST — only ask for a count once the manager confirms it tracks the id.
            if (!currency->HasItem(id))
                continue;

            // Held amount for this currency. Currencies carry no quality, so the whole count is normal
            // quality (the Nq bucket). A zero here adds nothing the manifest's explicit zero does not
            // already report, so skip it rather than create an all-zero live entry.
            var count = currency->GetItemCount(id);
            if (count == 0)
                continue;

            AccumulateNq(live, id, count);
        }
    }

    // Fills the cached tally from every cache-backed source, and records how each was read into the
    // source notes. Each source is gated: it contributes only once the player has opened it, and the
    // note says whether it was cached/loaded (read) or unscanned (never opened).
    private void BuildCachedTallies(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached,
        Dictionary<string, ItemSourceStatus> notes)
    {
        // ARMOIRE — reached through UIState, independent of ItemFinderModule. The armoire is fetched
        // from the server the first time the player opens it each session; until then it is not
        // loaded and the game genuinely cannot answer.
        var uiState = UIState.Instance();
        var cabinetLoaded = uiState is not null && uiState->Cabinet.IsCabinetLoaded();
        notes[SourceKeys.Armoire] = new ItemSourceStatus
        {
            State = cabinetLoaded ? SourceStates.Loaded : SourceStates.Unscanned,
        };
        if (cabinetLoaded)
        {
            // Tally one per stored manifest item: the armoire holds a single copy of each item it can
            // store, so a match contributes a count of 1. Iterating the deduped id set (not the raw
            // manifest) keeps a duplicate manifest entry from counting the same armoire item twice.
            foreach (var id in manifestIds)
            {
                if (id != 0 && IsStoredInArmoire(id))
                    AccumulateNq(cached, id, 1);
            }
        }

        var finder = ItemFinderModule.Instance();
        if (finder is null)
        {
            // Nothing could be read from the cache-backed sources. Report them as unscanned — the
            // honest status for "not read", rather than implying an empty-but-current read.
            notes[SourceKeys.Saddlebag] = new ItemSourceStatus { State = SourceStates.Unscanned };
            notes[SourceKeys.Retainers] = new ItemSourceStatus { State = SourceStates.Unscanned };
            notes[SourceKeys.GlamourDresser] = new ItemSourceStatus { State = SourceStates.Unscanned };
            return;
        }

        // SADDLEBAG (including the premium half). Readable once the player has opened it; the game
        // then keeps a cache. Every count here goes into the Nq bucket — the cache exposes ids and
        // counts but no quality flags, so the plugin cannot tell HQ or collectable copies apart and
        // does not pretend to.
        if (finder->IsSaddleBagCached)
        {
            TallySlots(finder->SaddleBagItemIds, finder->SaddleBagItemCount, manifestIds, cached);
            TallySlots(finder->PremiumSaddleBagItemIds, finder->PremiumSaddleBagItemCount, manifestIds, cached);
            notes[SourceKeys.Saddlebag] = new ItemSourceStatus { State = SourceStates.Cached };
        }
        else
        {
            notes[SourceKeys.Saddlebag] = new ItemSourceStatus { State = SourceStates.Unscanned };
        }

        // GLAMOUR DRESSER. Readable once opened this session; the game caches the stored ids.
        if (finder->IsGlamourDresserCached)
        {
            // KNOWN GAP: this finds gear stored in the dresser INDIVIDUALLY. Gear stored "as an outfit"
            // occupies a dresser slot whose id is the outfit's set id (a MirageStoreSetItem row), not
            // the id of any piece inside it — so a piece stored that way does not match here, while the
            // same piece stored individually does.
            //
            // How wide the gap is depends on the game's curation: only gear with a MirageStoreSetItem
            // row can be stored as an outfit, and the game keeps ADDING sets over time — so no class of
            // gear can be assumed safe from hiding here, and the assumption must be re-checked whenever
            // the manifest grows. Weapons and tools are the one durable exception: outfits are armor
            // sets, so they can never be outfit-stored.
            //
            // The standing rule, then: before any GEAR ids join the manifest, intersect them with the
            // member item ids of every MirageStoreSetItem row. If the intersection is empty, nothing is
            // needed yet. If it is not, pair each dresser slot's id with its set-unlock bits
            // (ItemFinderModule keeps both, and both survive a logout) and map set id + slot to a piece
            // through that same sheet — otherwise a piece stored as an outfit reads as not owned.
            //
            // The dresser stores a single copy per slot, so each id match tallies 1 (again Nq only —
            // no quality flags are exposed for the cached ids).
            foreach (var storedId in finder->GlamourDresserItemIds)
            {
                if (storedId != 0 && manifestIds.Contains(storedId))
                    AccumulateNq(cached, storedId, 1);
            }

            notes[SourceKeys.GlamourDresser] = new ItemSourceStatus { State = SourceStates.Cached };
        }
        else
        {
            notes[SourceKeys.GlamourDresser] = new ItemSourceStatus { State = SourceStates.Unscanned };
        }

        // RETAINERS. `.Count` is the number of retainers whose contents the cache remembers; reading
        // it does not touch the map's keys (see the privacy note on TallyRetainers). A count of zero
        // means the player has never had a retainer's contents cached — nothing to read, so unscanned.
        //
        // The TOTAL retainer count comes from RetainerManager, so the note can say "3 of 5 scanned"
        // rather than a bare "3" that hides never-summoned retainers. GetRetainerCount is a count
        // only — the manager's per-retainer entries (ids, names) are never read, the same privacy
        // rule TallyRetainers documents. The manager is populated by the game during the session;
        // until then it reports 0, which is indistinguishable from "has no retainers", so zero is
        // treated as unknown and the total is simply omitted.
        var retainerManager = RetainerManager.Instance();
        var knownTotal = retainerManager is not null ? (int)retainerManager->GetRetainerCount() : 0;
        int? retainerTotal = knownTotal > 0 ? knownTotal : null;

        var retainerCount = finder->RetainerInventories.Count;
        if (retainerCount > 0)
        {
            TallyRetainers(finder, manifestIds, cached);
            notes[SourceKeys.Retainers] = new ItemSourceStatus
            {
                State = SourceStates.Cached,
                Count = retainerCount,
                Total = retainerTotal,
            };
        }
        else
        {
            notes[SourceKeys.Retainers] = new ItemSourceStatus
            {
                State = SourceStates.Unscanned,
                Total = retainerTotal,
            };
        }
    }

    /// <summary>
    /// Adds every occupied slot of the local player's retainers to the cached tally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retainers matter: relic weapons and tools are very often parked on one, and skipping them
    /// would report zero for an item the player demonstrably owns, leaving the relic step unproven.
    /// </para>
    /// <para>
    /// These are the local player's own retainers — their own possessions, not another player's
    /// data. Only the inventory <i>values</i> of the map are read; the keys, which are retainer
    /// IDs, are never touched and never leave the process. Nothing but item counts is uploaded.
    /// </para>
    /// <para>
    /// <c>ItemFinderModule</c> persists this cache to a local user file, so retainer contents can
    /// be present after a fresh login <b>without the player summoning the retainer</b> — the data
    /// came off disk, not from the server. It is therefore a genuine cache and may be stale, which
    /// is exactly why the retainer source is reported as cached, not live. A stale positive still
    /// proves the item was once held, which is all the server needs; possession is volatile, and
    /// relic proofs are sticky. A retainer that has never been visited contributes nothing, because
    /// its entry simply is not in the map.
    /// </para>
    /// <para>
    /// <c>IsRetainerCurrent</c> could tell a this-session read apart from the disk cache, but using
    /// it would mean reading the map's retainer-ID keys, which this plugin deliberately never
    /// touches. The distinction would change nothing: both are reported as not fresh.
    /// </para>
    /// </remarks>
    private static void TallyRetainers(
        ItemFinderModule* finder,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        // `.Values` walks the map's inventories without ever reading its keys.
        foreach (var inventoryPointer in finder->RetainerInventories.Values)
        {
            var retainer = inventoryPointer.Value;
            if (retainer is null)
                continue;

            // Stored in the retainer's bags, with per-slot counts.
            TallySlots(retainer->ItemIds, retainer->ItemCount, manifestIds, cached);

            // Equipped on the retainer: one each, and there are no counts to pair with.
            foreach (var equippedId in retainer->EquippedItemIds)
            {
                if (equippedId != 0 && manifestIds.Contains(equippedId))
                    AccumulateNq(cached, equippedId, 1);
            }
        }
    }

    /// <summary>
    /// Adds every manifest-matching slot of a container's parallel (item id, count) arrays to the
    /// cached tally. Used for the saddlebags and for each retainer's bags, which share that layout.
    /// </summary>
    // A Span<T> is a window onto memory that already exists — no copy is made. Iterating one is the
    // safe way to walk a fixed-size array living inside the game's own structs. The loop bound is the
    // shorter of the two spans so a mismatched pair can never read out of bounds. Cached counts carry
    // no quality flags, so everything lands in the Nq bucket.
    private static void TallySlots(
        Span<uint> itemIds,
        Span<ushort> counts,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        for (var slot = 0; slot < itemIds.Length && slot < counts.Length; slot++)
        {
            var id = itemIds[slot];
            if (id == 0 || !manifestIds.Contains(id))
                continue;

            AccumulateNq(cached, id, counts[slot]);
        }
    }

    // Adds a live-container quantity to an id's tally, routing it into exactly one quality bucket.
    private static void AccumulateLive(
        Dictionary<uint, ItemTally> tallies,
        uint id,
        uint quantity,
        bool isHighQuality,
        bool isCollectable)
    {
        // Collectable is checked first because a collectable item is its own quality, distinct from
        // ordinary high quality; everything else is normal quality. The three buckets are never
        // summed together — the server applies different rules per quality — so they travel apart.
        ItemTally delta;
        if (isCollectable)
            delta = new ItemTally(Nq: 0, Hq: 0, Collectable: quantity);
        else if (isHighQuality)
            delta = new ItemTally(Nq: 0, Hq: quantity, Collectable: 0);
        else
            delta = new ItemTally(Nq: quantity, Hq: 0, Collectable: 0);

        AddTally(tallies, id, delta);
    }

    // Adds a cached quantity to an id's tally in the Nq bucket. Cached sources expose no quality
    // flags, so their counts are always normal quality (see the per-source comments).
    private static void AccumulateNq(Dictionary<uint, ItemTally> tallies, uint id, uint quantity) =>
        AddTally(tallies, id, new ItemTally(Nq: quantity, Hq: 0, Collectable: 0));

    // Folds a delta into any running total already recorded for this id — the same item can occupy
    // several slots and several containers. TryGetValue leaves `existing` as the default all-zero
    // tally when the id is absent, which ItemTally.Add treats as the identity.
    private static void AddTally(Dictionary<uint, ItemTally> tallies, uint id, ItemTally delta)
    {
        tallies.TryGetValue(id, out var existing);
        tallies[id] = existing.Add(delta);
    }

    private bool IsStoredInArmoire(uint itemId)
    {
        var uiState = UIState.Instance();
        if (uiState is null)
            return false;

        // The armoire is fetched from the server only when the player opens it. Until then the game
        // genuinely does not know its contents, and asking would return a confident "no".
        if (!uiState->Cabinet.IsCabinetLoaded())
            return false;

        // Built once and reused; the sheet cannot change while the game runs. Deliberately NOT
        // `armoireIndex ??= Build()`: if the sheet were momentarily unreadable, that would cache an
        // empty index forever and silently disable armoire detection for the rest of the session.
        // A failed build leaves the field null so the next call retries.
        if (armoireIndex is null)
        {
            var built = TryBuildArmoireIndex();
            if (built is null)
                return false;

            armoireIndex = built;
        }

        // The game answers by armoire row, not by item, hence the lookup.
        return armoireIndex.TryGetValue(itemId, out var cabinetId)
               && uiState->Cabinet.IsItemInCabinet(cabinetId);
    }

    // Returns null when the sheet could not be read, so the caller knows not to cache the result.
    private IReadOnlyDictionary<uint, uint>? TryBuildArmoireIndex()
    {
        var sheet = dataManager.GetExcelSheet<CabinetSheet>();
        if (sheet is null)
            return null;

        var rows = new List<(uint CabinetId, uint ItemId)>();
        foreach (var row in sheet)
            rows.Add((row.RowId, row.Item.RowId));

        return ArmoireIndex.Build(rows);
    }
}
