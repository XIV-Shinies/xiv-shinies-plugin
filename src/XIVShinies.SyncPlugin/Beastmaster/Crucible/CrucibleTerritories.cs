using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Where the Crucible run sharing may send from: the entrance, for the two windows it opens before the
/// duty starts, and the boards, for everything else.
/// </summary>
/// <remarks>
/// <para>
/// One of the few places this plugin names game ids itself. The client has to know it is standing on a
/// board before it may send anything about a run, and a territory counts as a board only once it is
/// known to be one: treating some other territory as a board would send enter, heartbeat and leave
/// uploads about a place that has nothing to do with the Crucible.
/// </para>
/// <para>
/// The boards are the five territories from <see cref="FirstBoard"/> to <see cref="LastBoard"/>, in
/// board order. The game data ties each row of <c>XBMContent</c>, one per board, through
/// <c>ContentFinderCondition</c> to its <c>TerritoryType</c>, and those are the five rows it names.
/// They are fixed here as a range rather than read from the sheets on each start, so a sheet whose
/// columns move in a patch cannot turn some other territory into a board. The server checks every
/// upload against the board's own layout, so a board added later needs the server to learn it too,
/// and reaches <see cref="IsBoard"/> in a plugin release.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class CrucibleTerritories
{
    /// <summary>The first board's <c>TerritoryType</c> row id.</summary>
    // `uint` is a whole number that can never be negative (an unsigned integer), the type of a
    // territory id; `const` fixes the value when the code compiles.
    public const uint FirstBoard = 1339;

    /// <summary>The fifth and last board's <c>TerritoryType</c> row id.</summary>
    public const uint LastBoard = 1343;

    /// <summary>
    /// The <c>TerritoryType</c> row id of the entrance, where the roster pick and the board seen
    /// before entering open.
    /// </summary>
    public const uint Entrance = 148;

    /// <summary>True when the territory is a Crucible board.</summary>
    /// <param name="territoryTypeId">The territory's <c>TerritoryType</c> row id.</param>
    // `=>` makes the expression after it the whole method body, like an arrow function.
    // `is >= A and <= B` is true when the value lies between the two, both ends included.
    public static bool IsBoard(uint territoryTypeId) => territoryTypeId is >= FirstBoard and <= LastBoard;

    /// <summary>
    /// True when a snapshot read in this territory may go up: the roster pick and the board seen
    /// before entering only at the entrance, and every other kind only inside a board.
    /// </summary>
    /// <remarks>
    /// The server fails a whole upload over one snapshot sent from the wrong side of the line between
    /// the entrance and the boards, so each is held to its own side, including on the first frames
    /// after a zone change, when a window from the other side can still be drawn.
    /// </remarks>
    /// <param name="observation">The snapshot.</param>
    /// <param name="territoryTypeId">The territory it was read in.</param>
    // `a ? b : c` picks b when a is true, else c, as in TypeScript.
    public static bool Admits(CrucibleObservation observation, uint territoryTypeId) =>
        IsEntranceWindow(observation) ? territoryTypeId == Entrance : IsBoard(territoryTypeId);

    /// <summary>True for the roster pick and the board seen before entering.</summary>
    /// <param name="observation">The snapshot.</param>
    // `private` keeps this helper inside the class, like a function a module does not export.
    // `is Type { Property: value }` matches a snapshot of that type with that value, like a
    // TypeScript type guard checking a field; `or` joins the two shapes. `(int)` turns the named
    // mode or view into the number the snapshot carries.
    private static bool IsEntranceWindow(CrucibleObservation observation) =>
        observation is CrucibleTeamObservation { Mode: (int)CrucibleTeamMode.RosterPick }
            or CrucibleBoardObservation { View: (int)CrucibleBoardView.PreEntry };
}
