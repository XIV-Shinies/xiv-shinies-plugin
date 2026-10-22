using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The last check before any upload. The API client vets the token's shape and the backend; only this
// gate knows whether the user agreed. "No silent upload" is a Dalamud rule, so the code enforces it,
// not only the UI.
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

    // Honoring the server's kill switch locally saves a round trip that would 503.
    [Fact]
    public void Refuses_while_the_server_kill_switch_is_off()
    {
        Assert.False(UploadGate.CanUpload(ReadyToUpload(), Remote(enabled: false)));
    }

    // Proceeding without /config is safe (see UploadGate.CanUpload).
    [Fact]
    public void Permits_upload_before_the_config_has_been_fetched()
    {
        Assert.True(UploadGate.CanUpload(ReadyToUpload(), remoteConfig: null));
    }

    // Contact needs consent and a token, not the server's permission. Otherwise a flipped kill switch
    // would be permanent: the plugin could never poll /config to learn it was flipped back.
    [Fact]
    public void Permits_contacting_the_server_even_while_the_kill_switch_is_off()
    {
        var settings = ReadyToUpload();

        Assert.False(UploadGate.CanUpload(settings, Remote(enabled: false)));

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
