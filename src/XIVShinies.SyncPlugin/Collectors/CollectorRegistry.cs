using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Builds the list of collectors the plugin runs.
/// </summary>
/// <remarks>
/// <b>This is the only place a collection is registered.</b> Adding one means adding a single
/// entry here — no change to the runner, the payload, the settings UI, or the API client. The
/// collectors are handed to <see cref="CollectorRunner"/> rather than constructed inside it,
/// because they hold Dalamud services and would otherwise make the runner impossible to test.
/// </remarks>
public static class CollectorRegistry
{
    // The user-facing copy for each collection, alongside its wire key. `WhatGetsSent` is shown next
    // to the opt-in toggle before the user consents, so it is a compliance surface: it must stay a
    // true description of what the matching collector actually uploads.

    private static readonly CategoryInfo Quests = new()
    {
        Key = CategoryKeys.Quests,
        DisplayName = "Quests",
        WhatGetsSent = "The ID numbers of quests you have completed.",
    };

    private static readonly CategoryInfo Mounts = new()
    {
        Key = CategoryKeys.Mounts,
        DisplayName = "Mounts",
        WhatGetsSent = "The ID numbers of mounts you have unlocked.",
    };

    private static readonly CategoryInfo Minions = new()
    {
        Key = CategoryKeys.Minions,
        DisplayName = "Minions",
        WhatGetsSent = "The ID numbers of minions you have unlocked.",
    };

    private static readonly CategoryInfo Achievements = new()
    {
        Key = CategoryKeys.Achievements,
        DisplayName = "Achievements",
        WhatGetsSent = "The ID numbers of achievements you have earned.",
    };

    private static readonly CategoryInfo Items = new()
    {
        Key = CategoryKeys.Items,
        DisplayName = "Relic items",

        // Says plainly that the search covers the character's own storage, retainers included, and
        // that a zero count is still a reported fact, not silence. Only the counts of the items XIV
        // Shinies named ever leave the machine, but a disclosure that omitted where the plugin looks
        // — or that "none of this item" is itself sent — would be technically true and practically
        // misleading. Also names the per-group choice now that item consent is granted per manifest
        // group rather than as one all-or-nothing switch.
        //
        // Currency balances get their own sentence, with gil named outright: a balance is wealth
        // data, and "items" alone would not tell a reader that consenting to a currency group sends
        // how much gil they hold. Phrased conditionally ("when XIV Shinies asks") because whether
        // any currency is asked about is the server's manifest choice, group-gated like everything
        // else — the sentence is true both before and after such a group exists.
        WhatGetsSent =
            "Counts of the specific items XIV Shinies asks about — including that you have none of " +
            "an item — checked across your inventory, Armoire, Glamour Dresser, Saddlebag, and " +
            "retainers. When XIV Shinies asks about currencies, your balances (gil included) are " +
            "sent the same way. You choose which groups to share when XIV Shinies offers them. " +
            "Nothing else is sent.",

        // The only collection whose scope comes from the server's item manifest rather than being
        // fixed at compile time, so it is the one that gets per-group consent rows in settings.
        UsesItemManifest = true,
    };

    /// <summary>Creates every collector, in the order they will be run.</summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="unlockState">Dalamud's local-player unlock state.</param>
    /// <param name="framework">Used by each collector to verify it is on the framework thread.</param>
    public static IReadOnlyList<ICollector> Create(
        IDataManager dataManager, IUnlockState unlockState, IFramework framework) =>
        new ICollector[]
        {
            // `unlockState.IsQuestCompleted` is a "method group": the method is passed as a value
            // where a `Func<Quest, bool>` is expected, and C# binds the receiver (`unlockState`)
            // along with it. This is unlike JS, where passing `obj.method` bare loses `this`.
            // Nothing is invoked here — the delegate is called later, during collection.

            // Quest Excel row IDs are what the server stores, so no mapping is needed.
            new ExcelUnlockCollector<Quest>(
                Quests, dataManager, framework, row => row.RowId, unlockState.IsQuestCompleted),

            new ExcelUnlockCollector<Mount>(
                Mounts, dataManager, framework, row => row.RowId, unlockState.IsMountUnlocked),

            // The game calls minions "Companions".
            new ExcelUnlockCollector<Companion>(
                Minions, dataManager, framework, row => row.RowId, unlockState.IsCompanionUnlocked),

            // Achievements are the one sheet the game cannot answer for until the player has opened
            // their Achievements window at least once this session. Until then we skip the category
            // rather than report an empty list, which would be a lie the server must not act on.
            new ExcelUnlockCollector<Achievement>(
                Achievements,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsAchievementComplete,
                precondition: () => unlockState.IsAchievementListLoaded
                    ? null
                    : CollectSkipReasons.AchievementListNotLoaded),

            // The odd one out: it reports possession counts rather than IDs, and it only looks at
            // the items the server named in its manifest. The runner treats it like any other.
            new ItemCollector(Items, dataManager, framework),
        };
}
