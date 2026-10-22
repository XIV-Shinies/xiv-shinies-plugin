using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Which chip the Crucible run sharing's card wears, and when the user counts as having seen it.
public class CrucibleBadgeTests
{
    // Server states, named so each table row reads as a sentence.
    private const string Permits = "permits";
    private const string SharingOff = "sharing switched off";
    private const string NoEndpoint = "no crucibleRuns block";
    private const string Paused = "global pause";
    private const string Unanswered = "no /config yet";

    /// <summary>The <c>/config</c> a server in the named state sends, or null before one arrives.</summary>
    private static ConfigResponse? Server(string state) => state switch
    {
        Permits => ConfigWithCrucible(),
        SharingOff => ConfigWithCrucible(sharingEnabled: false),
        NoEndpoint => ConfigWithCrucible() with { CrucibleRuns = null },
        Paused => ConfigWithCrucible(globallyEnabled: false),
        Unanswered => null,
        _ => throw new ArgumentException(state),
    };

    private static ConfigResponse ConfigWithCrucible(
        bool sharingEnabled = true, bool globallyEnabled = true) =>
        new()
        {
            Categories = new Dictionary<string, bool>(),
            Enabled = globallyEnabled,
            Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
            ItemManifest = [],
            ManifestVersion = "x",
            CrucibleRuns = new CrucibleRunsConfig { Enabled = sharingEnabled },
        };

    // --- Whether the card counts as new ----------------------------------------------------------

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void A_card_the_server_permits_is_new_while_unseen_or_already_badged(
        bool seen, bool badged, bool expected)
    {
        Assert.Equal(expected, CrucibleBadge.IsNew(seen: seen, Server(Permits), badgedThisSession: badged));
    }

    // A switched-off card is not offered, and before the server answers it may not exist at all.
    [Theory]
    [InlineData(SharingOff)]
    [InlineData(NoEndpoint)]
    [InlineData(Paused)]
    [InlineData(Unanswered)]
    public void A_card_the_server_does_not_permit_is_never_new(string server)
    {
        Assert.False(CrucibleBadge.IsNew(seen: false, Server(server), badgedThisSession: false));
        Assert.False(CrucibleBadge.IsNew(seen: true, Server(server), badgedThisSession: true));
    }

    // --- Which chip the card wears -----------------------------------------------------------------

    // "Off" outranks "New", whatever has been seen.
    [Theory]
    [InlineData(SharingOff)]
    [InlineData(NoEndpoint)]
    [InlineData(Paused)]
    public void A_switched_off_card_wears_off_on_both_surfaces(string server)
    {
        Assert.Equal(
            CategoryBadgeKind.Off,
            CrucibleBadge.BadgeFor(seen: false, Server(server), showNewChip: true, badgedThisSession: false));
        Assert.Equal(
            CategoryBadgeKind.Off,
            CrucibleBadge.BadgeFor(
                seen: false, Server(server), showNewChip: false, badgedThisSession: false));
    }

    // Setup shows the card as part of its purpose.
    [Fact]
    public void Only_the_settings_screen_draws_new()
    {
        Assert.Equal(
            CategoryBadgeKind.New,
            CrucibleBadge.BadgeFor(
                seen: false, Server(Permits), showNewChip: true, badgedThisSession: false));
        Assert.Equal(
            CategoryBadgeKind.None,
            CrucibleBadge.BadgeFor(
                seen: false, Server(Permits), showNewChip: false, badgedThisSession: false));
    }

    // Not "Off", which is not known yet, nor "New", which could turn into "Off" a moment later.
    [Fact]
    public void A_card_wears_nothing_before_the_server_answers()
    {
        Assert.Equal(
            CategoryBadgeKind.None,
            CrucibleBadge.BadgeFor(
                seen: false, Server(Unanswered), showNewChip: true, badgedThisSession: false));
    }

    // --- When the card counts as seen --------------------------------------------------------------

    [Theory]
    [InlineData(Permits, true, true)]
    [InlineData(Permits, false, false)]
    [InlineData(SharingOff, true, false)]
    [InlineData(NoEndpoint, true, false)]
    [InlineData(Paused, true, false)]
    [InlineData(Unanswered, true, false)]
    public void Showing_the_card_counts_only_on_screen_while_the_server_permits_it(
        string server, bool onScreen, bool expected)
    {
        Assert.Equal(
            expected,
            CrucibleBadge.ShowingRetires(seen: false, Server(server), onScreen: onScreen, clicked: false));
    }

    // A click counts whatever the server has said: "New" must not appear later beside a box the user
    // ticked before the server answered.
    [Fact]
    public void Clicking_the_card_counts_as_seeing_it()
    {
        Assert.True(
            CrucibleBadge.ShowingRetires(seen: false, Server(Unanswered), onScreen: true, clicked: true));
    }

    // Later frames must save nothing.
    [Fact]
    public void A_seen_card_is_never_recorded_again()
    {
        Assert.False(CrucibleBadge.ShowingRetires(
            seen: true, Server(Permits), onScreen: true, clicked: true));
    }

    // --- One viewing, frame by frame ---------------------------------------------------------------

    [Fact]
    public void The_chip_survives_its_own_record_and_retires_when_the_window_closes()
    {
        var server = Server(Permits);

        // Frame 1: unseen, so the chip shows, and drawing the card on screen records it.
        Assert.Equal(
            CategoryBadgeKind.New,
            CrucibleBadge.BadgeFor(seen: false, server, showNewChip: true, badgedThisSession: false));
        Assert.True(CrucibleBadge.ShowingRetires(seen: false, server, onScreen: true, clicked: false));

        // Frame 2: recorded, but the window set its session flag on frame 1.
        Assert.Equal(
            CategoryBadgeKind.New,
            CrucibleBadge.BadgeFor(seen: true, server, showNewChip: true, badgedThisSession: true));

        // Reopened: the flag was cleared on close.
        Assert.Equal(
            CategoryBadgeKind.None,
            CrucibleBadge.BadgeFor(seen: true, server, showNewChip: true, badgedThisSession: false));
    }
}
