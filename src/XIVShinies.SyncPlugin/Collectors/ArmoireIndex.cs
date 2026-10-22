using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>Maps an item ID to the armoire ("Cabinet") row that holds it.</summary>
/// <remarks>
/// The game answers "is this in the armoire?" by Cabinet row ID, not item ID. This reads no game
/// state; the collector reads the sheet that supplies the pairs.
/// </remarks>
public static class ArmoireIndex
{
    /// <summary>Builds an item ID to Cabinet row ID lookup.</summary>
    /// <param name="rows">Every armoire row, as (its own ID, the item it stores).</param>
    public static IReadOnlyDictionary<uint, uint> Build(IEnumerable<(uint CabinetId, uint ItemId)> rows)
    {
        var index = new Dictionary<uint, uint>();

        foreach (var (cabinetId, itemId) in rows)
        {
            // Row 0 and rows with no item are sheet padding.
            if (itemId == 0)
                continue;

            // An item listed twice keeps whichever of its rows comes first, and the duplicate does not throw.
            index.TryAdd(itemId, cabinetId);
        }

        return index;
    }
}
