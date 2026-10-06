using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Which chip the Crucible run sharing's card wears, and when the user counts as having seen it. Each
// `[Fact]` is one test, like `it(...)` in Jest; a `[Theory]` runs once per `[InlineData]` row, like
// `it.each`.
public class CrucibleBadgeTests
{
    // The server states the rules are judged against, named so each table row reads as a sentence.
    // `const string` fixes each name when the code compiles, so `[InlineData]` can use it.
    private const string Permits = "permits";
    private const string SharingOff = "sharing switched off";
    private const string NoEndpoint = "no crucibleRuns block";
    private const string Paused = "global pause";
    private const string Unanswered = "no /config yet";

    /// <summary>The <c>/config</c> a server in the named state sends, or null before one arrives.</summary>
    // `ConfigResponse?` may be null, like `ConfigResponse | null`; `=>` makes the expression after it
    // the whole method body. A `switch` expression picks a value by matching, like a lookup object or
    // a chain of ternaries; `_` is the case that matches anything else. `with { ... }` copies a record
    // with one property changed. `sharingEnabled: false` names the parameter the value fills, like
    // `{ sharingEnabled: false }` in TypeScript; parameters left out keep their defaults. `throw` can
    // stand where a value is expected, so a misspelled state fails loudly.
    private static ConfigResponse? Server(string state) => state switch
    {
        Permits => ConfigWithCrucible(),
        SharingOff => ConfigWithCrucible(sharingEnabled: false),
        NoEndpoint => ConfigWithCrucible() with { CrucibleRuns = null },
        Paused => ConfigWithCrucible(globallyEnabled: false),
        Unanswered => null,
        _ => throw new ArgumentException(state),
    };

    /// <summary>
    /// A config advertising the Crucible endpoint, with both switches on unless an argument turns
    /// one off.
    /// </summary>
    // `bool sharingEnabled = true` is an optional parameter with a default, like
    // `sharingEnabled = true` in a TypeScript signature. `new() { ... }` builds a ConfigResponse and
    // sets these properties on it, like an object literal. `[]` is an empty list.
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

    // With a server that permits the sharing, an unseen card is new, and a seen one stays new only
    // while its chip already went up this viewing.
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

    // In every other state the card is not new, even unseen and even with its chip already up: a
    // switched-off card is not offered, and before the server answers it may not exist at all.
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

    // "Off" outranks "New": a switched-off card says so on both surfaces, whatever has been seen.
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

    // Only the settings screen draws "New"; setup shows the card as part of its purpose.
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

    // Before the server answers the card wears nothing: neither "Off", which is not known yet, nor
    // "New", which could turn into "Off" a moment later.
    [Fact]
    public void A_card_wears_nothing_before_the_server_answers()
    {
        Assert.Equal(
            CategoryBadgeKind.None,
            CrucibleBadge.BadgeFor(
                seen: false, Server(Unanswered), showNewChip: true, badgedThisSession: false));
    }

    // --- When the card counts as seen --------------------------------------------------------------

    // A viewing counts only when the card was on screen while the server permitted the sharing.
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

    // A click counts whatever the server has said: the user has plainly seen the card, and "New"
    // must not appear later beside a box they ticked before the server answered.
    [Fact]
    public void Clicking_the_card_counts_as_seeing_it()
    {
        Assert.True(
            CrucibleBadge.ShowingRetires(seen: false, Server(Unanswered), onScreen: true, clicked: true));
    }

    // A card already recorded needs no second record, so nothing is saved again on later frames.
    [Fact]
    public void A_seen_card_is_never_recorded_again()
    {
        Assert.False(CrucibleBadge.ShowingRetires(
            seen: true, Server(Permits), onScreen: true, clicked: true));
    }

    // --- One viewing, frame by frame ---------------------------------------------------------------

    // The first frame shows the chip and records the card; the chip stays up for the rest of the
    // viewing on the session flag; once the window closes and the flag is cleared, it is gone.
    [Fact]
    public void The_chip_survives_its_own_record_and_retires_when_the_window_closes()
    {
        // `var` lets the compiler infer the type, like an unannotated `let`.
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
