using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads the rows <see cref="CrucibleNames"/> resolves the results screen's names against, from the
/// game's own data in the client's language.
/// </summary>
/// <remarks>
/// Read through <see cref="RawRow"/>, by row number and column position: generated bindings pin a hash
/// of their column layout and throw when the game's differs, and each sheet here needs only one text
/// column. A column that is missing or does not hold text is refused and logged rather than decoded.
/// Each sheet is read on its own, so one that cannot be read leaves only its own names unresolved, and
/// row text keeps its formatting codes for <see cref="CrucibleNames"/> to remove.
/// </remarks>
internal static class CrucibleNameSheets
{
    /// <summary>The sheet of interface strings, which holds the Degree names.</summary>
    private const string AddonSheet = "Addon";

    private const string RankSheet = "XBMScoreRank";
    private const string BonusSheet = "XBMScoreBonus";
    private const int DegreeColumn = 0;
    private const int RankColumn = 0;

    /// <summary>The name column; column 0 is a flag and column 2 the description.</summary>
    private const int BonusColumn = 1;

    /// <summary>Reads the three sheets' names, logging any sheet that cannot be read.</summary>
    public static CrucibleNames Load(IDataManager dataManager, IPluginLog log) =>
        new(
            ReadDegreeRows(dataManager, log),
            ReadWholeSheet(dataManager, log, RankSheet, RankColumn),
            ReadWholeSheet(dataManager, log, BonusSheet, BonusColumn));

    /// <summary>The Degree rows of <c>Addon</c>: only those, since the sheet holds every UI string.</summary>
    private static IReadOnlyList<CrucibleSheetText> ReadDegreeRows(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            // `null` asks for the client's own language.
            var sheet = dataManager.GetExcelSheet<RawRow>(null, AddonSheet);
            if (!HoldsText(sheet, DegreeColumn))
                return Unreadable(log, AddonSheet, null);

            var rows = new List<CrucibleSheetText>(CrucibleNames.Degrees);

            for (var degree = 0u; degree < CrucibleNames.Degrees; degree++)
            {
                var rowId = CrucibleNames.FirstDegreeRow + degree;
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

            // The walk decodes rows as it goes, so a row that cannot decode throws here, inside the `try`.
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
    private static bool HoldsText(ExcelSheet<RawRow> sheet, int column) =>
        column < sheet.Columns.Count && sheet.Columns[column].Type == ExcelColumnDataType.String;

    /// <summary>
    /// A row's text in one column as the game stores it, formatting codes included, decoded from UTF-8
    /// as a window's text is.
    /// </summary>
    private static string RawText(RawRow row, int column) =>
        Encoding.UTF8.GetString(row.ReadStringColumn(column).Data.Span);

    /// <summary>Reports a sheet that could not be read and stands in no rows for it.</summary>
    /// <param name="ex">What it threw, or null when its column is missing or does not hold text.</param>
    private static IReadOnlyList<CrucibleSheetText> Unreadable(
        IPluginLog log, string sheetName, Exception? ex)
    {
        var message = $"Could not read the {sheetName} sheet; Crucible results send its names as null.";
        if (ex is null)
            log.Warning($"{message} Its name column is missing or does not hold text.");
        else
            log.Warning(ex, message);

        return [];
    }
}
