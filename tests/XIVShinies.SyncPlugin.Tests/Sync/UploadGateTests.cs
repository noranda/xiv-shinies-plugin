using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The last line of defense before anything is uploaded. The API client checks the token's shape and
// the backend's safety; only this gate knows whether the USER agreed to any of it. "No silent
// upload" is a Dalamud compliance rule, so it is enforced in code, not just in the UI.
public class UploadGateTests
{
    private const string ValidToken = "xvs_0123456789012345678901234567890123456789abc";

    private static PluginSettings ReadyToUpload() => new()
    {
        MasterEnabled = true,
        OnboardingComplete = true,
        Token = ValidToken,
    };

    private static ConfigResponse Remote(bool enabled = true) => new()
    {
        Categories = new Dictionary<string, bool>(),
        Enabled = enabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
    };

    [Fact]
    public void Permits_upload_once_the_user_has_opted_in_with_a_token()
    {
        Assert.True(UploadGate.CanUpload(ReadyToUpload(), Remote()));
    }

    [Fact]
    public void Refuses_while_the_master_switch_is_off()
    {
        var settings = ReadyToUpload();
        settings.MasterEnabled = false;

        Assert.False(UploadGate.CanUpload(settings, Remote()));
    }

    // A fresh install must upload nothing before the user has seen what gets sent.
    [Fact]
    public void Refuses_before_onboarding_completes()
    {
        var settings = ReadyToUpload();
        settings.OnboardingComplete = false;

        Assert.False(UploadGate.CanUpload(settings, Remote()));
    }

    [Fact]
    public void Refuses_without_a_usable_token()
    {
        var settings = ReadyToUpload();
        settings.Token = "nonsense";

        Assert.False(UploadGate.CanUpload(settings, Remote()));
    }

    // The server's global kill switch. Honoring it locally saves a round trip that would 503.
    [Fact]
    public void Refuses_while_the_server_kill_switch_is_off()
    {
        Assert.False(UploadGate.CanUpload(ReadyToUpload(), Remote(enabled: false)));
    }

    // Not having fetched /config yet is not a reason to refuse: the server enforces its own
    // switches, and refusing would strand the plugin whenever /config is unreachable.
    [Fact]
    public void Permits_upload_before_the_config_has_been_fetched()
    {
        Assert.True(UploadGate.CanUpload(ReadyToUpload(), remoteConfig: null));
    }

    // Contacting the server at all needs the user's consent and a token, but NOT the server's
    // permission — the /config poll is how the plugin discovers the kill switch in the first place.
    [Fact]
    public void Permits_contacting_the_server_even_while_the_kill_switch_is_off()
    {
        var settings = ReadyToUpload();

        Assert.False(UploadGate.CanUpload(settings, Remote(enabled: false)));

        // Otherwise a flipped kill switch would be permanent: the plugin could never poll /config
        // again to learn it had been flipped back.
        Assert.True(UploadGate.CanContactServer(settings));
    }

    [Theory]
    [InlineData(false, true, ValidToken)] // master switch off
    [InlineData(true, false, ValidToken)] // never onboarded
    [InlineData(true, true, "nonsense")]  // no usable token
    public void Refuses_to_contact_the_server_without_consent_and_a_token(
        bool masterEnabled, bool onboardingComplete, string token)
    {
        var settings = new PluginSettings
        {
            MasterEnabled = masterEnabled,
            OnboardingComplete = onboardingComplete,
            Token = token,
        };

        Assert.False(UploadGate.CanContactServer(settings));
    }
}
