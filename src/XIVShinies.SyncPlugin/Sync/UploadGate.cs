using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides whether the plugin may upload anything at all, right now.
/// </summary>
/// <remarks>
/// <para>
/// This is the gate that enforces <b>consent</b>. The API client independently refuses to send a
/// malformed token, or to send any token to an unsafe or unacknowledged backend — but it knows
/// nothing about whether the user ever agreed to sync. Dalamud requires an explicit opt-in before
/// non-essential data is collected, so that decision is enforced in code here, not merely reflected
/// by a checkbox in the settings window.
/// </para>
/// <para>
/// Per-category consent is a separate question, answered by
/// <see cref="Collectors.CollectorGate"/> once an upload is permitted at all.
/// </para>
/// </remarks>
public static class UploadGate
{
    /// <summary>
    /// True when the plugin may make any request at all — including the <c>/config</c> poll.
    /// </summary>
    /// <remarks>
    /// Consent plus a credential, and deliberately nothing else. In particular it does not consult
    /// the server's kill switch, because the poll that <i>reads</i> that switch has to be allowed to
    /// run. Gating the poll on its own result would make a flipped switch permanent: the plugin
    /// could never learn it had been flipped back.
    /// </remarks>
    public static bool CanContactServer(PluginSettings settings)
    {
        // The user's own switches. A fresh install talks to nobody: both default to false.
        if (!settings.MasterEnabled || !settings.OnboardingComplete)
            return false;

        // No credential, nothing to authenticate with. Checking the shape locally avoids a request
        // that could only ever earn an opaque 401.
        return settings.HasUsableToken();
    }

    /// <summary>True when an upload may be attempted.</summary>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if it has not been fetched.</param>
    public static bool CanUpload(PluginSettings settings, ConfigResponse? remoteConfig)
    {
        if (!CanContactServer(settings))
            return false;

        // A config we have not fetched cannot forbid anything. Refusing here would strand the plugin
        // whenever /config is unreachable, and the server enforces its own switches regardless.
        return remoteConfig is null || remoteConfig.Enabled;
    }
}
