using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
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

    // --- What the settings toggle draws ------------------------------------------------------

    // ServerHasSwitchedOff decides whether the toggle draws greyed and chipped "Off". It has to
    // agree with CanTrack about what the server allows, or the control describes something other
    // than what happens — so the three arms are pinned separately from the gate's own tests.

    [Fact]
    public void A_server_that_switched_the_tracker_off_is_reported_off()
    {
        Assert.True(OccultGate.ServerHasSwitchedOff(ConfigWithTracker(trackerEnabled: false)));
    }

    // A server that never advertised the endpoint cannot serve it, so the control must not offer
    // it either. This is the arm that differs from the unknown-category rule.
    [Fact]
    public void A_config_carrying_no_tracker_block_is_reported_off()
    {
        // `with` copies the record and replaces one property — the block is init-only.
        var config = ConfigWithTracker() with { OccultTracker = null };

        Assert.True(OccultGate.ServerHasSwitchedOff(config));
    }

    // The arm most likely to regress: before the first /config answers, the server has forbidden
    // nothing, so the toggle keeps showing the user's own choice rather than greying out.
    [Fact]
    public void A_config_that_has_not_arrived_is_not_reported_off()
    {
        Assert.False(OccultGate.ServerHasSwitchedOff(null));
    }

    [Fact]
    public void An_enabled_tracker_is_not_reported_off()
    {
        Assert.False(OccultGate.ServerHasSwitchedOff(ConfigWithTracker()));
    }

    // The control and the gate must refuse on the same terms. Asserted as an equivalence rather
    // than an implication: the failure they guard against is the two drifting apart, and drift in
    // the direction that matters — the gate refusing while the control still reads live — is
    // invisible to a one-way check, which passes by asserting nothing at all on exactly the
    // configs where the control is wrong.
    //
    // A config that has not arrived is deliberately left out: it is the one asymmetric case (the
    // server has forbidden nothing, but the tracker cannot run yet either), documented on
    // ServerHasSwitchedOff and pinned by its own test above.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void The_control_and_the_gate_refuse_on_exactly_the_same_terms(
        bool globallyEnabled, bool trackerEnabled)
    {
        var config = ConfigWithTracker(
            globallyEnabled: globallyEnabled, trackerEnabled: trackerEnabled);

        Assert.Equal(
            OccultGate.ServerHasSwitchedOff(config),
            !OccultGate.CanTrack(OptedIn(), config));
    }

    // A paused server is not a statement about the tracker, so the chip must not make one — the
    // same precedence the collection rows follow. Every combination is pinned, because the case
    // that settles the precedence is the one where BOTH are off: swapping the two arms of the
    // choice would still satisfy either switch tested alone.
    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, CategorySettingsRow.ServerOffFallback)]
    [InlineData(false, true, CategorySettingsRow.ServerPausedFallback)]
    [InlineData(false, false, CategorySettingsRow.ServerPausedFallback)]
    public void The_chip_names_the_pause_before_the_feature(
        bool globallyEnabled, bool trackerEnabled, string? expected)
    {
        var config = ConfigWithTracker(
            globallyEnabled: globallyEnabled, trackerEnabled: trackerEnabled);

        Assert.Equal(expected, OccultGate.ServerOffText(config));

        // The chip's presence and its sentence are one decision: text exactly when switched off.
        Assert.Equal(OccultGate.ServerHasSwitchedOff(config), OccultGate.ServerOffText(config) is not null);
    }

    // A server that never advertised the tracker cannot serve it, so the feature's own sentence
    // applies even though there is no switch to read.
    [Fact]
    public void A_config_with_no_tracker_block_says_the_tracker_is_off()
    {
        var config = ConfigWithTracker() with { OccultTracker = null };

        Assert.Equal(CategorySettingsRow.ServerOffFallback, OccultGate.ServerOffText(config));
    }

    [Fact]
    public void A_permitted_tracker_has_nothing_to_say()
    {
        Assert.Null(OccultGate.ServerOffText(ConfigWithTracker()));
        Assert.Null(OccultGate.ServerOffText(null));
    }
}
