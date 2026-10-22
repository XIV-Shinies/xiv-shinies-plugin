using System;
using Dalamud.Plugin.Services;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>Reads how many beasts the Master's Bestiary holds, from the game's data.</summary>
/// <remarks>
/// The completeness test checks the window's own total against this figure, so a short total, from a
/// list still loading or an unrecognized view, cannot pass a slice off as the whole (see
/// <see cref="TamedBeastLedger.IsComplete"/>). It reads through <see cref="RawRow"/>, rows without
/// columns, because the typed sheet's binding pins a column-layout hash the live game does not match,
/// and throws. Counting rows needs no column, so a moved layout cannot throw it off.
/// </remarks>
public static class BestiaryReader
{
    private const string BestiarySheetName = "XBMPet";

    /// <summary>
    /// How many beasts the bestiary holds, or null when the sheet cannot be read.
    /// </summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <remarks>
    /// Null rather than zero on failure: zero would claim the game has no bestiary, and a completeness
    /// check comparing against it would agree with an empty read.
    /// </remarks>
    public static int? CountBeasts(IDataManager dataManager)
    {
        try
        {
            var bestiary = dataManager.GetExcelSheet<RawRow>(null, BestiarySheetName);
            if (bestiary is null)
                return null;

            // Row 0 is blank padding, not a beast. Counting only rows with a real bestiary number
            // keeps this comparable with the numbers the window lists.
            var beasts = 0;
            foreach (var row in bestiary)
            {
                if (row.RowId > 0)
                    beasts++;
            }

            return beasts > 0 ? beasts : null;
        }
        catch (Exception)
        {
            // The walk is guarded too: a row that cannot decode throws partway through, and letting
            // it escape would report a sheet fault as a window read failure, retried every refresh.
            return null;
        }
    }
}
