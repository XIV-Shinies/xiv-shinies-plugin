using System.Collections.Generic;
using Dalamud.Plugin.Services;

// The game has two different things called "Cabinet": a memory struct (the Armoire itself) and an
// Excel sheet (the list of what the Armoire can hold). An alias keeps them apart.
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

// The sheet listing every glamour-dresser outfit and the pieces inside it. Aliased for a short,
// intent-revealing name at the one place it is read.
using MirageSetSheet = Lumina.Excel.Sheets.MirageStoreSetItem;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reads the two game sheets that describe storage: the glamour dresser's outfit sets and the
/// Armoire's rows. Shared by every collector that reads that storage.
/// </summary>
/// <remarks>
/// <para>
/// Each method turns a sheet into plain values. <see cref="MirageSets"/> hands its rows to the
/// pure, unit-tested <see cref="MirageSetIndex.Build"/> and returns the index it builds.
/// <see cref="CabinetRows"/> returns the rows themselves: one caller hands them to the pure,
/// unit-tested <see cref="ArmoireIndex.Build"/> to look items up by id, and another walks them
/// directly. The sheet read itself needs the game's data files, so it is verified in game.
/// </para>
/// <para>
/// <b>An unreadable sheet throws out of here.</b> Nothing is caught, so a caller decides what an
/// unreadable sheet means for its own category, and a caller that caches the result only ever
/// caches a real one. Both sheets are fixed for as long as the game runs, so a caller may build
/// from them once and reuse the result.
/// </para>
/// </remarks>
// `internal` makes the class visible only inside this assembly (the plugin and, through the test
// project's access grant, its tests), the way an un-exported module is private to a package.
// `static` means it is never instantiated and holds only static methods.
internal static class StorageSheets
{
    /// <summary>
    /// Every glamour-dresser outfit, as its set item id mapped to the pieces inside it in the
    /// sheet's column order.
    /// </summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <exception cref="System.Exception">The sheet could not be read; see the class remarks.</exception>
    // Not named MirageSetIndex after the class it builds: inside this class, a method of that name
    // would hide the class, and `MirageSetIndex.Build` below would stop compiling.
    public static IReadOnlyDictionary<uint, uint[]> MirageSets(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<MirageSetSheet>();

        var rows = new List<(uint SetItemId, uint[] PieceItemIds)>();
        foreach (var row in sheet)
        {
            // The set's 11 slot columns in sheet order. This order is load-bearing: a stored outfit's
            // unlock bit i refers to the piece at index i, so it must line up with MirageStoreSetItem's
            // columns (MainHand, OffHand, Head, Body, Hands, Legs, Feet, Earrings, Necklace, Bracelets,
            // Ring). Empty slots resolve to id 0 and stay in place — StoredPieces drops them, Build does
            // not. .RowId reads the referenced Item row's id (0 when the column links nothing).
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
    /// The game answers "is this in the Armoire?" by Cabinet row id rather than by item id, so a
    /// caller needs both halves of each row. Rows that store no item (item id 0) are padding in the
    /// sheet, not real entries, and are left out.
    /// </remarks>
    // `(uint CabinetId, uint ItemId)` is a named tuple: a small value that groups two fields and
    // labels them, the C# counterpart of a `{ cabinetId, itemId }` object in TypeScript.
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
