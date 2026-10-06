using System.Text;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads the numbers the Crucible windows draw as text: token balances, prices, HP as
/// "current/max", and familiar ranks.
/// </summary>
/// <remarks>
/// <para>
/// The client hands a window its text raw, with the game's formatting codes still in it: a color
/// wrap around an item's name, or around a price the balance cannot cover. A code runs from the
/// character <c>\u0002</c> to the next <c>\u0003</c>, and its payload can hold digits, so codes are
/// removed before any number is read.
/// </para>
/// <para>
/// Numbers are read by their digits alone. The separators between digit groups differ by client
/// language, and the words around a number are the client's language too, so neither is assumed.
/// </para>
/// </remarks>
// A `static class` holds only static members and is never instantiated: a module of plain
// functions, like a TypeScript file that exports functions.
public static class CrucibleText
{
    // A `char` is one 16-bit character, written in single quotes; "x" in double quotes is a string.
    // `\u0002` spells a character by its code point, which is how invisible characters stay visible
    // in source.

    /// <summary>Starts a formatting code.</summary>
    private const char CodeStart = '\u0002';

    /// <summary>Ends a formatting code.</summary>
    private const char CodeEnd = '\u0003';

    /// <summary>
    /// The private-use glyph (U+E0BC) the team window draws before a rank the board has capped (the paw
    /// glyph, U+E036, comes before every rank).
    /// </summary>
    private const char RankSyncGlyph = '\uE0BC';

    /// <summary>The text with every formatting code removed and the ends trimmed.</summary>
    /// <param name="text">The raw text a window was handed.</param>
    /// <remarks>
    /// A code with no end is cut at its start: what follows it is the code's payload, never text.
    /// </remarks>
    public static string StripCodes(string text)
    {
        // A StringBuilder collects characters without building a new string per append, like
        // pushing into an array and joining once in TypeScript.
        var plain = new StringBuilder(text.Length);
        var inCode = false;

        foreach (var character in text)
        {
            if (character == CodeStart)
                inCode = true;
            else if (character == CodeEnd)
                inCode = false;
            else if (!inCode)
                plain.Append(character);
        }

        return plain.ToString().Trim();
    }

    /// <summary>
    /// The number the text starts with, read through any digit-group separators, or null when the
    /// text does not start with one.
    /// </summary>
    /// <param name="text">The raw text, formatting codes included.</param>
    /// <remarks>
    /// Reading stops at the first character that is neither a digit nor a separator between two
    /// digits, so "25 (-50%)" reads 25. No number reads as null rather than zero: zero is a valid
    /// reading (an empty purse) and must not stand in for a misread.
    /// </remarks>
    // `string?` marks text that may be null. `int?` is a number that may be null, which a plain
    // `int` cannot be: the TypeScript `number | null`.
    public static int? ReadNumber(string? text)
    {
        if (text is null)
            return null;

        var plain = StripCodes(text);
        var value = 0;
        var digits = 0;

        for (var index = 0; index < plain.Length; index++)
        {
            var character = plain[index];

            if (char.IsAsciiDigit(character))
            {
                // A number too long for an int is not one these windows draw.
                if (value > (int.MaxValue - 9) / 10)
                    return null;

                // Characters are numbers underneath, and the digits run in order from '0', so
                // subtracting '0' turns the character '7' into the number 7.
                value = (value * 10) + (character - '0');
                digits++;
                continue;
            }

            // A separator counts only between two digits; anything else ends the number.
            var between = digits > 0
                          && index + 1 < plain.Length
                          && char.IsAsciiDigit(plain[index + 1]);
            if (between && IsGroupSeparator(character))
                continue;

            break;
        }

        return digits > 0 ? value : null;
    }

    /// <summary>
    /// The first number in the text, wherever it starts, or null when the text has no digit.
    /// </summary>
    /// <param name="text">The raw text, formatting codes included.</param>
    /// <remarks>
    /// For counts drawn after a sign, such as "x 3" on the results screen, where the sign and its
    /// spacing are the client's language and the number is not.
    /// </remarks>
    public static int? ReadFirstNumber(string? text)
    {
        if (text is null)
            return null;

        var plain = StripCodes(text);
        for (var index = 0; index < plain.Length; index++)
        {
            // `plain[index..]` is the rest of the string from this position, like slice() in TypeScript.
            if (char.IsAsciiDigit(plain[index]))
                return ReadNumber(plain[index..]);
        }

        return null;
    }

    /// <summary>
    /// The two numbers of a "current/max" fraction, or null when the text does not have exactly one
    /// slash with a number leading each side.
    /// </summary>
    /// <param name="text">The raw text, formatting codes included.</param>
    // `(int Current, int Max)?` is a nullable tuple: two named values traveling together, like a
    // TypeScript `{ current: number; max: number } | null`.
    public static (int Current, int Max)? ReadFraction(string? text)
    {
        if (text is null)
            return null;

        var parts = StripCodes(text).Split('/');
        if (parts.Length != 2)
            return null;

        // `x is { } current` matches when x is not null and names the value `current`, already
        // unwrapped from `int?` to `int`: a null check and a variable declaration in one step.
        return ReadNumber(parts[0]) is { } current && ReadNumber(parts[1]) is { } max
            ? (current, max)
            : null;
    }

    /// <summary>
    /// A team-window rank label read as its rank and whether the board has capped it, or null when
    /// the label carries no digits.
    /// </summary>
    /// <param name="label">
    /// The raw label: a paw glyph and the digits, behind a sync glyph when capped.
    /// </param>
    public static (int Rank, bool Synced)? ReadRank(string? label)
    {
        if (label is null)
            return null;

        var plain = StripCodes(label);
        var digits = new StringBuilder(plain.Length);
        foreach (var character in plain)
        {
            if (char.IsAsciiDigit(character))
                digits.Append(character);
        }

        // `is not { } rank` is the negated form, true when the number is null. That branch returns,
        // so past this `if` the compiler knows `rank` was set, and it is a plain `int`.
        if (digits.Length == 0 || ReadNumber(digits.ToString()) is not { } rank)
            return null;

        return (rank, plain.Contains(RankSyncGlyph));
    }

    /// <summary>
    /// Whether a character separates digit groups in some client language: a comma, a period, a
    /// space, a no-break space or a narrow no-break space.
    /// </summary>
    // `=>` gives a method a single expression as its body, like an arrow function without braces.
    // `is A or B` is true when the character is any of the listed ones.
    private static bool IsGroupSeparator(char character) =>
        character is ',' or '.' or ' ' or '\u00A0' or '\u202F';
}
