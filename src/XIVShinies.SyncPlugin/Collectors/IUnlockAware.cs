using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Implemented by collectors that can recognise the game's real-time <c>Unlock</c> event as their
/// own, so an unlock can upload just the one affected category.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>separate</b> from <see cref="ICollector"/> rather than a member of it.
/// <see cref="RowRef"/> comes from Lumina, and putting it on <see cref="ICollector"/> would drag
/// <c>Lumina.dll</c> into anything that merely implements the interface — including the fake
/// collector the unit tests use, which would then fail to load outside the game. Keeping it optional
/// lets the collector runner and its tests stay free of game assemblies.
/// </para>
/// <para>
/// Why route the event at all, rather than re-uploading everything on any unlock: an
/// <c>unlock</c>-triggered upload stamps the upload moment as the acquisition date for every
/// category it carries, and no later plugin upload revises an existing date. A blanket sweep would
/// therefore write permanently wrong dates onto rows that happened to be uploaded for the first time
/// alongside a genuine unlock. Quests are the sharpest case, because a snapshot upload
/// (<c>login</c>/<c>interval</c>/<c>manual</c>) deliberately leaves their date null — so a swept
/// quest that an unlock upload happened to create would be dated the moment an unrelated mount was
/// earned. Wrong dates cannot be repaired; a missed unlock merely waits for the next full sweep.
/// </para>
/// </remarks>
// `internal` = visible within this assembly only. Nothing outside the plugin needs to know.
internal interface IUnlockAware
{
    /// <summary>True when the unlocked row belongs to this collector's category.</summary>
    /// <remarks>
    /// An untyped <see cref="RowRef"/> (one the game gave no row type for) belongs to nobody and is
    /// claimed by no collector, which means that unlock is simply not uploaded early.
    /// </remarks>
    bool Handles(RowRef rowRef);
}
