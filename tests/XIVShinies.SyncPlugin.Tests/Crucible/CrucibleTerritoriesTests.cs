using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Where the Crucible sharing may send from: the entrance's two pre-duty windows only from the
// entrance, and everything else only from a board.
public class CrucibleTerritoriesTests
{
    private const uint Entrance = 148;

    /// <summary>A moment for the snapshots, which these rules never look at.</summary>
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static CrucibleObservation Board(CrucibleBoardView view) =>
        CrucibleUploadBuilder.Board(new CrucibleBoardReading(view, [], null, null), ObservedAt, false);

    private static CrucibleObservation Team(CrucibleTeamMode mode) =>
        CrucibleUploadBuilder.Team(new CrucibleTeamReading(mode, null, []), ObservedAt, false);

    private static CrucibleObservation Results() =>
        CrucibleUploadBuilder.Results(
            new CrucibleResultsReading(
                "Standard", 1, 2, 3, new CrucibleScoreLine(4, 5), new CrucibleScoreLine(6, 7),
                new CrucibleScoreLine(8, 9), 10, 11, 12, "Exemplary", [], []),
            ObservedAt, degree: null, rankIndex: null, bonusId: _ => null);

    /// <summary>One snapshot of every kind but the entrance's two windows.</summary>
    private static CrucibleObservation[] InDuty() =>
    [
        Board(CrucibleBoardView.WholeBoard),
        Board(CrucibleBoardView.Scoped),
        Team(CrucibleTeamMode.Browse),
        Team(CrucibleTeamMode.Lineup),
        Team(CrucibleTeamMode.Feed),
        Team(CrucibleTeamMode.Campsite),
        Team(CrucibleTeamMode.BlessedHorn),
        CrucibleUploadBuilder.Bag(new CrucibleBagReading(100, [], []), ObservedAt),
        CrucibleUploadBuilder.Offer(
            new CrucibleOfferReading(CrucibleOfferSource.Treasure, 100, null, []), ObservedAt, false),
        CrucibleUploadBuilder.Offer(
            new CrucibleOfferReading(CrucibleOfferSource.Loot, 100, 5, []), ObservedAt, false),
        CrucibleUploadBuilder.Offer(
            new CrucibleOfferReading(CrucibleOfferSource.Shop, 100, null, []), ObservedAt, false),
        Results(),
        CrucibleUploadBuilder.Self(500, 1000, ObservedAt),
        CrucibleUploadBuilder.Familiars(
            true, [new CrucibleFieldedFamiliar(13, 358, 560, true, ObservedAt)], ObservedAt),
    ];

    // The five boards, in board order.
    [Theory]
    [InlineData(1339u)]
    [InlineData(1340u)]
    [InlineData(1341u)]
    [InlineData(1342u)]
    [InlineData(1343u)]
    public void Each_of_the_five_boards_is_a_board(uint territory)
    {
        Assert.True(CrucibleTerritories.IsBoard(territory));
    }

    // The entrance hosts the roster pick but is not a board.
    [Theory]
    [InlineData(Entrance)]
    [InlineData(0u)]
    [InlineData(1338u)]
    [InlineData(1344u)]
    public void Any_other_territory_is_not_a_board(uint territory)
    {
        Assert.False(CrucibleTerritories.IsBoard(territory));
    }

    [Theory]
    [InlineData(1339u)]
    [InlineData(1340u)]
    [InlineData(1341u)]
    [InlineData(1342u)]
    [InlineData(1343u)]
    public void Inside_a_board_every_in_duty_kind_is_admitted(uint territory)
    {
        foreach (var observation in InDuty())
            Assert.True(CrucibleTerritories.Admits(observation, territory));
    }

    [Fact]
    public void At_the_entrance_the_roster_pick_and_the_pre_entry_board_are_admitted()
    {
        Assert.True(CrucibleTerritories.Admits(Team(CrucibleTeamMode.RosterPick), Entrance));
        Assert.True(CrucibleTerritories.Admits(Board(CrucibleBoardView.PreEntry), Entrance));
    }

    // The server refuses them outside the entrance, and the roster pick can still be redrawn just
    // after zoning in to a board.
    [Theory]
    [InlineData(1339u)]
    [InlineData(1340u)]
    [InlineData(1341u)]
    [InlineData(1342u)]
    [InlineData(1343u)]
    [InlineData(0u)]
    [InlineData(129u)]
    public void The_entrance_windows_are_refused_anywhere_but_the_entrance(uint territory)
    {
        Assert.False(CrucibleTerritories.Admits(Team(CrucibleTeamMode.RosterPick), territory));
        Assert.False(CrucibleTerritories.Admits(Board(CrucibleBoardView.PreEntry), territory));
    }

    [Theory]
    [InlineData(Entrance)]
    [InlineData(0u)]
    [InlineData(129u)]
    public void Outside_a_board_every_in_duty_snapshot_is_refused(uint territory)
    {
        foreach (var observation in InDuty())
            Assert.False(CrucibleTerritories.Admits(observation, territory));
    }
}
