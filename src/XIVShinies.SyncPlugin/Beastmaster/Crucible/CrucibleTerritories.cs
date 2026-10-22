using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Where the Crucible run sharing may send from: the entrance, for the two windows it opens before the
/// duty starts, and the boards, for everything else.
/// </summary>
/// <remarks>
/// Treating any other territory as a board would send enter, heartbeat and leave uploads about a place
/// unrelated to the Crucible. The boards are the <c>TerritoryType</c> rows the game data ties to
/// <c>XBMContent</c>'s rows through <c>ContentFinderCondition</c>, fixed here as a range so a sheet whose
/// columns move in a patch cannot turn another territory into a board. A board added later needs a
/// plugin release and a server change, since the server checks every upload against the board's layout.
/// </remarks>
public static class CrucibleTerritories
{
    /// <summary>The first board's <c>TerritoryType</c> row id.</summary>
    public const uint FirstBoard = 1339;

    /// <summary>The fifth and last board's <c>TerritoryType</c> row id.</summary>
    public const uint LastBoard = 1343;

    /// <summary>
    /// The entrance's <c>TerritoryType</c> row id, where the roster pick and the board seen before
    /// entering open.
    /// </summary>
    public const uint Entrance = 148;

    /// <summary>True when the <c>TerritoryType</c> row id is a Crucible board.</summary>
    public static bool IsBoard(uint territoryTypeId) => territoryTypeId is >= FirstBoard and <= LastBoard;

    /// <summary>
    /// True when a snapshot read in this territory may go up: the roster pick and the board seen
    /// before entering only at the entrance, and every other kind only inside a board.
    /// </summary>
    /// <remarks>
    /// The server fails a whole upload over one snapshot from the wrong side of the entrance-board
    /// line, so each is held to its own side, even on the first frames after a zone change, when a
    /// window from the other side can still be drawn.
    /// </remarks>
    /// <param name="territoryTypeId">The territory the snapshot was read in.</param>
    public static bool Admits(CrucibleObservation observation, uint territoryTypeId) =>
        IsEntranceWindow(observation) ? territoryTypeId == Entrance : IsBoard(territoryTypeId);

    /// <summary>True for the roster pick and the board seen before entering.</summary>
    private static bool IsEntranceWindow(CrucibleObservation observation) =>
        observation is CrucibleTeamObservation { Mode: (int)CrucibleTeamMode.RosterPick }
            or CrucibleBoardObservation { View: (int)CrucibleBoardView.PreEntry };
}
