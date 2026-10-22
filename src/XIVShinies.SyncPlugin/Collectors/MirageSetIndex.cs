using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Maps a glamour-dresser outfit's set item ID to its pieces, and reads a slot's unlock bits to say
/// which of those pieces are stored now.
/// </summary>
/// <remarks>
/// A dresser slot holding an outfit records its set item ID (a <c>MirageStoreSetItem</c> row), not
/// its pieces' IDs. The sheet says which pieces a set has, and a per-slot bit field says which are
/// stored. This reads no game state; the collector reads the sheet and the dresser cache.
/// </remarks>
public static class MirageSetIndex
{
    // A MirageStoreSetItem row has one column per equipment slot, and the outfit's unlock bits use one
    // bit per column in the same order, so a piece array is this wide and bit i names slot i.
    private const int SlotCount = 11;

    /// <summary>Builds a set item ID to piece IDs lookup, one array per outfit in column order.</summary>
    /// <param name="rows">
    /// Every outfit row, as (its set item ID, its <see cref="SlotCount"/> piece IDs). Empty slots are
    /// ID 0 and must keep their position: <see cref="StoredPieces"/> pairs bit i with index i.
    /// </param>
    public static IReadOnlyDictionary<uint, uint[]> Build(
        IEnumerable<(uint SetItemId, uint[] PieceItemIds)> rows)
    {
        var index = new Dictionary<uint, uint[]>();

        foreach (var (setItemId, pieceItemIds) in rows)
        {
            // Row 0 and rows with every slot empty are sheet padding. A row with some empty slots
            // keeps its zeros, because positions matter.
            if (setItemId == 0 || AllEmpty(pieceItemIds))
                continue;

            // A set listed twice keeps whichever of its rows comes first, and the duplicate does not throw.
            index.TryAdd(setItemId, pieceItemIds);
        }

        return index;
    }

    /// <summary>Yields the piece IDs a stored outfit currently holds, per its unlock bits.</summary>
    /// <param name="pieceItemIds">The outfit's pieces in column order (from <see cref="Build"/>).</param>
    /// <param name="unlockBits">
    /// One bit per slot, set once that slot's piece is withdrawn from the outfit and clear while it is
    /// stored, so a complete stored outfit reads 0.
    /// </param>
    public static IEnumerable<uint> StoredPieces(uint[] pieceItemIds, ushort unlockBits)
    {
        // Bounded by both, so a stray high bit cannot read past the pieces or invent a slot.
        var slots = pieceItemIds.Length < SlotCount ? pieceItemIds.Length : SlotCount;

        for (var slot = 0; slot < slots; slot++)
        {
            var withdrawn = (unlockBits & (1 << slot)) != 0;

            // An empty slot can read as stored. Armor outfits leave the weapon slots empty, so this
            // is common.
            if (!withdrawn && pieceItemIds[slot] != 0)
                yield return pieceItemIds[slot];
        }
    }

    private static bool AllEmpty(uint[] pieceItemIds)
    {
        foreach (var id in pieceItemIds)
        {
            if (id != 0)
                return false;
        }

        return true;
    }
}
