using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
// The sheet column layout types, used to check the link column holds a whole number before reading it.
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads each familiar's link to its <c>Pet</c> row from the game's <c>XBMPet</c> sheet, into a
/// <see cref="CrucibleFamiliarLinks"/>.
/// </summary>
/// <remarks>
/// Read through <see cref="RawRow"/>, by column position, for the reason
/// <see cref="BestiaryReader"/> gives: the generated binding for this sheet does not match the game's
/// layout and throws. The link is the sheet's first column, a whole number. A column of another type,
/// or one <see cref="CrucibleFamiliarLinks.From"/> refuses, leaves no links, so no familiar HP is sent;
/// the log says so once.
/// </remarks>
// `internal` makes it visible only inside this plugin. A `static class` holds only shared members and
// is never instantiated: a module of functions.
internal static class CrucibleFamiliarLinkSheet
{
    /// <summary>The bestiary sheet's name in the game data.</summary>
    private const string SheetName = "XBMPet";

    /// <summary>The column holding each familiar's <c>Pet</c> row.</summary>
    private const int PetRowColumn = 0;

    /// <summary>
    /// Reads the links, or returns null when the sheet cannot be read or its column is not the link,
    /// logging why.
    /// </summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="log">Where a sheet that cannot be read is reported.</param>
    public static CrucibleFamiliarLinks? Load(IDataManager dataManager, IPluginLog log)
    {
        // `try` runs the block and `catch` takes over if anything in it throws, like try/catch in
        // TypeScript. The walk below is inside it as well as the lookup: a row the game data cannot
        // decode throws partway through.
        try
        {
            // `null` asks for the client's own language; the link is the same in every language.
            var sheet = dataManager.GetExcelSheet<RawRow>(null, SheetName);
            if (PetRowColumn >= sheet.Columns.Count || !IsWholeNumber(sheet.Columns[PetRowColumn].Type))
                return Unreadable(log, null);

            // A list of (row id, link) pairs; `(uint PetId, long PetRow)` is a tuple type with named
            // parts, two values traveling together.
            var rows = new List<(uint PetId, long PetRow)>();

            foreach (var row in sheet)
            {
                // `ReadColumn` returns the value boxed as an `object` of the column's own type, and
                // `Convert.ToInt64` turns any whole-number type into one `long`, for From to check.
                // An unsigned 64-bit value too large for a `long` throws, which refuses the sheet.
                rows.Add((row.RowId, Convert.ToInt64(row.ReadColumn(PetRowColumn))));
            }

            // `??` uses the right-hand value when the left is null, as in TypeScript.
            return CrucibleFamiliarLinks.From(rows) ?? Unreadable(log, null);
        }
        catch (Exception ex)
        {
            // `Exception ex` catches every kind of failure and names it `ex`.
            return Unreadable(log, ex);
        }
    }

    /// <summary>True for the column types that hold a whole number.</summary>
    // `is A or B or ...` is true when the value equals any of them.
    private static bool IsWholeNumber(ExcelColumnDataType type) =>
        type is ExcelColumnDataType.Int8 or ExcelColumnDataType.UInt8
            or ExcelColumnDataType.Int16 or ExcelColumnDataType.UInt16
            or ExcelColumnDataType.Int32 or ExcelColumnDataType.UInt32
            or ExcelColumnDataType.Int64 or ExcelColumnDataType.UInt64;

    /// <summary>Reports a sheet whose links could not be read, and stands in no links for it.</summary>
    /// <param name="log">Where the report goes.</param>
    /// <param name="ex">What it threw, or null when its link column is not one.</param>
    // `Exception?` is an exception that may be null, like `Error | null`.
    private static CrucibleFamiliarLinks? Unreadable(IPluginLog log, Exception? ex)
    {
        // A `const` inside a method is a local value fixed when the code compiles.
        const string message = "Could not read the familiars' pet links from the XBMPet sheet; "
            + "Crucible run sharing sends no familiar HP.";
        if (ex is null)
            log.Warning($"{message} Its link column is missing, not a number, links no pet, or repeats one.");
        else
            log.Warning(ex, message);

        return null;
    }
}
