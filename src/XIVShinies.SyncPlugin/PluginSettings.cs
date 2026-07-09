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
    /// True when a token is present and has the shape the server issues. A local sanity check
    /// only — only the server can say whether a well-formed token is actually valid.
    /// </summary>
    // Deliberately a method rather than a property: Dalamud's serializer would otherwise write
    // this computed value into the saved config file as a redundant field.
    public bool HasUsableToken() => TokenFormat.IsWellFormed(Token);
}
