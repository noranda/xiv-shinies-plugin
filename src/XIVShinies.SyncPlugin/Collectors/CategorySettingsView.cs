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

                // A config we have not fetched forbids nothing, matching how the collectors and the
                // upload gate treat it. Otherwise a plugin that cannot reach /config would show every
                // category as disabled by the server, which would be a lie.
                ServerEnabled = remoteConfig?.IsCategoryEnabled(key) ?? true,

                // `TryGetValue` fills the out parameter and returns whether the key existed. The
                // discard-style pattern below just means "null when it was not there".
                SkipReason = lastSkipped is not null && lastSkipped.TryGetValue(key, out var reason)
                    ? reason
                    : null,
            });
        }

        return rows;
    }
}
