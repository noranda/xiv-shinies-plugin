using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Kill-switch precedence, pinned directly rather than only through the runner. Four switches must
// all be on; any one of them off wins. Two belong to the user, two to the server.
public class CollectorGateTests
{
    private const string Category = "quests";

    private static PluginSettings FullyOptedIn()
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };
        settings.SetCategoryEnabled(Category, true);
        return settings;
    }

    private static ConfigResponse Remote(bool enabled = true, bool categoryEnabled = true) => new()
    {
        Categories = new Dictionary<string, bool> { [Category] = categoryEnabled },
        Enabled = enabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
    };

    [Fact]
    public void All_four_switches_on_permits_collection()
    {
        Assert.True(CollectorGate.IsEnabled(Category, FullyOptedIn(), Remote()));
    }

    [Fact]
    public void The_user_master_switch_off_wins()
    {
        var settings = FullyOptedIn();
        settings.MasterEnabled = false;

        Assert.False(CollectorGate.IsEnabled(Category, settings, Remote()));
    }

    // Nothing may be collected before the user has been shown what gets sent and opted in.
    [Fact]
    public void Incomplete_onboarding_wins()
    {
        var settings = FullyOptedIn();
        settings.OnboardingComplete = false;

        Assert.False(CollectorGate.IsEnabled(Category, settings, Remote()));
    }

    [Fact]
    public void A_category_the_user_did_not_opt_into_wins()
    {
        var settings = FullyOptedIn();
        settings.SetCategoryEnabled(Category, false);

        Assert.False(CollectorGate.IsEnabled(Category, settings, Remote()));
    }

    [Fact]
    public void The_server_global_kill_switch_wins()
    {
        Assert.False(CollectorGate.IsEnabled(Category, FullyOptedIn(), Remote(enabled: false)));
    }

    [Fact]
    public void The_server_per_category_kill_switch_wins()
    {
        Assert.False(CollectorGate.IsEnabled(Category, FullyOptedIn(), Remote(categoryEnabled: false)));
    }

    // Without a fetched config we cannot know the server's switches. Proceeding is safe — the
    // server strips disabled categories and answers 503 on its global switch — and refusing would
    // strand the plugin whenever /config is unreachable.
    [Fact]
    public void A_missing_remote_config_falls_back_to_the_users_own_switches()
    {
        Assert.True(CollectorGate.IsEnabled(Category, FullyOptedIn(), remoteConfig: null));
    }

    // ...but the user's switches still govern when the config is missing.
    [Fact]
    public void A_missing_remote_config_never_overrides_the_user()
    {
        var settings = FullyOptedIn();
        settings.MasterEnabled = false;

        Assert.False(CollectorGate.IsEnabled(Category, settings, remoteConfig: null));
    }

    // A category the server has never heard of is permitted: the server strips unknown payload keys,
    // so a collector can ship before the server grows its switch.
    [Fact]
    public void A_category_the_server_never_mentions_is_permitted()
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };
        settings.SetCategoryEnabled("facewear", true);

        Assert.True(CollectorGate.IsEnabled("facewear", settings, Remote()));
    }
}
