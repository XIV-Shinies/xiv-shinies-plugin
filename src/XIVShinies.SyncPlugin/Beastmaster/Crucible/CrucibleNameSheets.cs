using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
// The sheet column layout types, used to check a column holds text before reading it.
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads the rows <see cref="CrucibleNames"/> resolves the results screen's names against, from the
/// game's own data in the client's language.
/// </summary>
/// <remarks>
/// <para>
/// Read through <see cref="RawRow"/>, which asks for rows by number and columns by position. Generated
/// bindings pin a hash of the column layout they were built against, and requesting one whose layout
/// does not match the game's throws rather than returning rows; each sheet here needs one text column,
/// which <see cref="RawRow"/> reads without that check. A column that is missing or does not hold text
/// is refused and logged rather than decoded.
/// </para>
/// <para>
/// Each row's text keeps its formatting codes (see <see cref="RawText"/>) for
/// <see cref="CrucibleNames"/> to remove.
/// </para>
/// <para>
/// Each sheet is read on its own, and one that cannot be read leaves only its own names unresolved
/// (see <see cref="CrucibleNames"/>).
/// </para>
/// </remarks>
// `internal` makes it visible only inside this plugin. A `static class` holds only shared members and
// is never instantiated: a module of functions.
internal static class CrucibleNameSheets
{
    /// <summary>The sheet of interface strings, which holds the Degree names.</summary>
    // `const` fixes the value when the code compiles.
    private const string AddonSheet = "Addon";

    /// <summary>The sheet of the ranks a run can earn.</summary>
    private const string RankSheet = "XBMScoreRank";

    /// <summary>The sheet of the bonuses a run can earn.</summary>
    private const string BonusSheet = "XBMScoreBonus";

    /// <summary>The column holding each Degree name in <c>Addon</c>.</summary>
    private const int DegreeColumn = 0;

    /// <summary>The column holding each rank name in <c>XBMScoreRank</c>.</summary>
    private const int RankColumn = 0;

    /// <summary>
    /// The column holding each bonus name in <c>XBMScoreBonus</c>; column 0 is a flag and column 2 the
    /// description.
    /// </summary>
    private const int BonusColumn = 1;

    /// <summary>Reads the three sheets' names, logging any sheet that cannot be read.</summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="log">Where a sheet that cannot be read is reported.</param>
    // `=>` gives the method a single expression for its body, like an arrow function, and `new(...)`
    // builds the declared return type, here a CrucibleNames.
    public static CrucibleNames Load(IDataManager dataManager, IPluginLog log) =>
        new(
            ReadDegreeRows(dataManager, log),
            ReadWholeSheet(dataManager, log, RankSheet, RankColumn),
            ReadWholeSheet(dataManager, log, BonusSheet, BonusColumn));

    /// <summary>The Degree rows of <c>Addon</c>: only those, since the sheet holds every UI string.</summary>
    private static IReadOnlyList<CrucibleSheetText> ReadDegreeRows(IDataManager dataManager, IPluginLog log)
    {
        // `try` runs the block and `catch` takes over if anything in it throws, like try/catch in
        // TypeScript; `Exception ex` catches every kind and names it `ex`.
        try
        {
            // `null` asks for the client's own language. The `<RawRow>` in angle brackets names the
            // row type, like a TypeScript generic argument.
            // `var` lets the compiler infer the type, like an unannotated `let` in TypeScript.
            var sheet = dataManager.GetExcelSheet<RawRow>(null, AddonSheet);
            if (!HoldsText(sheet, DegreeColumn))
                return Unreadable(log, AddonSheet, null);

            // The number in parentheses is the room the list starts with, not items in it: the list
            // starts empty.
            var rows = new List<CrucibleSheetText>(CrucibleNames.Degrees);

            // `0u` is zero as a `uint`, the type of a row id.
            for (var degree = 0u; degree < CrucibleNames.Degrees; degree++)
            {
                var rowId = CrucibleNames.FirstDegreeRow + degree;
                // `TryGetRow` returns whether the row exists and, when it does, writes it into `row`.
                if (sheet.TryGetRow(rowId, out var row))
                    rows.Add(new CrucibleSheetText(rowId, RawText(row, DegreeColumn)));
            }

            return rows;
        }
        catch (Exception ex)
        {
            return Unreadable(log, AddonSheet, ex);
        }
    }

    /// <summary>Every row of a sheet, each with its text from one column.</summary>
    private static IReadOnlyList<CrucibleSheetText> ReadWholeSheet(
        IDataManager dataManager, IPluginLog log, string sheetName, int column)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<RawRow>(null, sheetName);
            if (!HoldsText(sheet, column))
                return Unreadable(log, sheetName, null);

            // `foreach` walks the sheet row by row, decoding each as it goes, which is why the walk
            // sits inside the `try` as well as the lookup: a row the game data cannot decode throws
            // partway through.
            var rows = new List<CrucibleSheetText>();
            foreach (var row in sheet)
                rows.Add(new CrucibleSheetText(row.RowId, RawText(row, column)));

            return rows;
        }
        catch (Exception ex)
        {
            return Unreadable(log, sheetName, ex);
        }
    }

    /// <summary>True when the sheet has the column and it holds text.</summary>
    // `ExcelSheet<RawRow>` is the sheet type the lookup returns; `Columns` describes its layout, one
    // entry per column.
    private static bool HoldsText(ExcelSheet<RawRow> sheet, int column) =>
        column < sheet.Columns.Count && sheet.Columns[column].Type == ExcelColumnDataType.String;

    /// <summary>
    /// A row's text in one column as the game stores it, formatting codes included, decoded from UTF-8
    /// as a window's text is.
    /// </summary>
    // `Data.Span` is a view of the string's stored bytes; `Encoding.UTF8.GetString` turns those bytes
    // into a C# string, like `new TextDecoder().decode(bytes)`.
    private static string RawText(RawRow row, int column) =>
        Encoding.UTF8.GetString(row.ReadStringColumn(column).Data.Span);

    /// <summary>Reports a sheet that could not be read and stands in no rows for it.</summary>
    /// <param name="log">Where the report goes.</param>
    /// <param name="sheetName">The sheet's name.</param>
    /// <param name="ex">What it threw, or null when its column is missing or does not hold text.</param>
    // `Exception?` is an exception that may be null, like `Error | null`.
    private static IReadOnlyList<CrucibleSheetText> Unreadable(
        IPluginLog log, string sheetName, Exception? ex)
    {
        // `$"..."` is an interpolated string, like a template literal.
        var message = $"Could not read the {sheetName} sheet; Crucible results send its names as null.";
        if (ex is null)
            log.Warning($"{message} Its name column is missing or does not hold text.");
        else
            log.Warning(ex, message);

        // `[]` is an empty list.
        return [];
    }
}
