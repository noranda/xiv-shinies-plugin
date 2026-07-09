using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides whether a category may be collected and uploaded at all.
/// </summary>
/// <remarks>
/// Four switches must all be on, and any one of them off wins. Two belong to the user (the master
/// switch and their per-category opt-in) and two to the server (the global kill switch and its
/// per-category switch). Keeping this in one pure function — rather than a method on each
/// collector — means every category is gated identically and no collector can forget a check.
/// </remarks>
public static class CollectorGate
{
    /// <summary>True when this category may be collected right now.</summary>
    /// <param name="categoryKey">The collector's category key.</param>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">
    /// The most recent <c>/config</c> response, or null when it has not been fetched yet.
    /// </param>
    public static bool IsEnabled(string categoryKey, PluginSettings settings, ConfigResponse? remoteConfig)
    {
        // The user's own switches come first. Nothing is ever collected before the user has been
        // shown what gets sent and has explicitly opted in — a Dalamud compliance rule.
        if (!settings.MasterEnabled || !settings.OnboardingComplete)
            return false;

        if (!settings.IsCategoryEnabled(categoryKey))
            return false;

        // Without a fetched config we cannot know the server's switches. Proceeding is safe: the
        // server enforces them regardless, stripping disabled categories and answering 503 when its
        // global switch is off. Refusing to collect would instead strand the plugin whenever
        // /config is unreachable.
        if (remoteConfig is null)
            return true;

        if (!remoteConfig.Enabled)
            return false;

        return remoteConfig.IsCategoryEnabled(categoryKey);
    }
}
