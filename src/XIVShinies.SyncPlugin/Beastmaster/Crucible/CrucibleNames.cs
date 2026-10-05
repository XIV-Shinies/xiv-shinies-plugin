using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>One row's text from a game sheet: the row id and its text in the client's language.</summary>
/// <param name="RowId">The row's id in its sheet.</param>
/// <param name="Text">The row's text.</param>
// A `record` is a class whose equality compares its values, like comparing two plain objects field
// by field; this one-line form declares its properties and constructor together. `uint` is a whole
// number that can never be negative, the type of a row id.
public sealed record CrucibleSheetText(uint RowId, string Text);

/// <summary>
/// Turns the text the results screen draws into the ids the contract sends: the board's Degree, the
/// run's rank, and each bonus.
/// </summary>
/// <remarks>
/// <para>
/// The screen draws these as names in the client's language, and the contract takes ids, never the
/// text. Each name is looked up among the rows of the game sheet that holds it, read in the same
/// language, so the lookup works in any client language:
/// </para>
/// <list type="bullet">
/// <item>The Degree names are the <c>Addon</c> rows from <see cref="FirstDegreeRow"/>, one per
/// Degree from 0 ("Standard" in English).</item>
/// <item>The rank names are <c>XBMScoreRank</c>'s nine rows, highest first, so a row's rank index,
/// counted from the lowest, is 8 minus the row.</item>
/// <item>The bonus names are <c>XBMScoreBonus</c>'s rows, and the id is the row. A bonus's tier is
/// part of its name, so each tier is a row of its own.</item>
/// </list>
/// <para>
/// A name matches only text a row holds exactly. Each row is stripped with
/// <see cref="CrucibleText.StripCodes"/>, the function the results reader strips the drawn text with,
/// so formatting codes in either one do not stop a match. A name the game builds from a code as it
/// draws (a conditional, or text from another sheet) is not in the row once codes are removed, so it
/// resolves to null. A name no row holds, or one two rows share, resolves to null: the contract
/// takes null for a name that does not resolve, and a near match would be a guess.
/// </para>
/// </remarks>
// `sealed` means nothing can subclass it. Every lookup is built once, in the constructor, and only
// read after that.
public sealed class CrucibleNames
{
    /// <summary>The <c>Addon</c> row holding the name of Degree 0.</summary>
    // `const` fixes the value when the code compiles.
    public const uint FirstDegreeRow = 17870;

    /// <summary>How many Degrees a board has: 0 to 3.</summary>
    public const int Degrees = 4;

    /// <summary>How many ranks a run can earn: rank indexes 0 to 8.</summary>
    public const int Ranks = 9;

    /// <summary>Names with no rows behind them, which resolve nothing.</summary>
    // `static readonly` builds the value once, before the class is first used, and never reassigns
    // it, like a module-level `const`; `new(...)` builds the declared type, here a CrucibleNames, and
    // `[]` is an empty list.
    public static readonly CrucibleNames None = new([], [], []);

    // Each lookup maps a name to the id it resolves to, or to null when two rows share the name.
    // `Dictionary<string, int?>` is a map from text to a number that may be null, like
    // `Map<string, number | null>`.
    private readonly Dictionary<string, int?> degrees;
    private readonly Dictionary<string, int?> ranks;
    private readonly Dictionary<string, uint?> bonuses;

    /// <summary>Builds the lookups from the rows read out of the three sheets.</summary>
    /// <param name="degreeRows">
    /// <c>Addon</c> rows; only the <see cref="Degrees"/> rows from <see cref="FirstDegreeRow"/> count.
    /// </param>
    /// <param name="rankRows"><c>XBMScoreRank</c> rows; only rows 0 to 8 count.</param>
    /// <param name="bonusRows"><c>XBMScoreBonus</c> rows.</param>
    // `IReadOnlyList<T>` is a list its holder cannot change, like `readonly T[]`.
    public CrucibleNames(
        IReadOnlyList<CrucibleSheetText> degreeRows,
        IReadOnlyList<CrucibleSheetText> rankRows,
        IReadOnlyList<CrucibleSheetText> bonusRows)
    {
        // `row => ...` is a lambda, like an arrow function. `Index<int>` sets the id type to `int`, so
        // the lambda returns an `int?`: the id, or null for a row that is not one of these names. A
        // row outside the range a sheet uses for these names answers null and is left out, rather
        // than turning into a Degree or rank that does not exist. `(int)` converts a `uint` to an
        // `int`; each subtraction runs only once the range check has passed, so it never goes below
        // zero, where a `uint` would wrap around to a huge number.
        degrees = Index<int>(degreeRows, row =>
            row.RowId >= FirstDegreeRow && row.RowId < FirstDegreeRow + Degrees
                ? (int)(row.RowId - FirstDegreeRow)
                : null);
        ranks = Index<int>(rankRows, row => row.RowId < Ranks ? Ranks - 1 - (int)row.RowId : null);
        bonuses = Index<uint>(bonusRows, row => row.RowId);
    }

    /// <summary>The Degree a drawn mode name stands for, or null when no Degree row holds it.</summary>
    /// <param name="drawn">The name as drawn, formatting codes removed.</param>
    // `=>` gives the method a single expression for its body, like an arrow function without braces.
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
    // `<T>` makes this generic, like a TypeScript generic function; `where T : struct` limits it to
    // value types such as `int` and `uint`, which is what lets `T?` mean "a T or null".
    // `Func<CrucibleSheetText, T?>` is a function value from a row to a `T?`.
    private static Dictionary<string, T?> Index<T>(
        IReadOnlyList<CrucibleSheetText> rows, Func<CrucibleSheetText, T?> idOf)
        where T : struct
    {
        // `StringComparer.Ordinal` compares text character by character, with no language rules.
        // `var` lets the compiler infer the type, like an unannotated `let` in TypeScript.
        var index = new Dictionary<string, T?>(StringComparer.Ordinal);

        // `foreach` walks every item in turn, like `for (const row of rows)`.
        foreach (var row in rows)
        {
            var text = CrucibleText.StripCodes(row.Text);
            // `is not { } id` is true when the id is null; past it, `id` holds the plain value.
            if (text.Length == 0 || idOf(row) is not { } id)
                continue;

            // A second row with the same text makes the text ambiguous for good.
            // `TryAdd` adds the entry only when the key is new, and says whether it did.
            if (!index.TryAdd(text, id))
                index[text] = null;
        }

        return index;
    }

    /// <summary>The id a drawn name resolves to, or null when it resolves to none.</summary>
    private static T? Find<T>(Dictionary<string, T?> index, string drawn)
        where T : struct =>
        // `TryGetValue` returns whether the key is there and, when it is, writes its value into `id`.
        index.TryGetValue(drawn, out var id) ? id : null;
}
