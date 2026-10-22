namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>What one tick's territory says about the character's visit to a board.</summary>
public enum CrucibleVisitChange
{
    /// <summary>Nothing changed: still outside every board, or still in the same one.</summary>
    None,

    /// <summary>The character zoned into a board, from somewhere seen on an earlier tick.</summary>
    Entered,

    /// <summary>
    /// The character was in a board on the first tick seen: there before the plugin started, or before
    /// the sharing was switched on.
    /// </summary>
    AlreadyInside,

    /// <summary>The character zoned out of the board it was in.</summary>
    Left,
}

/// <summary>Follows the character's visits to the Crucible's boards from each tick's territory.</summary>
/// <remarks>
/// <see cref="CrucibleVisitChange.Entered"/> sends a visit's first snapshots as an <c>enter</c>, and
/// <see cref="CrucibleVisitChange.AlreadyInside"/> as a <c>change</c> (see
/// <see cref="CrucibleUploadScheduler.NotifyEntered"/>). Only the game's main thread calls it.
/// </remarks>
public sealed class CrucibleVisits
{
    /// <summary>The territory seen on the last tick, or null before the first one.</summary>
    private uint? lastTerritory;

    /// <summary>The board being visited, or null when the character is not in one.</summary>
    public uint? Board { get; private set; }

    /// <summary>Takes in the territory the character stands in on one tick, and says what changed.</summary>
    public CrucibleVisitChange Update(uint territoryTypeId)
    {
        var seenBefore = lastTerritory is not null;
        lastTerritory = territoryTypeId;
        var isBoard = CrucibleTerritories.IsBoard(territoryTypeId);

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
