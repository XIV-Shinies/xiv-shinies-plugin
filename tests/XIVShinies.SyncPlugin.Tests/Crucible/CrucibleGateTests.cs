using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The Crucible run sharing's consent gate. Whatever drives the uploads must ask it before each one,
// so each refusal here guarantees that flipping that switch stops the sharing.
public class CrucibleGateTests
{
    // Passes TokenFormat's shape check: "xvs_" and exactly 43 base64url characters.
    private const string UsableToken = "xvs_" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    // A made-up website address, so a test can tell an address passed in from one written into the
    // copy. The rows below spell each sentence out rather than call ServerOffCopy.
    private const string Host = "shinies.example";

    private static PluginSettings OptedIn() => new()
    {
        MasterEnabled = true,
        OnboardingComplete = true,
        Token = UsableToken,
        ShareCrucibleRuns = true,
    };

    private static ConfigResponse ConfigWithCrucible(
        bool sharingEnabled = true, bool globallyEnabled = true, string? note = null) => new()
    {
        Categories = new Dictionary<string, bool>(),
        Enabled = globallyEnabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = [],
        ManifestVersion = "x",
        CrucibleRuns = new CrucibleRunsConfig { Enabled = sharingEnabled, Note = note },
    };

    // The sharing's own consent starts off, and nothing turns it on for the user.
    [Fact]
    public void A_fresh_install_does_not_share_crucible_runs()
    {
        Assert.False(new PluginSettings().ShareCrucibleRuns);
        Assert.False(CrucibleGate.CanShare(new PluginSettings(), ConfigWithCrucible()));
    }

    [Fact]
    public void The_master_switch_gates_sharing()
    {
        var settings = OptedIn();
        settings.MasterEnabled = false;

        Assert.False(CrucibleGate.CanShare(settings, ConfigWithCrucible()));
    }

    [Fact]
    public void Incomplete_onboarding_gates_sharing()
    {
        var settings = OptedIn();
        settings.OnboardingComplete = false;

        Assert.False(CrucibleGate.CanShare(settings, ConfigWithCrucible()));
    }

    [Fact]
    public void The_sharing_toggle_gates_sharing()
    {
        var settings = OptedIn();
        settings.ShareCrucibleRuns = false;

        Assert.False(CrucibleGate.CanShare(settings, ConfigWithCrucible()));
    }

    // Neither the auto-enable, whatever keys it is handed, nor any upgrade rule turns the sharing on.
    [Fact]
    public void Turning_on_new_collections_automatically_does_not_share_crucible_runs()
    {
        var settings = OptedIn();
        settings.ShareCrucibleRuns = false;
        settings.AutoEnableNewFeatures = true;
        settings.InitializeSeenCategories(["quests"]);

        settings.AutoEnableUnseenCategories(["quests", "mounts", "crucibleRuns"]);
        settings.ApplyUpgradeMigrations(fromVersion: 0);

        Assert.False(settings.ShareCrucibleRuns);
        Assert.False(CrucibleGate.CanShare(settings, ConfigWithCrucible()));
    }

    [Fact]
    public void An_unusable_token_gates_sharing()
    {
        var settings = OptedIn();
        settings.Token = "not-a-token";

        Assert.False(CrucibleGate.CanShare(settings, ConfigWithCrucible()));
    }

    [Fact]
    public void No_config_yet_means_no_sharing()
    {
        Assert.False(CrucibleGate.CanShare(OptedIn(), remoteConfig: null));
    }

    [Fact]
    public void A_config_without_the_crucibleRuns_block_means_no_sharing()
    {
        var config = ConfigWithCrucible() with { CrucibleRuns = null };

        Assert.False(CrucibleGate.CanShare(OptedIn(), config));
    }

    [Fact]
    public void The_server_sharing_switch_gates_sharing()
    {
        Assert.False(CrucibleGate.CanShare(OptedIn(), ConfigWithCrucible(sharingEnabled: false)));
    }

    [Fact]
    public void The_server_global_kill_switch_gates_sharing()
    {
        Assert.False(CrucibleGate.CanShare(OptedIn(), ConfigWithCrucible(globallyEnabled: false)));
    }

    [Fact]
    public void A_fully_opted_in_user_with_an_enabled_server_shares()
    {
        Assert.True(CrucibleGate.CanShare(OptedIn(), ConfigWithCrucible()));
    }

    // --- What a settings toggle should draw -----------------------------------------------------

    [Fact]
    public void A_server_that_switched_the_sharing_off_is_reported_off()
    {
        Assert.True(CrucibleGate.ServerHasSwitchedOff(ConfigWithCrucible(sharingEnabled: false)));
    }

    [Fact]
    public void A_config_carrying_no_crucibleRuns_block_is_reported_off()
    {
        var config = ConfigWithCrucible() with { CrucibleRuns = null };

        Assert.True(CrucibleGate.ServerHasSwitchedOff(config));
    }

    // Before the first /config answers, a toggle keeps showing the user's own choice.
    [Fact]
    public void A_config_that_has_not_arrived_is_not_reported_off()
    {
        Assert.False(CrucibleGate.ServerHasSwitchedOff(null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void The_control_and_the_gate_refuse_on_exactly_the_same_terms(
        bool globallyEnabled, bool sharingEnabled)
    {
        var config = ConfigWithCrucible(sharingEnabled: sharingEnabled, globallyEnabled: globallyEnabled);

        Assert.Equal(
            CrucibleGate.ServerHasSwitchedOff(config),
            !CrucibleGate.CanShare(OptedIn(), config));
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, "Temporarily switched off by " + Host + ".")]
    [InlineData(false, true, Host + " has paused syncing for everyone. Your own choices are unchanged.")]
    [InlineData(false, false, Host + " has paused syncing for everyone. Your own choices are unchanged.")]
    public void The_chip_names_the_pause_before_the_feature(
        bool globallyEnabled, bool sharingEnabled, string? expected)
    {
        var config = ConfigWithCrucible(sharingEnabled: sharingEnabled, globallyEnabled: globallyEnabled);

        Assert.Equal(expected, CrucibleGate.ServerOffText(config, Host));
        Assert.Equal(
            CrucibleGate.ServerHasSwitchedOff(config), CrucibleGate.ServerOffText(config, Host) is not null);
    }

    [Fact]
    public void A_switched_off_sharing_with_a_note_shows_the_note()
    {
        var config = ConfigWithCrucible(sharingEnabled: false, note: "In testing.\nSoon.");

        Assert.Equal("In testing. Soon.", CrucibleGate.ServerOffText(config, Host));
    }

    [Fact]
    public void A_paused_server_shows_the_pause_even_with_a_note()
    {
        var config = ConfigWithCrucible(sharingEnabled: false, globallyEnabled: false, note: "In testing.");

        Assert.Equal(ServerOffCopy.Paused(Host), CrucibleGate.ServerOffText(config, Host));
    }

    [Fact]
    public void A_blank_note_shows_the_generic_line()
    {
        var blankNote = ConfigWithCrucible(sharingEnabled: false, note: "  ");

        Assert.Equal(ServerOffCopy.Feature(Host), CrucibleGate.ServerOffText(blankNote, Host));
    }

    // Nothing was switched off, so the chip must not describe a decision that was never made.
    [Fact]
    public void No_block_says_the_server_does_not_offer_the_sharing()
    {
        var noBlock = ConfigWithCrucible() with { CrucibleRuns = null };

        Assert.Equal(ServerOffCopy.NotOffered(Host), CrucibleGate.ServerOffText(noBlock, Host));
    }

    // A pause stops every upload, whatever the server offers.
    [Fact]
    public void A_paused_server_with_no_block_shows_the_pause()
    {
        var config = ConfigWithCrucible(globallyEnabled: false) with { CrucibleRuns = null };

        Assert.Equal(ServerOffCopy.Paused(Host), CrucibleGate.ServerOffText(config, Host));
    }

    // Nothing is switched off for the note to explain.
    [Fact]
    public void A_note_on_enabled_sharing_shows_nothing()
    {
        Assert.Null(CrucibleGate.ServerOffText(ConfigWithCrucible(note: "In testing."), Host));
    }

    [Fact]
    public void A_permitted_sharing_has_nothing_to_say()
    {
        Assert.Null(CrucibleGate.ServerOffText(ConfigWithCrucible(), Host));
        Assert.Null(CrucibleGate.ServerOffText(null, Host));
    }

    // --- When the card names the master switch ---------------------------------------------------

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
        Assert.True(CrucibleGate.StoppedBySyncSwitch(SwitchedOff(), ConfigWithCrucible()));
        Assert.True(CrucibleGate.StoppedBySyncSwitch(SwitchedOff(), remoteConfig: null));
    }

    // With an unusable token, switching syncing on would not start the sharing either.
    [Fact]
    public void An_unusable_token_is_not_stopped_by_the_switch()
    {
        var settings = SwitchedOff();
        settings.Token = "not-a-token";

        Assert.False(CrucibleGate.StoppedBySyncSwitch(settings, ConfigWithCrucible()));
    }

    [Fact]
    public void A_switch_that_is_on_is_not_named()
    {
        Assert.False(CrucibleGate.StoppedBySyncSwitch(OptedIn(), ConfigWithCrucible()));
    }

    [Fact]
    public void A_sharing_switched_off_by_the_user_is_not_stopped_by_the_switch()
    {
        var settings = SwitchedOff();
        settings.ShareCrucibleRuns = false;

        Assert.False(CrucibleGate.StoppedBySyncSwitch(settings, ConfigWithCrucible()));
    }

    // The server's "Off" chip explains these, so the card names no switch of the user's.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_sharing_the_server_rules_out_is_not_stopped_by_the_switch(
        bool globallyEnabled, bool sharingEnabled)
    {
        var config = ConfigWithCrucible(sharingEnabled: sharingEnabled, globallyEnabled: globallyEnabled);
        var noBlock = ConfigWithCrucible() with { CrucibleRuns = null };

        Assert.False(CrucibleGate.StoppedBySyncSwitch(SwitchedOff(), config));
        Assert.False(CrucibleGate.StoppedBySyncSwitch(SwitchedOff(), noBlock));
    }

    // The setup wizard draws the card before its last step turns the master switch on.
    [Fact]
    public void The_setup_wizard_names_no_switch()
    {
        var settings = SwitchedOff();
        settings.OnboardingComplete = false;

        Assert.False(CrucibleGate.StoppedBySyncSwitch(settings, ConfigWithCrucible()));
    }

    // Naming the switch claims it is the only thing stopping the sharing.
    [Fact]
    public void Turning_the_switch_on_is_all_the_sharing_needs()
    {
        var settings = SwitchedOff();
        Assert.True(CrucibleGate.StoppedBySyncSwitch(settings, ConfigWithCrucible()));

        settings.MasterEnabled = true;
        Assert.True(CrucibleGate.CanShare(settings, ConfigWithCrucible()));
    }
}
