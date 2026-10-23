using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads each familiar's link to its <c>Pet</c> row from the game's <c>XBMPet</c> sheet, into a
/// <see cref="CrucibleFamiliarLinks"/>.
/// </summary>
/// <remarks>
/// Read through <see cref="RawRow"/>, by column position, because the sheet's generated binding pins a
/// column-layout hash the live game does not match, and throws. The link is the sheet's first column, a
/// whole number. A column of another type, or links <see cref="CrucibleFamiliarLinks.From"/> refuses,
/// leaves no links, so no familiar HP is sent; the log says so once.
/// </remarks>
internal static class CrucibleFamiliarLinkSheet
{
    private const string SheetName = "XBMPet";

    /// <summary>The column holding each familiar's <c>Pet</c> row.</summary>
    private const int PetRowColumn = 0;

    /// <summary>
    /// Reads the links, or returns null, logging why, when the sheet cannot be read or its column is not
    /// the link.
    /// </summary>
    public static CrucibleFamiliarLinks? Load(IDataManager dataManager, IPluginLog log)
    {
        // The walk is inside the `try` too: a row the game data cannot decode throws partway through.
        try
        {
            // `null` asks for the client's own language; the link is the same in every language.
            var sheet = dataManager.GetExcelSheet<RawRow>(null, SheetName);
            if (PetRowColumn >= sheet.Columns.Count || !IsWholeNumber(sheet.Columns[PetRowColumn].Type))
                return Unreadable(log, null);

            var rows = new List<(uint PetId, long PetRow)>();

            foreach (var row in sheet)
            {
                // Any whole-number column type widens to a `long` for From to check. An unsigned 64-bit
                // value too large for a `long` throws, which refuses the sheet.
                rows.Add((row.RowId, Convert.ToInt64(row.ReadColumn(PetRowColumn))));
            }

            return CrucibleFamiliarLinks.From(rows) ?? Unreadable(log, null);
        }
        catch (Exception ex)
        {
            return Unreadable(log, ex);
        }
    }

    /// <summary>True for the column types that hold a whole number.</summary>
    private static bool IsWholeNumber(ExcelColumnDataType type) =>
        type is ExcelColumnDataType.Int8 or ExcelColumnDataType.UInt8
            or ExcelColumnDataType.Int16 or ExcelColumnDataType.UInt16
            or ExcelColumnDataType.Int32 or ExcelColumnDataType.UInt32
            or ExcelColumnDataType.Int64 or ExcelColumnDataType.UInt64;

    /// <summary>Logs a sheet whose links could not be read, and returns no links for it.</summary>
    /// <param name="ex">What it threw, or null when its link column is not one.</param>
    private static CrucibleFamiliarLinks? Unreadable(IPluginLog log, Exception? ex)
    {
        const string message = "Could not read the familiars' pet links from the XBMPet sheet; "
            + "Crucible run sharing sends no familiar HP.";
        if (ex is null)
            log.Warning($"{message} Its link column is missing, not a number, links no pet, or repeats one.");
        else
            log.Warning(ex, message);

        return null;
    }
}
