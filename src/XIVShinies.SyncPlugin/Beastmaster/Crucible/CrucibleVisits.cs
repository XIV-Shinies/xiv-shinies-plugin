namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>What one tick's territory says about the character's visit to a board.</summary>
// An `enum` is a fixed set of named values, numbers underneath, like a TypeScript numeric `enum`.
public enum CrucibleVisitChange
{
    /// <summary>Nothing changed: still outside every board, or still in the same one.</summary>
    None,

    /// <summary>The character zoned into a board, from somewhere seen on an earlier tick.</summary>
    Entered,

    /// <summary>
    /// The character was already in a board on the first tick seen, so it was there before the plugin
    /// started, or before the sharing was switched on.
    /// </summary>
    AlreadyInside,

    /// <summary>The character zoned out of the board it was in.</summary>
    Left,
}

/// <summary>
/// Follows the character's visits to the Crucible's boards from the territory it stands in on each
/// tick.
/// </summary>
/// <remarks>
/// The difference between <see cref="CrucibleVisitChange.Entered"/> and
/// <see cref="CrucibleVisitChange.AlreadyInside"/> decides how a visit's first snapshots go up: as an
/// <c>enter</c> when the character was seen zoning in, and as a <c>change</c> otherwise (see
/// <see cref="CrucibleUploadScheduler.NotifyEntered"/>). Only the game's main thread calls it.
/// </remarks>
// A class (not a record) because it holds changing state; `sealed` means nothing can subclass it.
public sealed class CrucibleVisits
{
    // `uint?` is a territory id that may be null, like `number | null`. A field with no `= ...`
    // starts at its type's default, which is null here.

    /// <summary>The territory seen on the last tick, or null before the first one.</summary>
    private uint? lastTerritory;

    /// <summary>The board being visited, or null when the character is not in one.</summary>
    // `{ get; private set; }`: anyone may read it, only this class may change it.
    public uint? Board { get; private set; }

    /// <summary>Takes in one tick's territory and says what it changed.</summary>
    /// <param name="territoryTypeId">The territory the character stands in.</param>
    public CrucibleVisitChange Update(uint territoryTypeId)
    {
        var seenBefore = lastTerritory is not null;
        lastTerritory = territoryTypeId;
        var isBoard = CrucibleTerritories.IsBoard(territoryTypeId);

        // `is { } board` matches when there is a value and names it.
        if (Board is { } board)
        {
            if (!isBoard)
            {
                Board = null;
                return CrucibleVisitChange.Left;
            }

            if (territoryTypeId == board)
                return CrucibleVisitChange.None;

            // Straight from one board to another.
            Board = territoryTypeId;
            return CrucibleVisitChange.Entered;
        }

        if (!isBoard)
            return CrucibleVisitChange.None;

        Board = territoryTypeId;
        return seenBefore ? CrucibleVisitChange.Entered : CrucibleVisitChange.AlreadyInside;
    }

    /// <summary>Forgets the visit and the last territory, so the next tick is seen as the first.</summary>
    public void Reset()
    {
        lastTerritory = null;
        Board = null;
    }
}
