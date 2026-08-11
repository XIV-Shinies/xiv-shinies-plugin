using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// The occult tracker's consent gate. The manager consults it every tick, so each refusal
// here is a live guarantee: flipping any of these switches stops the tracker within a second.
public class OccultGateTests
{
    // A token whose shape passes the local validity check: xvs_ + exactly 43 base64url
    // characters (see TokenFormat).
    private const string UsableToken = "xvs_" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    /// <summary>Settings for a user who has fully opted in.</summary>
    private static PluginSettings OptedIn() => new()
    {
        MasterEnabled = true,
        OnboardingComplete = true,
        Token = UsableToken,
    };

    /// <summary>A config whose occult tracker is advertised and switched on.</summary>
    private static ConfigResponse ConfigWithTracker(bool trackerEnabled = true, bool globallyEnabled = true) => new()
    {
        Categories = new Dictionary<string, bool>(),
        Enabled = globallyEnabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = [],
        ManifestVersion = "x",
        OccultTracker = new OccultTrackerConfig { Enabled = trackerEnabled },
    };

    [Fact]
    public void A_fresh_install_cannot_track()
    {
        // Every default is off: no master switch, no onboarding, no token.
        Assert.False(OccultGate.CanTrack(new PluginSettings(), ConfigWithTracker()));
    }

    [Fact]
    public void The_master_switch_gates_tracking()
    {
        var settings = OptedIn();
        settings.MasterEnabled = false;

        Assert.False(OccultGate.CanTrack(settings, ConfigWithTracker()));
    }

    [Fact]
    public void Incomplete_onboarding_gates_tracking()
    {
        var settings = OptedIn();
        settings.OnboardingComplete = false;

        Assert.False(OccultGate.CanTrack(settings, ConfigWithTracker()));
    }

    [Fact]
    public void The_feature_toggle_gates_tracking()
    {
        var settings = OptedIn();
        settings.ShareOccultInstanceState = false;

        Assert.False(OccultGate.CanTrack(settings, ConfigWithTracker()));
    }

    // Opposite of the unknown-category rule, deliberately: this is a separate endpoint, and
    // a server that has not advertised it would 404 every upload. No config yet — same.
    [Fact]
    public void No_config_yet_means_no_tracking()
    {
        Assert.False(OccultGate.CanTrack(OptedIn(), remoteConfig: null));
    }

    [Fact]
    public void A_config_without_the_occultTracker_block_means_no_tracking()
    {
        var config = ConfigWithTracker() with { OccultTracker = null };

        Assert.False(OccultGate.CanTrack(OptedIn(), config));
    }

    [Fact]
    public void The_server_tracker_switch_gates_tracking()
    {
        Assert.False(OccultGate.CanTrack(OptedIn(), ConfigWithTracker(trackerEnabled: false)));
    }

    [Fact]
    public void The_server_global_kill_switch_gates_tracking()
    {
        Assert.False(OccultGate.CanTrack(OptedIn(), ConfigWithTracker(globallyEnabled: false)));
    }

    [Fact]
    public void A_fully_opted_in_user_with_an_enabled_server_tracks()
    {
        Assert.True(OccultGate.CanTrack(OptedIn(), ConfigWithTracker()));
    }
}
