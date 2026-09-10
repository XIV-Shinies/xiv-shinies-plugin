using System;
using Dalamud.Plugin.Services;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// Reads how many beasts the Master's Bestiary holds, from the game's own data.
/// </summary>
/// <remarks>
/// <para>
/// One number, and it is the one number that cannot come from the bestiary window. The window
/// reports its own total, but a filtered view reports the size of the filtered slice — so a
/// completeness claim resting on the window alone would let a subset agree with itself. This is the
/// outside opinion that check needs; see <see cref="TamedBeastLedger.IsComplete"/>.
/// </para>
/// <para>
/// Read through <see cref="RawRow"/>, which asks for rows without asking for columns. The generated
/// binding for this sheet pins a hash of the column layout it was built against, and the live
/// game's layout does not match it, so requesting the typed sheet throws rather than returning
/// rows. Counting rows needs no column at all, so nothing here can be thrown off by a layout that
/// moves.
/// </para>
/// </remarks>
public static class BestiaryReader
{
    /// <summary>The bestiary sheet's name in the game data.</summary>
    private const string BestiarySheetName = "XBMPet";

    /// <summary>
    /// How many beasts the bestiary holds, or null when the sheet cannot be read.
    /// </summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <remarks>
    /// Null rather than zero on failure, because the two mean opposite things: zero would claim the
    /// game has no bestiary, and a completeness check comparing against it would agree with an
    /// empty read. Null withholds the claim instead.
    /// </remarks>
    public static int? CountBeasts(IDataManager dataManager)
    {
        try
        {
            var bestiary = dataManager.GetExcelSheet<RawRow>(null, BestiarySheetName);
            if (bestiary is null)
                return null;

            // Row 0 is the sheets' blank padding rather than a beast, so it is not counted.
            // Counting the rows that carry a real bestiary number is what makes this comparable
            // with the numbers the window lists.
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
            // The walk is inside the guard as well as the lookup. A sheet the game data cannot
            // supply is reported by throwing, and so is a row it cannot decode partway through —
            // letting the second escape would surface a bestiary-sheet fault as a failure to read
            // the window, and leave the caller retrying it on every refresh.
            return null;
        }
    }
}
