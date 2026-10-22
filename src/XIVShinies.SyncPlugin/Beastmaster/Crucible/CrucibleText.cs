using System.Text;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads the numbers the Crucible windows draw as text: token balances, prices, HP as
/// "current/max", and familiar ranks.
/// </summary>
/// <remarks>
/// The client hands a window its text raw, with the game's formatting codes in it, such as a color
/// wrap around an item's name or a price the balance cannot cover. A code runs from <c>\u0002</c> to
/// the next <c>\u0003</c> and its payload can hold digits, so codes are removed before any number is
/// read. Numbers are read by their digits alone, since digit-group separators and the words around a
/// number vary with the client's language.
/// </remarks>
public static class CrucibleText
{
    private const char CodeStart = '\u0002';
    private const char CodeEnd = '\u0003';

    /// <summary>
    /// The glyph the team window draws before a rank the board has capped. The paw glyph, U+E036,
    /// comes before every rank.
    /// </summary>
    private const char RankSyncGlyph = '\uE0BC';

    /// <summary>The text with every formatting code removed and the ends trimmed.</summary>
    /// <remarks>
    /// A code with no end is cut at its start: what follows it is the code's payload, never text.
    /// </remarks>
    public static string StripCodes(string text)
    {
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
    /// <remarks>
    /// Reading stops at the first character that is neither a digit nor a separator between two
    /// digits, so "25 (-50%)" reads 25. No number reads as null, not zero: zero is a valid reading,
    /// an empty purse, and must not stand in for a misread.
    /// </remarks>
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
            if (char.IsAsciiDigit(plain[index]))
                return ReadNumber(plain[index..]);
        }

        return null;
    }

    /// <summary>
    /// The two numbers of a "current/max" fraction, or null when the text does not have exactly one
    /// slash with a number leading each side.
    /// </summary>
    public static (int Current, int Max)? ReadFraction(string? text)
    {
        if (text is null)
            return null;

        var parts = StripCodes(text).Split('/');
        if (parts.Length != 2)
            return null;

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

        if (digits.Length == 0 || ReadNumber(digits.ToString()) is not { } rank)
            return null;

        return (rank, plain.Contains(RankSyncGlyph));
    }

    /// <summary>
    /// Whether a character separates digit groups in some client language: a comma, a period, a
    /// space, a no-break space or a narrow no-break space.
    /// </summary>
    private static bool IsGroupSeparator(char character) =>
        character is ',' or '.' or ' ' or '\u00A0' or '\u202F';
}
