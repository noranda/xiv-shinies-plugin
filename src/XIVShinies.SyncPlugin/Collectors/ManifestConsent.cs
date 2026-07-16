using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Answers questions about item-manifest consent without anyone having to name a category.
/// </summary>
/// <remarks>
/// A separate class because the code that needs these answers lives outside the collectors — the sync
/// orchestrator, for one — and the extensibility contract forbids it from naming a category to get them.
/// Pure and Dalamud-free, so the gate test can run a collector this plugin has never heard of through it.
/// </remarks>
public static class ManifestConsent
{
    /// <summary>
    /// True when the user has opted into a collection that is driven by the item manifest — the consent
    /// the one-time group migration carries over onto the server's <c>legacy</c> groups.
    /// </summary>
    /// <remarks>
    /// Asked of the collectors rather than named. The migration needs the consent of whichever category
    /// owns the manifest groups, and the collectors already say which one that is
    /// (<see cref="ICollector.UsesItemManifest"/>), so a second manifest-driven collection is migrated
    /// for free instead of silently never being migrated at all.
    /// </remarks>
    /// <param name="collectors">Every registered collector.</param>
    /// <param name="settings">The consent to read.</param>
    public static bool AnyManifestCategoryEnabled(
        IEnumerable<ICollector> collectors, PluginSettings settings)
    {
        foreach (var collector in collectors)
        {
            if (collector.UsesItemManifest && settings.IsCategoryEnabled(collector.CategoryKey))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Writes one category's consent, and every manifest group nested under it, together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A category and its groups cannot send anything without each other: a group's ids are only ever
    /// scanned as part of its category's pass, and a category driven by the manifest sends only what its
    /// enabled groups cover. A ticked category with every group off would therefore upload nothing while
    /// presenting itself as on — so ticking a category ticks its groups, and unticking it unticks them.
    /// Narrowing the selection group by group afterwards is exactly what those checkboxes are for.
    /// </para>
    /// <para>
    /// Only the groups ON THE ROW are touched — that is, the groups the user is looking at. A group the
    /// server adds later is not in this list and is not written here, so it arrives switched off and
    /// waits for a tick of its own.
    /// </para>
    /// </remarks>
    /// <param name="row">The category row whose consent is being written.</param>
    /// <param name="enabled">The value to write to the category and to each of its groups.</param>
    /// <param name="settings">The consent to write.</param>
    public static void SetRowConsent(CategorySettingsRow row, bool enabled, PluginSettings settings)
    {
        settings.SetCategoryEnabled(row.Key, enabled);

        if (row.Groups is not { Count: > 0 } groups)
            return;

        foreach (var group in groups)
            settings.SetItemGroupEnabled(group.Key, enabled);
    }

    /// <summary>
    /// Writes one group's consent, keeping its category in agreement with it.
    /// </summary>
    /// <remarks>
    /// The other half of the rule <see cref="SetRowConsent"/> writes from above. Switching a group ON
    /// switches its category on, because a group is only ever scanned as part of that category's pass.
    /// Switching the LAST enabled group OFF switches the category off, because with no group left to
    /// scan it would collect nothing while still presenting itself as switched on — and that is the one
    /// state a consent surface must never be able to reach.
    /// </remarks>
    /// <param name="row">The category row the group belongs to.</param>
    /// <param name="groupKey">The group the user just clicked.</param>
    /// <param name="enabled">What they set it to.</param>
    /// <param name="settings">The consent to write.</param>
    public static void SetGroupConsent(
        CategorySettingsRow row, string groupKey, bool enabled, PluginSettings settings)
    {
        settings.SetItemGroupEnabled(groupKey, enabled);

        if (enabled)
        {
            settings.SetCategoryEnabled(row.Key, true);
            return;
        }

        if (row.Groups is { Count: > 0 } groups && !AnyGroupEnabled(groups, settings))
            settings.SetCategoryEnabled(row.Key, false);
    }

    /// <summary>
    /// Whether every consent control on screen is switched on — the state of the "all collections" box.
    /// </summary>
    /// <remarks>
    /// Two questions, not one, because "every row it met was on" is only an answer if it met any. A box
    /// seeded true and then shown nothing — no collectors at all, or every category switched off by the
    /// server — would render ticked while nothing whatsoever is on, which is the one reading a consent
    /// control must never give. Rows the server has switched off are skipped: they are not the user's to
    /// answer for, and their own controls are drawn greyed out.
    /// </remarks>
    /// <param name="rows">The category rows on screen, group state included.</param>
    public static bool AllConsentGiven(IReadOnlyList<CategorySettingsRow> rows)
    {
        var sawServerEnabledRow = false;
        var allEnabled = true;

        foreach (var row in rows)
        {
            if (!row.ServerEnabled)
                continue;

            sawServerEnabledRow = true;

            // `&=` is "and-assign": allEnabled stays true only while every row it meets is on.
            allEnabled &= row.UserEnabled;

            if (row.Groups is not { Count: > 0 } groups)
                continue;

            foreach (var group in groups)
                allEnabled &= group.Enabled;
        }

        return allEnabled && sawServerEnabledRow;
    }

    /// <summary>
    /// True when the server offers consent groups for this collection and the user has none of them
    /// switched on — so there is nothing the pass is allowed to look for.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the item collector that acts on it: that collector reads game memory
    /// and cannot be constructed outside the game, so a rule deciding whether a whole collection is
    /// scanned at all would otherwise only ever be witnessed in game. Distinct from an empty manifest,
    /// which means the server asked about nothing — this means it asked, and the user has not agreed to
    /// answer.
    /// </remarks>
    /// <param name="context">The collect context for this pass.</param>
    public static bool GroupsOfferedButNoneEnabled(CollectContext context) =>
        context.RemoteConfig?.ItemManifestGroups is { Count: > 0 }
        && context.EnabledGroups.Count == 0;

    /// <summary>True when the user has at least one of these groups switched on.</summary>
    private static bool AnyGroupEnabled(
        IReadOnlyList<ItemGroupRow> groups, PluginSettings settings)
    {
        foreach (var group in groups)
        {
            if (settings.IsItemGroupEnabled(group.Key))
                return true;
        }

        return false;
    }
}
