using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One row of the settings window's category list, as the window should draw it.
/// </summary>
/// <remarks>
/// The window renders these without knowing which collection each one is. That is the whole point:
/// a new collection appears in the settings UI by <i>existing</i>, not by being added to a list.
/// </remarks>
public sealed record CategorySettingsRow
{
    /// <summary>The category this row is about. The window uses it only to write the toggle back.</summary>
    public required string Key { get; init; }

    /// <summary>The label to draw beside the checkbox.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The plain-language description of what uploading this category sends.</summary>
    public required string WhatGetsSent { get; init; }

    /// <summary>Whether the user has opted this category in.</summary>
    public required bool UserEnabled { get; init; }

    /// <summary>
    /// Whether this row's collector announced itself as manifest-driven (see
    /// <see cref="ICollector.UsesItemManifest"/>).
    /// </summary>
    /// <remarks>
    /// Carried on the row so a pure view can act on it without holding a collector, and it is what keeps
    /// <see cref="ReadStatusView"/> free of a category-name branch: the container lines belong to a
    /// manifest-driven collection, so the panel needs to know which row that is — to stand its container
    /// lines in for the collection's own line, and to drop them when no such collection is switched on.
    /// The per-group checkboxes are governed by the same flag one step earlier: <see cref="Groups"/> is
    /// only ever populated for a collector that announced it.
    /// </remarks>
    public required bool UsesItemManifest { get; init; }

    /// <summary>
    /// False when the server has switched this category off for everyone. The checkbox stays
    /// visible but disabled: the user's own preference is remembered and reapplied if the server
    /// turns it back on.
    /// </summary>
    public required bool ServerEnabled { get; init; }

    /// <summary>
    /// Why the last collection pass skipped this category, or null if it did not.
    /// </summary>
    /// <remarks>
    /// This is how the "open your Achievements window once" hint reaches the UI without anyone
    /// writing <c>if (key == "achievements")</c>. The collector reports a reason; the window shows
    /// whatever reason it is given.
    /// </remarks>
    public string? SkipReason { get; init; }

    /// <summary>True when this category will actually be uploaded as things stand.</summary>
    public bool IsEffectivelyOn => UserEnabled && ServerEnabled;

    /// <summary>
    /// One row per item-manifest consent group, for a manifest-driven collector — or null when this
    /// row has no groups to draw.
    /// </summary>
    /// <remarks>
    /// Null covers two different situations on purpose: the collector never announced itself as
    /// manifest-driven (see <see cref="ICollector.UsesItemManifest"/>), or the server has not sent any
    /// groups yet. Either way there is nothing to draw beneath this row, so the window does not need
    /// to tell them apart — it just checks for null.
    /// </remarks>
    public IReadOnlyList<ItemGroupRow>? Groups { get; init; }
}

/// <summary>
/// One row of a manifest-driven category's group list — one checkbox per named slice of the item
/// manifest, drawn beneath that category's row in the settings window.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Api.ItemManifestGroup"/> but adds the two things the server response does not
/// carry: whether <i>this</i> user has opted the group in, and whether the settings window has shown
/// it to them before. Keeping those two computed fields off the wire type is why this record exists
/// separately rather than reusing <see cref="Api.ItemManifestGroup"/> directly.
/// </remarks>
public sealed record ItemGroupRow
{
    /// <summary>The server's stable group key, used to read and write this group's consent.</summary>
    public required string Key { get; init; }

    /// <summary>The label to draw beside the group's checkbox.</summary>
    public required string Label { get; init; }

    /// <summary>Whether the user has opted this group in.</summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// True when the settings window has never shown this group before, so it should carry a "New"
    /// badge. A group the server just added is new for everyone until each user's settings window has
    /// rendered it once.
    /// </summary>
    public required bool IsNew { get; init; }
}

/// <summary>
/// Assembles the settings window's category list from the registered collectors.
/// </summary>
/// <remarks>
/// <para>
/// Pure and Dalamud-free, so the <b>extensibility contract is testable</b>: a fake collector for a
/// category this plugin has never heard of must flow through here and appear in the list, proving the
/// settings surface contains no category-name branch.
/// </para>
/// <para>
/// Note what is absent — there is no table of names, no ordering by category, and no special case for
/// any collection. Every row is built from what the collector says about itself.
/// </para>
/// </remarks>
public static class CategorySettingsView
{
    /// <summary>Builds one row per registered collector, in registration order.</summary>
    /// <param name="collectors">Every registered collector.</param>
    /// <param name="settings">The user's persisted opt-ins.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if it has not been fetched.</param>
    /// <param name="lastSkipped">
    /// Skip reasons from the most recent collection pass, keyed by category. Empty before the first
    /// pass, which simply means no row shows a hint yet.
    /// </param>
    public static IReadOnlyList<CategorySettingsRow> Build(
        IEnumerable<ICollector> collectors,
        PluginSettings settings,
        ConfigResponse? remoteConfig,
        IReadOnlyDictionary<string, string>? lastSkipped = null)
    {
        var rows = new List<CategorySettingsRow>();

        foreach (var collector in collectors)
        {
            var key = collector.CategoryKey;

            rows.Add(new CategorySettingsRow
            {
                Key = key,
                DisplayName = collector.DisplayName,
                WhatGetsSent = collector.WhatGetsSent,
                UserEnabled = settings.IsCategoryEnabled(key),

                // Carried through verbatim from the collector's own self-description. Nothing here
                // decides which collections are manifest-driven; the collector says so itself.
                UsesItemManifest = collector.UsesItemManifest,

                // A config we have not fetched forbids nothing, matching how the collectors and the
                // upload gate treat it. Otherwise a plugin that cannot reach /config would show every
                // category as disabled by the server, which would be a lie.
                ServerEnabled = remoteConfig?.IsCategoryEnabled(key) ?? true,

                // `TryGetValue` fills the out parameter and returns whether the key existed. The
                // discard-style pattern below just means "null when it was not there".
                SkipReason = lastSkipped is not null && lastSkipped.TryGetValue(key, out var reason)
                    ? reason
                    : null,

                Groups = BuildGroupRows(collector, settings, remoteConfig),
            });
        }

        return rows;
    }

    // Group rows attach only when BOTH sides agree there is something to draw: the collector has
    // announced itself as manifest-driven (self-description, not a check on its category key), and
    // the server actually sent a group list this pass. The null return is deliberate — an absent
    // group list and an empty one both look empty to a caller checking `.Count`, but only null
    // tells the window there is nothing here at all, so absence is never papered over with
    // `Array.Empty`.
    private static IReadOnlyList<ItemGroupRow>? BuildGroupRows(
        ICollector collector, PluginSettings settings, ConfigResponse? remoteConfig)
    {
        if (!collector.UsesItemManifest || remoteConfig?.ItemManifestGroups is not { } manifestGroups)
            return null;

        var groupRows = new List<ItemGroupRow>();
        foreach (var group in manifestGroups)
        {
            // A blank key is server data gone wrong, and it can never behave: consent reads treat it
            // as off, seen-marking skips it (so it would wear a "New" badge forever and re-trigger a
            // config save every frame), and the consent write would throw. Dropping it here, at the
            // pure boundary, keeps every one of those paths safe — and testable.
            if (string.IsNullOrEmpty(group.Key))
                continue;

            groupRows.Add(new ItemGroupRow
            {
                Key = group.Key,
                Label = group.Label,
                Enabled = settings.IsItemGroupEnabled(group.Key),
                IsNew = !settings.IsItemGroupSeen(group.Key),
            });
        }

        return groupRows;
    }
}
