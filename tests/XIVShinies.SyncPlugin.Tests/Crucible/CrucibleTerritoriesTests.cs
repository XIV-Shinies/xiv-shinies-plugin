using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Where the Crucible sharing may send from: the boards themselves, plus the two windows the entrance
// opens before the duty starts. Each `[Fact]` is one test, like `it(...)` in Jest; a `[Theory]` runs
// once per `[InlineData]` row, like `it.each`.
public class CrucibleTerritoriesTests
{
    // `uint` is a whole number that can never be negative (an unsigned integer), the type of a
    // territory id; `const` fixes the value when the code compiles.
    private const uint FirstBoard = 1339;
    private const uint SecondBoard = 1340;
    private const uint Entrance = 148;

    /// <summary>A moment for the snapshots, which these rules never look at.</summary>
    // `new(...)` with no type name builds the field's declared type, here a DateTimeOffset.
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A board snapshot in this view.</summary>
    // `=>` makes the expression after it the whole method body, like an arrow function. `[]` is an
    // empty list.
    private static CrucibleObservation Board(CrucibleBoardView view) =>
        CrucibleUploadBuilder.Board(new CrucibleBoardReading(view, [], null, null), ObservedAt, false);

    /// <summary>A team snapshot in this mode.</summary>
    private static CrucibleObservation Team(CrucibleTeamMode mode) =>
        CrucibleUploadBuilder.Team(new CrucibleTeamReading(mode, null, []), ObservedAt, false);

    /// <summary>A results snapshot.</summary>
    // `_ => null` is a lambda that ignores its argument and resolves every bonus name to nothing.
    private static CrucibleObservation Results() =>
        CrucibleUploadBuilder.Results(
            new CrucibleResultsReading(
                "Standard", 1, 2, 3, new CrucibleScoreLine(4, 5), new CrucibleScoreLine(6, 7),
                new CrucibleScoreLine(8, 9), 10, 11, 12, "Exemplary", [], []),
            ObservedAt, degree: null, rankIndex: null, bonusId: _ => null);

    [Theory]
    [InlineData(FirstBoard)]
    [InlineData(SecondBoard)]
    public void A_known_board_is_a_board(uint territory)
    {
        Assert.True(CrucibleTerritories.IsBoard(territory));
    }

    // The entrance hosts the roster pick but is not a board, and a territory not known to be a
    // board is never treated as one. The `u` suffix makes a number a `uint`.
    [Theory]
    [InlineData(Entrance)]
    [InlineData(0u)]
    [InlineData(1341u)]
    public void Any_other_territory_is_not_a_board(uint territory)
    {
        Assert.False(CrucibleTerritories.IsBoard(territory));
    }

    // Inside a board, every kind of snapshot may go up.
    [Fact]
    public void Inside_a_board_every_kind_is_admitted()
    {
        CrucibleObservation[] observations =
        [
            Board(CrucibleBoardView.WholeBoard),
            Team(CrucibleTeamMode.Lineup),
            CrucibleUploadBuilder.Bag(new CrucibleBagReading(100, [], []), ObservedAt),
            CrucibleUploadBuilder.Offer(
                new CrucibleOfferReading(CrucibleOfferSource.Shop, 100, null, []), ObservedAt, false),
            Results(),
            CrucibleUploadBuilder.Self(500, 1000, ObservedAt),
        ];

        foreach (var observation in observations)
            Assert.True(CrucibleTerritories.Admits(observation, FirstBoard));
    }

    // Outside a board, only the two windows the entrance opens before the duty may go up: the
    // roster pick and the board seen before entering.
    [Fact]
    public void Outside_a_board_only_the_roster_pick_and_the_pre_entry_board_are_admitted()
    {
        Assert.True(CrucibleTerritories.Admits(Team(CrucibleTeamMode.RosterPick), Entrance));
        Assert.True(CrucibleTerritories.Admits(Board(CrucibleBoardView.PreEntry), Entrance));
    }

    [Fact]
    public void Outside_a_board_every_other_snapshot_is_refused()
    {
        CrucibleObservation[] observations =
        [
            Board(CrucibleBoardView.WholeBoard),
            Board(CrucibleBoardView.Scoped),
            Team(CrucibleTeamMode.Lineup),
            Team(CrucibleTeamMode.Browse),
            CrucibleUploadBuilder.Bag(new CrucibleBagReading(100, [], []), ObservedAt),
            CrucibleUploadBuilder.Offer(
                new CrucibleOfferReading(CrucibleOfferSource.Treasure, 100, null, []), ObservedAt, false),
            Results(),
            CrucibleUploadBuilder.Self(500, 1000, ObservedAt),
        ];

        foreach (var observation in observations)
            Assert.False(CrucibleTerritories.Admits(observation, Entrance));
    }
}
