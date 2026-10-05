using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Where the Crucible run sharing may send from: the boards, plus the two windows the entrance opens
/// before the duty starts.
/// </summary>
/// <remarks>
/// <para>
/// One of the few places this plugin names game ids itself. The client has to know it is standing on a
/// board before it may send anything about a run, and a territory counts as a board only once it is
/// known to be one: treating some other territory as a board would send enter, heartbeat and leave
/// uploads about a place that has nothing to do with the Crucible. A board added later joins this list
/// in a plugin release.
/// </para>
/// <para>
/// The roster pick and the board seen before entering open at the entrance, outside every board, so
/// <see cref="Admits"/> lets those two through wherever they are read.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class CrucibleTerritories
{
    /// <summary>The first board's <c>TerritoryType</c> row id.</summary>
    // `uint` is a whole number that can never be negative (an unsigned integer), the type of a
    // territory id; `const` fixes the value when the code compiles.
    public const uint FirstBoard = 1339;

    /// <summary>The second board's <c>TerritoryType</c> row id.</summary>
    public const uint SecondBoard = 1340;

    /// <summary>True when the territory is a Crucible board.</summary>
    /// <param name="territoryTypeId">The territory's <c>TerritoryType</c> row id.</param>
    // `=>` makes the expression after it the whole method body, like an arrow function. `is A or B`
    // is true when the value equals either one.
    public static bool IsBoard(uint territoryTypeId) => territoryTypeId is FirstBoard or SecondBoard;

    /// <summary>
    /// True when a snapshot read in this territory may go up: inside a board every kind may, and
    /// outside one only the roster pick and the board seen before entering.
    /// </summary>
    /// <param name="observation">The snapshot.</param>
    /// <param name="territoryTypeId">The territory it was read in.</param>
    // `is Type { Property: value }` matches a snapshot of that type with that value, like a
    // TypeScript type guard checking a field; `or` joins the two shapes.
    public static bool Admits(CrucibleObservation observation, uint territoryTypeId) =>
        IsBoard(territoryTypeId)
        || observation is CrucibleTeamObservation { Mode: (int)CrucibleTeamMode.RosterPick }
            or CrucibleBoardObservation { View: (int)CrucibleBoardView.PreEntry };
}
