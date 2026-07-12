using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// Every user-facing setting, and the rules that govern them.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of Dalamud types. <see cref="Configuration"/> implements Dalamud's
/// <c>IPluginConfiguration</c>, which means merely constructing it requires loading
/// <c>Dalamud.dll</c> — impossible outside the running game. Keeping the settings and their logic
/// here lets the xUnit suite exercise them directly, while <see cref="Configuration"/> stays a
/// thin persistence shell.
/// </para>
/// <para>
/// Every default is "off". A fresh install must upload nothing until the user has been shown what
/// gets sent and has explicitly opted in — a Dalamud compliance rule, not a preference.
/// </para>
/// </remarks>
[Serializable]
public class PluginSettings
{
    /// <summary>
    /// The XIV Shinies API token, pasted by the user. Stored as plain text in Dalamud's plugin
    /// config, which is standard for the ecosystem: it is a plugin-scoped, revocable credential
    /// that cannot act on the account itself.
    /// </summary>
    /// <remarks>Never write this value to the log.</remarks>
    public string Token { get; set; } = string.Empty;

    /// <summary>The master switch. While false, the plugin uploads nothing at all.</summary>
    public bool MasterEnabled { get; set; }

    /// <summary>True once the user has completed the first-run wizard and chosen categories.</summary>
    public bool OnboardingComplete { get; set; }

    /// <summary>
    /// The backend server. User-overridable per Dalamud's recommendation; validate any change with
    /// <see cref="BackendUrl.TryNormalize"/> before storing it.
    /// </summary>
    public string BaseUrl { get; set; } = BackendUrl.Default;

    /// <summary>
    /// True once the user has acknowledged that pointing the plugin at a non-default server sends
    /// their API token to that server. Reset this whenever <see cref="BaseUrl"/> changes.
    /// </summary>
    public bool CustomBackendAcknowledged { get; set; }

    /// <summary>
    /// Which collection categories the user opted into, keyed by the collector's category key
    /// (for example <c>"quests"</c>).
    /// </summary>
    /// <remarks>
    /// A dictionary rather than one named property per category, on purpose: adding a new
    /// collection must be one new collector class and nothing else. Naming the categories here
    /// would force a settings change (and a migration) for every new collection.
    /// </remarks>
    // A Dictionary maps keys to values — like a JS object used as a lookup, or a `Map`.
    public Dictionary<string, bool> EnabledCategories { get; set; } = new();

    /// <summary>
    /// True when the user has opted into uploading the given category. An unknown key reads as
    /// false, so a collector added in a later version starts opted-out rather than silently on.
    /// </summary>
    // `TryGetValue` is the allocation-free "look it up, tell me if it was there" pattern: it
    // returns a bool and hands the value back through the `out` parameter. The blank-key guard
    // matters because a Dictionary throws on a null key rather than simply missing.
    public bool IsCategoryEnabled(string categoryKey) =>
        !string.IsNullOrEmpty(categoryKey)
        && EnabledCategories.TryGetValue(categoryKey, out var enabled)
        && enabled;

    /// <summary>Opts the given category in or out.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public void SetCategoryEnabled(string categoryKey, bool enabled)
    {
        // Fail loudly on a blank key rather than writing an unreachable entry. Reading tolerates a
        // blank key (returns false); writing one is always a caller bug.
        ArgumentException.ThrowIfNullOrEmpty(categoryKey);

        EnabledCategories[categoryKey] = enabled;
    }

    /// <summary>
    /// Which item manifest groups the user opted into, by the server's group key. An opt-in
    /// ALLOWLIST: an unknown key reads as disabled, so a group added server-side starts OFF
    /// until the user ticks it.
    /// </summary>
    public List<string> EnabledItemGroupKeys { get; set; } = new();

    /// <summary>Group keys the settings UI has already shown once — everything else gets a "New" badge.</summary>
    public List<string> SeenItemGroupKeys { get; set; } = new();

    /// <summary>True once the one-time pre-group consent migration has run.</summary>
    public bool ItemGroupConsentMigrated { get; set; }

    /// <summary>
    /// True when the user has opted into the given item group. An unknown key reads as false, so
    /// a group added in a later version starts opted-out rather than silently on.
    /// </summary>
    // `Contains` on a List is O(n), but the list stays tiny (a handful of group keys), so a plain
    // List wins on simplicity. If groups ever number in the hundreds, switch to a HashSet.
    public bool IsItemGroupEnabled(string groupKey) =>
        !string.IsNullOrEmpty(groupKey)
        && EnabledItemGroupKeys.Contains(groupKey);

    /// <summary>Opts the given item group in or out.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public void SetItemGroupEnabled(string groupKey, bool enabled)
    {
        // Fail loudly on a blank key rather than writing an unreachable entry. Reading tolerates a
        // blank key (returns false); writing one is always a caller bug.
        ArgumentException.ThrowIfNullOrEmpty(groupKey);

        if (enabled)
        {
            // Add to the list only if it is not already there (idempotent, no duplicates).
            if (!EnabledItemGroupKeys.Contains(groupKey))
            {
                EnabledItemGroupKeys.Add(groupKey);
            }
        }
        else
        {
            // Remove from the list if present. Remove(item) does nothing if the item is not in the list.
            EnabledItemGroupKeys.Remove(groupKey);
        }
    }

    /// <summary>True when the settings UI has already shown the given item group once.</summary>
    public bool IsItemGroupSeen(string groupKey) =>
        !string.IsNullOrEmpty(groupKey)
        && SeenItemGroupKeys.Contains(groupKey);

    /// <summary>Mark the given item groups as seen in the settings UI.</summary>
    /// <remarks>
    /// <para>
    /// Idempotent: calling this multiple times with the same group keys does not duplicate list
    /// entries.
    /// </para>
    /// <para>
    /// Deliberately best-effort, unlike <see cref="SetItemGroupEnabled"/>'s throw-on-blank
    /// convention: a null sequence is a no-op, and a blank key is skipped while the rest of the
    /// batch is still marked. The keys here come from server-supplied group data during UI
    /// rendering, so a malformed group must degrade gracefully rather than crash the draw loop.
    /// <see cref="SetItemGroupEnabled"/>, by contrast, receives the key of a known row the user
    /// clicked — a blank key there is always a caller bug worth failing loudly on.
    /// </para>
    /// </remarks>
    public void MarkItemGroupsSeen(IEnumerable<string> groupKeys)
    {
        if (groupKeys == null)
        {
            return;
        }

        foreach (var groupKey in groupKeys)
        {
            // Add to the list only if it is not already there (idempotent, no duplicates).
            if (!string.IsNullOrEmpty(groupKey) && !SeenItemGroupKeys.Contains(groupKey))
            {
                SeenItemGroupKeys.Add(groupKey);
            }
        }
    }

    /// <summary>
    /// One-time migration: a user whose Items toggle was on had already consented to the
    /// scope the server now marks <c>legacy: true</c> — enable exactly those groups. Every
    /// other group starts OFF regardless (explicit opt-in is a Dalamud rule, not a preference).
    /// Legacy groups are also marked seen: they are not new to this user. Returns true when
    /// the migration ran (caller persists the config), false when it had already run.
    /// </summary>
    /// <param name="groups">
    /// The item manifest groups from the server. Must be non-null — pass an empty list when the
    /// config carries no groups.
    /// </param>
    /// <param name="itemsCategoryEnabled">
    /// Whether the user had the Items category enabled. Passed in rather than looked up so this
    /// stays free of category-name knowledge — the caller owns which category the manifest
    /// belongs to.
    /// </param>
    /// <returns>When true, the caller should persist the updated config.</returns>
    public bool MigrateItemGroupConsent(
        IReadOnlyList<ItemManifestGroup> groups, bool itemsCategoryEnabled)
    {
        // Run only once.
        if (ItemGroupConsentMigrated)
        {
            return false;
        }

        // Mark that the migration has run, regardless of the outcome.
        ItemGroupConsentMigrated = true;

        // Collect the legacy group keys once; both steps below operate on the same set. A blank
        // key is skipped rather than passed on: group data comes from the server, and a malformed
        // group must degrade gracefully here for the same reason it does in MarkItemGroupsSeen —
        // SetItemGroupEnabled would throw on it, and a throw mid-migration would leave the
        // run-once flag set with only part of the work done.
        var legacyGroupKeys = new List<string>();
        foreach (var group in groups)
        {
            if (group.Legacy && !string.IsNullOrEmpty(group.Key))
            {
                legacyGroupKeys.Add(group.Key);
            }
        }

        // Enabling is conditional on the user's prior Items consent — a group is only turned on
        // when its scope was already covered by what the user agreed to send.
        if (itemsCategoryEnabled)
        {
            foreach (var groupKey in legacyGroupKeys)
            {
                SetItemGroupEnabled(groupKey, true);
            }
        }

        // Seen-marking is UNCONDITIONAL: a legacy group predates this user's install either way,
        // so it must never wear a "New" badge — even for a user whose items consent was off.
        MarkItemGroupsSeen(legacyGroupKeys);

        return true;
    }

    /// <summary>
    /// True when a token is present and has the shape the server issues. A local sanity check
    /// only — only the server can say whether a well-formed token is actually valid.
    /// </summary>
    // Deliberately a method rather than a property: Dalamud's serializer would otherwise write
    // this computed value into the saved config file as a redundant field.
    public bool HasUsableToken() => TokenFormat.IsWellFormed(Token);
}
