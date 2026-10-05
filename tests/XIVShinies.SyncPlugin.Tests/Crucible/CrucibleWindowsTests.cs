using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Which reader each Crucible window's values go to, and which windows send a closing snapshot. Each
// `[Fact]` is one test, like `it(...)` in Jest; a `[Theory]` runs once per `[InlineData]` row, like
// `it.each`.
public class CrucibleWindowsTests
{
    // `new(...)` with no type name builds the field's declared type, here a DateTimeOffset.
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The snapshot a window's sample values make, read open at <see cref="At"/>.</summary>
    // `=>` makes the expression after it the whole method body, like an arrow function. `!` tells the
    // compiler the value is not null here; if it were, calling it would throw and fail the test.
    private static CrucibleObservation Snapshot(string windowName, string fixture) =>
        CrucibleWindows.Read(windowName, WindowFixture.Load(fixture))!(At, false);

    // Each window's values reach the reader written for that window. `Assert.IsType<T>` fails unless
    // the value is exactly that type, and hands it back typed.
    [Fact]
    public void Each_window_goes_to_its_own_reader()
    {
        Assert.IsType<CrucibleBoardObservation>(
            Snapshot(CrucibleWindows.Board, "stage-view1-midrun-progress-board1.json"));
        Assert.IsType<CrucibleTeamObservation>(
            Snapshot(CrucibleWindows.Team, "petparty-mode2-lineup-picked.json"));
        Assert.IsType<CrucibleBagObservation>(Snapshot(CrucibleWindows.Hud, "hud.json"));
        Assert.IsType<CrucibleResultsObservation>(
            Snapshot(CrucibleWindows.Results, "result-board2.json"));
    }

    // The three offer windows share one kind, so the source is what tells them apart.
    [Theory]
    [InlineData(CrucibleWindows.Treasure, "treasure.json", CrucibleOfferSource.Treasure)]
    [InlineData(CrucibleWindows.Loot, "booty.json", CrucibleOfferSource.Loot)]
    [InlineData(CrucibleWindows.Shop, "shop.json", CrucibleOfferSource.Shop)]
    public void Each_offer_window_names_its_own_source(
        string windowName, string fixture, CrucibleOfferSource source)
    {
        Assert.Equal(source, Assert.IsType<CrucibleOfferObservation>(Snapshot(windowName, fixture)).Source);
    }

    // The same reading makes a closing snapshot too.
    [Fact]
    public void A_reading_can_make_a_closing_snapshot()
    {
        var snapshot = CrucibleWindows.Read(
            CrucibleWindows.Team, WindowFixture.Load("petparty-mode2-lineup-picked.json"))!;

        Assert.True(Assert.IsType<CrucibleTeamObservation>(snapshot(At, true)).Closed);
    }

    [Fact]
    public void A_window_this_sharing_does_not_read_gives_nothing()
    {
        Assert.Null(CrucibleWindows.Read("XBMMonsterNotebook", WindowFixture.Load("hud.json")));
    }

    [Fact]
    public void Values_that_are_not_the_windows_layout_give_nothing()
    {
        Assert.Null(CrucibleWindows.Read(CrucibleWindows.Board, WindowFixture.Load("hud.json")));
    }

    // The board, team and offer windows close with a last snapshot marked closed; the run HUD stays
    // open, and the results screen carries no closed flag.
    [Fact]
    public void The_board_team_and_offer_windows_send_a_closing_snapshot()
    {
        // `new[] { ... }` builds an array from the items listed; `.Order()` sorts a copy, so the two
        // lists compare without depending on their order.
        Assert.Equal(
            new[]
            {
                CrucibleWindows.Board, CrucibleWindows.Team, CrucibleWindows.Treasure,
                CrucibleWindows.Loot, CrucibleWindows.Shop,
            }.Order(),
            CrucibleWindows.Closing.Order());

        Assert.False(CrucibleWindows.Closes(CrucibleWindows.Hud));
        Assert.False(CrucibleWindows.Closes(CrucibleWindows.Results));
    }

    // Every window read has a reader, and every closing window is one of them.
    [Fact]
    public void Every_window_listened_to_has_a_reader()
    {
        // `Concat` walks one list and then the other, like `[...a, ...b]`.
        foreach (var windowName in CrucibleWindows.Redrawn.Concat(CrucibleWindows.CompleteAtOpen))
            Assert.True(CrucibleWindows.IsRead(windowName));

        // `Assert.All` runs the check on every item; `windowName => ...` is a lambda, like an arrow
        // function.
        Assert.All(
            CrucibleWindows.Closing, windowName => Assert.Contains(windowName, CrucibleWindows.Redrawn));
    }
}
