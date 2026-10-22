using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>One row's text from a game sheet, in the client's language.</summary>
public sealed record CrucibleSheetText(uint RowId, string Text);

/// <summary>
/// Turns the text the results screen draws into the ids the contract sends: the board's Degree, the
/// run's rank, and each bonus.
/// </summary>
/// <remarks>
/// The screen draws names in the client's language and the contract takes ids, so each name is looked
/// up among its sheet's rows, read in the same language. A name matches only a row's exact text, both
/// stripped with <see cref="CrucibleText.StripCodes"/> so formatting codes cannot stop a match. A name
/// the game builds from a code as it draws is in no row. A name no row holds, or one two rows share,
/// resolves to null, which the contract takes: a near match would be a guess.
/// </remarks>
public sealed class CrucibleNames
{
    /// <summary>The <c>Addon</c> row holding the name of Degree 0.</summary>
    public const uint FirstDegreeRow = 17870;

    /// <summary>How many Degrees a board has: 0 to 3.</summary>
    public const int Degrees = 4;

    /// <summary>How many ranks a run can earn: rank indexes 0 to 8.</summary>
    public const int Ranks = 9;

    /// <summary>Names with no rows behind them, which resolve nothing.</summary>
    public static readonly CrucibleNames None = new([], [], []);

    // Each lookup maps a name to the id it resolves to, or to null when two rows share the name.
    private readonly Dictionary<string, int?> degrees;
    private readonly Dictionary<string, int?> ranks;
    private readonly Dictionary<string, uint?> bonuses;

    /// <summary>Builds the lookups from the rows read out of the three sheets.</summary>
    /// <param name="degreeRows">
    /// <c>Addon</c> rows; only the <see cref="Degrees"/> rows from <see cref="FirstDegreeRow"/> count.
    /// </param>
    /// <param name="rankRows">
    /// <c>XBMScoreRank</c> rows, highest rank first, so a row's rank index is 8 minus the row. Only
    /// rows 0 to 8 count.
    /// </param>
    /// <param name="bonusRows">
    /// <c>XBMScoreBonus</c> rows. The id is the row, and each bonus tier is a row of its own.
    /// </param>
    public CrucibleNames(
        IReadOnlyList<CrucibleSheetText> degreeRows,
        IReadOnlyList<CrucibleSheetText> rankRows,
        IReadOnlyList<CrucibleSheetText> bonusRows)
    {
        // A row outside its sheet's range answers null and is left out, not made a Degree or rank.
        // Each subtraction runs only after its range check, so a `uint` never wraps.
        degrees = Index<int>(degreeRows, row =>
            row.RowId >= FirstDegreeRow && row.RowId < FirstDegreeRow + Degrees
                ? (int)(row.RowId - FirstDegreeRow)
                : null);
        ranks = Index<int>(rankRows, row => row.RowId < Ranks ? Ranks - 1 - (int)row.RowId : null);
        bonuses = Index<uint>(bonusRows, row => row.RowId);
    }

    /// <summary>The Degree a drawn mode name stands for, or null when no Degree row holds it.</summary>
    /// <param name="drawn">The name as drawn, formatting codes removed.</param>
    public int? Degree(string drawn) => Find(degrees, drawn);

    /// <summary>
    /// The rank index (0 for the lowest rank, 8 for the highest) a drawn rank name stands for, or null
    /// when no rank row holds it.
    /// </summary>
    /// <param name="drawn">The name as drawn, formatting codes removed.</param>
    public int? RankIndex(string drawn) => Find(ranks, drawn);

    /// <summary>The <c>XBMScoreBonus</c> row a drawn bonus name is, or null when no row holds it.</summary>
    /// <param name="drawn">The name as drawn, formatting codes removed.</param>
    public uint? BonusId(string drawn) => Find(bonuses, drawn);

    /// <summary>
    /// Maps each row's text to the id <paramref name="idOf"/> gives it, skipping rows it gives none
    /// and rows with no text, and mapping a text two rows share to null.
    /// </summary>
    private static Dictionary<string, T?> Index<T>(
        IReadOnlyList<CrucibleSheetText> rows, Func<CrucibleSheetText, T?> idOf)
        where T : struct
    {
        var index = new Dictionary<string, T?>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var text = CrucibleText.StripCodes(row.Text);
            if (text.Length == 0 || idOf(row) is not { } id)
                continue;

            // A second row with the same text makes the text ambiguous for good.
            if (!index.TryAdd(text, id))
                index[text] = null;
        }

        return index;
    }

    /// <summary>The id a drawn name resolves to, or null when it resolves to none.</summary>
    private static T? Find<T>(Dictionary<string, T?> index, string drawn)
        where T : struct =>
        index.TryGetValue(drawn, out var id) ? id : null;
}
