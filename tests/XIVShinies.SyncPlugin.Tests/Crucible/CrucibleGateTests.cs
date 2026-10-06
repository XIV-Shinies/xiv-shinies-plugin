using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The Crucible run sharing's consent gate. Whatever drives the uploads must ask it before each one,
// so each refusal here guarantees that flipping that switch stops the sharing. Each `[Fact]` is one
// test, like `it(...)` in Jest; a `[Theory]` runs once per `[InlineData]` row, like `it.each`.
public class CrucibleGateTests
{
    // A token whose shape passes the local validity check: xvs_ + exactly 43 base64url characters
    // (see TokenFormat).
    private const string UsableToken = "xvs_" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    /// <summary>Settings for a user who has opted in to everything, Crucible sharing included.</summary>
    // `=> new() { ... }` builds a PluginSettings and sets these properties on it, like an object
    // literal; every property it leaves out keeps its default.
    private static PluginSettings OptedIn() => new()
    {
        MasterEnabled = true,
        OnboardingComplete = true,
        Token = UsableToken,
        ShareCrucibleRuns = true,
    };

    /// <summary>
    /// A config advertising the Crucible endpoint, with both switches on unless an argument turns
    /// one off.
    /// </summary>
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

    // Turning on new collections automatically is a separate choice that does not reach this one:
    // running the auto-enable, whatever keys it is handed, and every upgrade rule on an install
    // that answered yes to it leaves the sharing off.
    [Fact]
    public void Turning_on_new_collections_automatically_does_not_share_crucible_runs()
    {
        var settings = OptedIn();
        settings.ShareCrucibleRuns = false;
        settings.AutoEnableNewFeatures = true;
        settings.InitializeSeenCategories(["quests"]);

        settings.AutoEnableUnseenCategories(["quests", "mounts", "crucibleRuns"]);
        // `fromVersion: 0` names the parameter it fills, so the call says what the 0 means.
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

    // No config yet, or a config without the block, means no sharing.
    [Fact]
    public void No_config_yet_means_no_sharing()
    {
        Assert.False(CrucibleGate.CanShare(OptedIn(), remoteConfig: null));
    }

    [Fact]
    public void A_config_without_the_crucibleRuns_block_means_no_sharing()
    {
        // `with` copies the record and replaces one property, since an `init` property can only be
        // set while the object is being built.
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

    // The control and the gate must refuse on the same terms, checked as an equivalence over every
    // pair of server switches.
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

    // The chip names a pause before the feature's own switch.
    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, ServerOffCopy.Feature)]
    [InlineData(false, true, ServerOffCopy.Paused)]
    [InlineData(false, false, ServerOffCopy.Paused)]
    public void The_chip_names_the_pause_before_the_feature(
        bool globallyEnabled, bool sharingEnabled, string? expected)
    {
        var config = ConfigWithCrucible(sharingEnabled: sharingEnabled, globallyEnabled: globallyEnabled);

        Assert.Equal(expected, CrucibleGate.ServerOffText(config));
        Assert.Equal(
            CrucibleGate.ServerHasSwitchedOff(config), CrucibleGate.ServerOffText(config) is not null);
    }

    // The server's own note replaces the generic line, folded to a single line.
    [Fact]
    public void A_switched_off_sharing_with_a_note_shows_the_note()
    {
        var config = ConfigWithCrucible(sharingEnabled: false, note: "In testing.\nSoon.");

        Assert.Equal("In testing. Soon.", CrucibleGate.ServerOffText(config));
    }

    // A pause outranks a note as well.
    [Fact]
    public void A_paused_server_shows_the_pause_even_with_a_note()
    {
        var config = ConfigWithCrucible(sharingEnabled: false, globallyEnabled: false, note: "In testing.");

        Assert.Equal(ServerOffCopy.Paused, CrucibleGate.ServerOffText(config));
    }

    // With no block, or with a blank note, the chip shows the feature's generic line.
    [Fact]
    public void No_block_or_a_blank_note_shows_the_generic_line()
    {
        var noBlock = ConfigWithCrucible() with { CrucibleRuns = null };
        var blankNote = ConfigWithCrucible(sharingEnabled: false, note: "  ");

        Assert.Equal(ServerOffCopy.Feature, CrucibleGate.ServerOffText(noBlock));
        Assert.Equal(ServerOffCopy.Feature, CrucibleGate.ServerOffText(blankNote));
    }

    // A note beside a switch that is on is ignored: there is nothing switched off to explain.
    [Fact]
    public void A_note_on_enabled_sharing_shows_nothing()
    {
        Assert.Null(CrucibleGate.ServerOffText(ConfigWithCrucible(note: "In testing.")));
    }

    [Fact]
    public void A_permitted_sharing_has_nothing_to_say()
    {
        Assert.Null(CrucibleGate.ServerOffText(ConfigWithCrucible()));
        Assert.Null(CrucibleGate.ServerOffText(null));
    }
}
