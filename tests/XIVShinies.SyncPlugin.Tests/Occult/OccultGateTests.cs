using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// The occult tracker's consent gate. The manager consults it every tick, so flipping any of these
// switches stops the tracker within a second.
public class OccultGateTests
{
    // Passes the local shape check; see TokenFormat.
    private const string UsableToken = "xvs_" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    // A made-up website address, so a test can tell a passed-in address from one written into the
    // copy. The rows below spell each sentence out rather than call ServerOffCopy.
    private const string Host = "shinies.example";

    // The paused line, which two rows below expect.
    private const string PausedLine =
        "Syncing is off on " + Host + ". If you turned off \"Sync from the plugin\" on your profile, "
        + "turn it back on there. Your own choices are unchanged.";

    private static PluginSettings OptedIn() => new()
    {
        MasterEnabled = true,
        OnboardingComplete = true,
        Token = UsableToken,
    };

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
        // The defaults leave the master switch off, onboarding unfinished, and no token.
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

    // Off until /config arrives, as with a config lacking the occultTracker block (see OccultGate).
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

    // ServerHasSwitchedOff decides whether the toggle draws grayed with an "Off" chip. It must agree
    // with CanTrack about what the server allows, or the control misdescribes what happens.

    [Fact]
    public void A_server_that_switched_the_tracker_off_is_reported_off()
    {
        Assert.True(OccultGate.ServerHasSwitchedOff(ConfigWithTracker(trackerEnabled: false)));
    }

    // A server that never advertised the endpoint cannot serve it. This arm differs from the
    // unknown-category rule.
    [Fact]
    public void A_config_carrying_no_tracker_block_is_reported_off()
    {
        var config = ConfigWithTracker() with { OccultTracker = null };

        Assert.True(OccultGate.ServerHasSwitchedOff(config));
    }

    // The arm most likely to regress. Before the first /config answers, the server has forbidden
    // nothing, so the toggle shows the user's own choice rather than graying out.
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

    // An equivalence, not an implication: a one-way check misses the drift that matters, the gate
    // refusing while the control still reads live. A config that has not arrived is left out: it is
    // the one asymmetric case, documented on ServerHasSwitchedOff and tested above.
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

    // A paused server says nothing about the tracker, so the chip names the pause first, as the
    // collection rows do. The both-off row settles the precedence: with the arms swapped, each switch
    // alone would still pass.
    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, "Temporarily switched off by " + Host + ".")]
    [InlineData(false, true, PausedLine)]
    [InlineData(false, false, PausedLine)]
    public void The_chip_names_the_pause_before_the_feature(
        bool globallyEnabled, bool trackerEnabled, string? expected)
    {
        var config = ConfigWithTracker(
            globallyEnabled: globallyEnabled, trackerEnabled: trackerEnabled);

        Assert.Equal(expected, OccultGate.ServerOffText(config, Host));

        Assert.Equal(
            OccultGate.ServerHasSwitchedOff(config), OccultGate.ServerOffText(config, Host) is not null);
    }

    // Nothing was switched off, so the chip says the website does not offer it.
    [Fact]
    public void A_config_with_no_tracker_block_says_the_server_does_not_offer_it()
    {
        var config = ConfigWithTracker() with { OccultTracker = null };

        Assert.Equal(ServerOffCopy.NotOffered(Host), OccultGate.ServerOffText(config, Host));
    }

    // A pause outranks a missing block: it stops every upload, whatever the server offers.
    [Fact]
    public void A_paused_server_with_no_tracker_block_shows_the_pause()
    {
        var config = ConfigWithTracker(globallyEnabled: false) with { OccultTracker = null };

        Assert.Equal(ServerOffCopy.Paused(Host), OccultGate.ServerOffText(config, Host));
    }

    [Fact]
    public void A_permitted_tracker_has_nothing_to_say()
    {
        Assert.Null(OccultGate.ServerOffText(ConfigWithTracker(), Host));
        Assert.Null(OccultGate.ServerOffText(null, Host));
    }

    // --- When the card names the master switch -----------------------------------------------

    private static PluginSettings SwitchedOff()
    {
        var settings = OptedIn();
        settings.MasterEnabled = false;
        return settings;
    }

    // Past onboarding, no /config is fetched while the master switch is off, so a session that
    // starts with it off has none; one switched off mid-session keeps the last.
    [Fact]
    public void The_master_switch_is_named_with_or_without_a_config()
    {
        Assert.True(OccultGate.StoppedBySyncSwitch(SwitchedOff(), ConfigWithTracker()));
        Assert.True(OccultGate.StoppedBySyncSwitch(SwitchedOff(), remoteConfig: null));
    }

    // With an unusable token, switching syncing on would not start the tracker either.
    [Fact]
    public void An_unusable_token_is_not_stopped_by_the_switch()
    {
        var settings = SwitchedOff();
        settings.Token = "not-a-token";

        Assert.False(OccultGate.StoppedBySyncSwitch(settings, ConfigWithTracker()));
    }

    [Fact]
    public void A_switch_that_is_on_is_not_named()
    {
        Assert.False(OccultGate.StoppedBySyncSwitch(OptedIn(), ConfigWithTracker()));
    }

    [Fact]
    public void A_tracker_switched_off_by_the_user_is_not_stopped_by_the_switch()
    {
        var settings = SwitchedOff();
        settings.ShareOccultInstanceState = false;

        Assert.False(OccultGate.StoppedBySyncSwitch(settings, ConfigWithTracker()));
    }

    // The server's "Off" chip explains these, so the card names no switch of the user's.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_tracker_the_server_rules_out_is_not_stopped_by_the_switch(
        bool globallyEnabled, bool trackerEnabled)
    {
        var config = ConfigWithTracker(globallyEnabled: globallyEnabled, trackerEnabled: trackerEnabled);
        var noBlock = ConfigWithTracker() with { OccultTracker = null };

        Assert.False(OccultGate.StoppedBySyncSwitch(SwitchedOff(), config));
        Assert.False(OccultGate.StoppedBySyncSwitch(SwitchedOff(), noBlock));
    }

    // The setup wizard draws the card before its last step turns the master switch on.
    [Fact]
    public void The_setup_wizard_names_no_switch()
    {
        var settings = SwitchedOff();
        settings.OnboardingComplete = false;

        Assert.False(OccultGate.StoppedBySyncSwitch(settings, ConfigWithTracker()));
    }

    [Fact]
    public void Turning_the_switch_on_is_all_the_tracker_needs()
    {
        var settings = SwitchedOff();
        Assert.True(OccultGate.StoppedBySyncSwitch(settings, ConfigWithTracker()));

        settings.MasterEnabled = true;
        Assert.True(OccultGate.CanTrack(settings, ConfigWithTracker()));
    }
}
