using System.Collections.Generic;
using Dalamud.Plugin.Services;

// "Cabinet" names both the armoire's memory struct and this sheet of what the armoire can hold.
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

// Every glamour-dresser outfit and the pieces inside it.
using MirageSetSheet = Lumina.Excel.Sheets.MirageStoreSetItem;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reads the two game sheets that describe storage: the glamour dresser's outfit sets and the
/// Armoire's rows. Shared by every collector that reads that storage.
/// </summary>
/// <remarks>
/// An unreadable sheet throws, uncaught, so each caller decides what that means for its category and
/// a caller that caches the result only ever caches a real one. Both sheets are fixed while the game
/// runs, so a caller may build from them once.
/// </remarks>
internal static class StorageSheets
{
    /// <summary>
    /// Every glamour-dresser outfit, as its set item id mapped to the pieces inside it in the
    /// sheet's column order.
    /// </summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <exception cref="System.Exception">The sheet could not be read; see the class remarks.</exception>
    public static IReadOnlyDictionary<uint, uint[]> MirageSets(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<MirageSetSheet>();

        var rows = new List<(uint SetItemId, uint[] PieceItemIds)>();
        foreach (var row in sheet)
        {
            // The sheet's column order is load-bearing: a stored outfit's unlock bit i refers to piece
            // i. An empty column reads id 0 and keeps its place; StoredPieces drops it, Build does not.
            var pieces = new uint[]
            {
                row.MainHand.RowId,
                row.OffHand.RowId,
                row.Head.RowId,
                row.Body.RowId,
                row.Hands.RowId,
                row.Legs.RowId,
                row.Feet.RowId,
                row.Earrings.RowId,
                row.Necklace.RowId,
                row.Bracelets.RowId,
                row.Ring.RowId,
            };
            rows.Add((row.RowId, pieces));
        }

        return MirageSetIndex.Build(rows);
    }

    /// <summary>
    /// Every Armoire row that stores an item, as (the row's own id, the item it stores).
    /// </summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <exception cref="System.Exception">The sheet could not be read; see the class remarks.</exception>
    /// <remarks>
    /// The game answers "is this in the Armoire?" by Cabinet row id, not item id, so a caller needs
    /// both. Rows with item id 0 are padding and are left out.
    /// </remarks>
    public static IReadOnlyList<(uint CabinetId, uint ItemId)> CabinetRows(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<CabinetSheet>();

        var rows = new List<(uint CabinetId, uint ItemId)>();
        foreach (var row in sheet)
        {
            var itemId = row.Item.RowId;
            if (itemId != 0)
                rows.Add((row.RowId, itemId));
        }

        return rows;
    }
}
