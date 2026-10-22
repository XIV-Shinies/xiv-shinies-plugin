using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Which reader each Crucible window's values go to, and which windows send a closing snapshot.
public class CrucibleWindowsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    // Read open. A null reading throws here, which fails the test.
    private static CrucibleObservation Snapshot(string windowName, string fixture) =>
        CrucibleWindows.Read(windowName, WindowFixture.Load(fixture), CrucibleNames.None)!(At, false);

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

    [Fact]
    public void A_reading_can_make_a_closing_snapshot()
    {
        var snapshot = CrucibleWindows.Read(
            CrucibleWindows.Team, WindowFixture.Load("petparty-mode2-lineup-picked.json"),
            CrucibleNames.None)!;

        Assert.True(Assert.IsType<CrucibleTeamObservation>(snapshot(At, true)).Closed);
    }

    // This screen drew "Standard", "Exemplary" and "Willing Sacrifice I".
    [Fact]
    public void The_results_screen_sends_the_ids_its_drawn_names_resolve_to()
    {
        var names = new CrucibleNames(
            [new(17870, "Standard")],
            [new(4, "Exemplary")],
            [new(0, "Willing Sacrifice I")]);

        var snapshot = CrucibleWindows.Read(
            CrucibleWindows.Results, WindowFixture.Load("result-board1-willing-sacrifice.json"), names)!;
        var results = Assert.IsType<CrucibleResultsObservation>(snapshot(At, false));

        Assert.Equal(0, results.Degree);
        Assert.Equal(4, results.RankIndex);
        Assert.Equal(0u, Assert.Single(results.Bonuses).BonusId);
    }

    // The rest of the screen goes up regardless.
    [Fact]
    public void The_results_screen_sends_null_for_names_that_do_not_resolve()
    {
        var snapshot = CrucibleWindows.Read(
            CrucibleWindows.Results, WindowFixture.Load("result-board1-willing-sacrifice.json"),
            CrucibleNames.None)!;
        var results = Assert.IsType<CrucibleResultsObservation>(snapshot(At, false));

        Assert.Null(results.Degree);
        Assert.Null(results.RankIndex);
        Assert.Null(Assert.Single(results.Bonuses).BonusId);
        Assert.Equal(250, results.Bonuses[0].Points);
    }

    [Fact]
    public void A_window_this_sharing_does_not_read_gives_nothing()
    {
        Assert.Null(
            CrucibleWindows.Read("XBMMonsterNotebook", WindowFixture.Load("hud.json"), CrucibleNames.None));
    }

    [Fact]
    public void Values_that_are_not_the_windows_layout_give_nothing()
    {
        Assert.Null(
            CrucibleWindows.Read(CrucibleWindows.Board, WindowFixture.Load("hud.json"), CrucibleNames.None));
    }

    // The run HUD stays open, and the results screen carries no closed flag.
    [Fact]
    public void The_board_team_and_offer_windows_send_a_closing_snapshot()
    {
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

    [Fact]
    public void Every_window_listened_to_has_a_reader()
    {
        foreach (var windowName in CrucibleWindows.Redrawn.Concat(CrucibleWindows.CompleteAtOpen))
            Assert.True(CrucibleWindows.IsRead(windowName));

        Assert.All(
            CrucibleWindows.Closing, windowName => Assert.Contains(windowName, CrucibleWindows.Redrawn));
    }
}
