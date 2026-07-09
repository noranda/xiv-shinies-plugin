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
/// Only the items in the server's manifest are ever looked at. Possession of one proves a relic
/// step server-side, so a stale positive is still useful — an item that <i>was</i> there proves the
/// step happened, even if it has since been consumed. That is why cached sources are consulted at
/// all, and why they are reported honestly with <c>fresh: false</c>.
/// </para>
/// <para>
/// Sources searched, in order: the live containers (bags, equipped, armoury chest), then the
/// armoire, the glamour dresser, the saddlebags, and finally the player's own <b>retainers</b> —
/// relic weapons and tools are commonly parked on a retainer, and omitting them would report zero
/// for an item the player owns. Only the local player's own storage is read; no other character's
/// data is touched, and no retainer ID ever leaves the process.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and cannot
/// be unit-tested. Only <see cref="ArmoireIndex"/>, the pure lookup it depends on, is covered by
/// tests; the container reads are verified by in-game QA.
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

    /// <summary>Creates the collector.</summary>
    /// <param name="categoryKey">
    /// The key this collector files its facts under. Passed in from the registry rather than
    /// hardcoded here, so that every category key in the plugin is chosen in exactly one file.
    /// </param>
    /// <param name="dataManager">Dalamud's game data accessor, used to read the armoire sheet.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public ItemCollector(string categoryKey, IDataManager dataManager, IFramework framework)
    {
        CategoryKey = categoryKey;
        this.dataManager = dataManager;
        this.framework = framework;
    }

    /// <inheritdoc/>
    public string CategoryKey { get; }

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

        var manifest = context.ItemManifest;

        // An empty manifest is a real answer: the server asked about nothing, so we found nothing.
        if (manifest.Count == 0)
            return CollectResult.Items(Array.Empty<ItemPossession>());

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return CollectResult.Skipped(CollectSkipReasons.InventoryUnavailable);

        var possessed = new List<ItemPossession>();

        foreach (var itemId in manifest)
        {
            // The server should never ask about item 0, but a zero would match the padding in the
            // game's cached arrays and put an invalid id on the wire, which fails validation and
            // rejects the whole upload. Refuse to look for it.
            if (itemId == 0)
                continue;

            // `->` dereferences a pointer and reads a member — the pointer equivalent of `.`.
            // The live read covers bags, equipped gear, and the armoury chest in one call.
            // `isHq: false` counts normal-quality only; relic-stage items are untradeable NQ, so
            // this is correct for the manifest, and in-game QA should confirm it for any item that
            // can exist in HQ form.
            var liveCount = inventory->GetInventoryItemCount(
                itemId, isHq: false, checkEquipped: true, checkArmory: true);

            if (liveCount > 0)
            {
                // The cast is safe only because of the `> 0` test above: a negative int would wrap
                // to an enormous uint and be reported as possession.
                possessed.Add(new ItemPossession { Id = itemId, Count = (uint)liveCount, Fresh = true });
                continue;
            }

            // Nothing live — fall back to the caches the game keeps of places we cannot read
            // directly. These are only populated once the player has visited the relevant UI.
            var cachedCount = CountInCachedSources(itemId);
            if (cachedCount > 0)
                possessed.Add(new ItemPossession { Id = itemId, Count = cachedCount, Fresh = false });
        }

        // Items the character does not have are simply left out. Absence proves nothing either way,
        // and the server only acts on a positive count.
        return CollectResult.Items(possessed);
    }

    // Sources the game caches rather than exposes live: the armoire, the glamour dresser, and the
    // saddlebags. Each is only readable once its cache has been populated, so each is gated.
    private uint CountInCachedSources(uint itemId)
    {
        if (IsStoredInArmoire(itemId))
            return 1;

        var finder = ItemFinderModule.Instance();
        if (finder is null)
            return 0;

        // KNOWN GAP: this finds gear stored in the dresser INDIVIDUALLY. Gear stored "as an outfit"
        // occupies a dresser slot whose id is the outfit's set id (a MirageStoreSetItem row), not
        // the id of any piece inside it, so a piece stored that way does not match here. Confirmed
        // in game: the same item is found stored individually and missed stored inside an outfit.
        //
        // The gap is narrow. Storing as an outfit is only offered for gear that has a curated
        // MirageStoreSetItem row (largely promotional sets), so ordinary gear — including relic
        // armour, checked in game — has no outfit option and cannot hide here. Weapons and tools,
        // which is all the manifest asks about today, never can.
        //
        // Before the server adds relic gear to the manifest, verify the assumption rather than
        // trusting it: intersect the member item ids of every MirageStoreSetItem row with the gear
        // ids being added. If the intersection is empty, nothing is needed. If it is not, pair each
        // dresser slot's id with its set-unlock bits (ItemFinderModule keeps both, and both survive
        // a logout) and map set id + slot to a piece through that same sheet.
        if (finder->IsGlamourDresserCached)
        {
            foreach (var storedId in finder->GlamourDresserItemIds)
            {
                if (storedId == itemId)
                    return 1;
            }
        }

        if (finder->IsSaddleBagCached)
        {
            var count = CountInSlots(finder->SaddleBagItemIds, finder->SaddleBagItemCount, itemId)
                        + CountInSlots(finder->PremiumSaddleBagItemIds, finder->PremiumSaddleBagItemCount, itemId);

            if (count > 0)
                return count;
        }

        return CountInRetainers(finder, itemId);
    }

    /// <summary>
    /// Counts the item across the local player's own retainers.
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
    /// is exactly why the call site reports <c>fresh: false</c>. A stale positive still proves the
    /// item was once held, which is all the server needs; possession is volatile, and relic proofs
    /// are sticky. A retainer that has never been visited contributes nothing, because its entry
    /// simply is not in the map.
    /// </para>
    /// <para>
    /// <c>IsRetainerCurrent</c> could tell a this-session read apart from the disk cache, but using
    /// it would mean reading the map's retainer-ID keys, which this plugin deliberately never
    /// touches. The distinction would change nothing: both are reported as not fresh.
    /// </para>
    /// </remarks>
    private static uint CountInRetainers(ItemFinderModule* finder, uint itemId)
    {
        uint total = 0;

        // `.Values` walks the map's inventories without ever reading its keys.
        foreach (var inventoryPointer in finder->RetainerInventories.Values)
        {
            var retainer = inventoryPointer.Value;
            if (retainer is null)
                continue;

            // Stored in the retainer's bags, with per-slot counts.
            total += CountInSlots(retainer->ItemIds, retainer->ItemCount, itemId);

            // Equipped on the retainer: one each, and there are no counts to pair with.
            foreach (var equippedId in retainer->EquippedItemIds)
            {
                if (equippedId == itemId)
                    total++;
            }
        }

        return total;
    }

    /// <summary>
    /// Sums the counts of one item across a container's parallel (item id, count) slot arrays.
    /// Used for the saddlebags and for each retainer's bags, which share that layout.
    /// </summary>
    // A Span<T> is a window onto memory that already exists — no copy is made. Iterating one is the
    // safe way to walk a fixed-size array living inside the game's own structs. The loop bound is
    // the shorter of the two spans so a mismatched pair can never read out of bounds.
    private static uint CountInSlots(Span<uint> itemIds, Span<ushort> counts, uint itemId)
    {
        uint total = 0;

        for (var slot = 0; slot < itemIds.Length && slot < counts.Length; slot++)
        {
            if (itemIds[slot] == itemId)
                total += counts[slot];
        }

        return total;
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
