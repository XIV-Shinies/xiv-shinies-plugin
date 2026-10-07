using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Where the Crucible sharing may send from: the entrance's two pre-duty windows only from the
// entrance, and everything else only from a board. Each `[Fact]` is one test, like `it(...)` in Jest;
// a `[Theory]` runs once per `[InlineData]` row, like `it.each`.
public class CrucibleTerritoriesTests
{
    // `uint` is a whole number that can never be negative (an unsigned integer), the type of a
    // territory id; `const` fixes the value when the code compiles.
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

    /// <summary>
    /// One snapshot of every in-duty kind: each board view and team mode but the entrance's, each
    /// offer source, the bag, the results and the character's own HP.
    /// </summary>
    // `[a, b, ...]` builds an array from the items listed, like an array literal in TypeScript.
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
    ];

    // The five boards, in board order. The `u` suffix makes a number a `uint`.
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

    // The entrance hosts the roster pick but is not a board, and a territory not known to be a
    // board is never treated as one, the ids either side of the boards included.
    [Theory]
    [InlineData(Entrance)]
    [InlineData(0u)]
    [InlineData(1338u)]
    [InlineData(1344u)]
    public void Any_other_territory_is_not_a_board(uint territory)
    {
        Assert.False(CrucibleTerritories.IsBoard(territory));
    }

    // Inside each board, every kind of snapshot but the entrance's two windows may go up. `foreach`
    // walks every item in turn, like `for (const observation of ...)` in TypeScript.
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

    // At the entrance, the two windows it opens before the duty may go up: the roster pick and the
    // board seen before entering.
    [Fact]
    public void At_the_entrance_the_roster_pick_and_the_pre_entry_board_are_admitted()
    {
        Assert.True(CrucibleTerritories.Admits(Team(CrucibleTeamMode.RosterPick), Entrance));
        Assert.True(CrucibleTerritories.Admits(Board(CrucibleBoardView.PreEntry), Entrance));
    }

    // The server refuses the entrance's two windows from anywhere but the entrance: from each board,
    // where the roster pick can still be redrawn just after zoning in, and from any other territory.
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

    // Outside a board, the entrance included, every in-duty snapshot is refused.
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
