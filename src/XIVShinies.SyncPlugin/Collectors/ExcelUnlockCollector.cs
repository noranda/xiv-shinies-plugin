using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the IDs of every row in a game data sheet that the local player has unlocked or
/// completed. Covers quests, mounts, minions, achievements — and any future sheet-backed
/// collection, since <c>IUnlockState</c> exposes the same shape for all of them.
/// </summary>
/// <typeparam name="TRow">
/// The Excel sheet row type, for example <c>Quest</c> or <c>Mount</c>. The constraint
/// <c>struct, IExcelRow&lt;TRow&gt;</c> is what Lumina requires of a sheet row.
/// </typeparam>
/// <remarks>
/// <para>
/// Reads game data, so <see cref="Collect"/> must run on the framework thread. It cannot be
/// unit-tested (constructing it loads <c>Dalamud.dll</c>); it is verified by in-game QA. The
/// decisions worth testing — gating, skip handling, payload assembly — live in the Dalamud-free
/// <see cref="CollectorRunner"/> instead.
/// </para>
/// <para>
/// Reads only the <b>local</b> player's unlock state. It never touches the object table or any
/// other character, which is a hard Dalamud rule.
/// </para>
/// </remarks>
// Generics let one class serve every sheet: `ExcelUnlockCollector<Quest>` and
// `ExcelUnlockCollector<Mount>` are distinct types the compiler generates from this one
// definition, exactly like a generic in TypeScript.
public sealed class ExcelUnlockCollector<TRow> : ICollector, IUnlockAware
    where TRow : struct, IExcelRow<TRow>
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    // Lumina's row interface does not expose RowId, so the caller hands us a way to read it.
    private readonly Func<TRow, uint> rowId;

    private readonly Func<TRow, bool> isUnlocked;

    // Returns a skip reason when the source cannot be read yet, or null when it can.
    private readonly Func<string?>? precondition;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    /// <summary>Creates a collector for one sheet.</summary>
    /// <param name="info">The category's wire key and its user-facing copy.</param>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="rowId">Reads a row's ID (usually <c>row =&gt; row.RowId</c>).</param>
    /// <param name="isUnlocked">True when the local player has unlocked/completed this row.</param>
    /// <param name="precondition">
    /// Optional. Returns a skip reason when the source is not readable yet — for example, the
    /// achievements list has never been requested from the server. Returning a reason omits the
    /// category from the upload, which is safe: the server treats absence as "not read this time".
    /// </param>
    public ExcelUnlockCollector(
        CategoryInfo info,
        IDataManager dataManager,
        IFramework framework,
        Func<TRow, uint> rowId,
        Func<TRow, bool> isUnlocked,
        Func<string?>? precondition = null)
    {
        this.info = info;
        this.dataManager = dataManager;
        this.framework = framework;
        this.rowId = rowId;
        this.isUnlocked = isUnlocked;
        this.precondition = precondition;
    }

    /// <inheritdoc/>
    public string CategoryKey => info.Key;

    /// <inheritdoc/>
    public string DisplayName => info.DisplayName;

    /// <inheritdoc/>
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    // Each collector recognises only its own sheet, so routing an unlock needs no lookup table and
    // no branch on category names. `Is<TRow>()` compares the row type the game reported against the
    // one this collector was built for.
    public bool Handles(RowRef rowRef) => rowRef.Is<TRow>();

    /// <inheritdoc/>
    // This collector needs nothing from the context; sheet-backed unlocks are self-contained.
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(ExcelUnlockCollector<TRow>));

        // `?.Invoke()` calls the delegate only when it is not null.
        var skipReason = precondition?.Invoke();
        if (skipReason is not null)
            return CollectResult.Skipped(skipReason);

        var sheet = dataManager.GetExcelSheet<TRow>();
        if (sheet is null)
            return CollectResult.Skipped(CollectSkipReasons.SheetUnavailable);

        var ids = new List<uint>();
        foreach (var row in sheet)
        {
            if (isUnlocked(row))
                ids.Add(rowId(row));
        }

        // An empty list is a legitimate result ("we read the sheet; nothing was unlocked"), and is
        // deliberately different from a skip.
        return CollectResult.Ids(ids);
    }
}
