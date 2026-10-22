using System.Globalization;
using System.Text;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// How a string that came from the server is made safe to keep, render, or log.
/// </summary>
/// <remarks>
/// The backend URL is user-overridable (a Dalamud requirement), so server strings are untrusted: any
/// length, any character, or missing where the contract promises a value. Bound a value once, where it
/// enters the plugin, not at each render site. Every operation here bounds its input before doing
/// work proportional to its length, because the only other ceiling is the response body limit.
/// </remarks>
internal static class ServerText
{
    /// <summary>The longest server string the plugin will keep, in UTF-16 code units.</summary>
    /// <remarks>
    /// Generous for a note, a validation complaint or a content hash, and small enough that a hostile
    /// one cannot fill a window or a log file. A marked result is this plus the three periods.
    /// </remarks>
    public const int MaxAdoptedLength = 500;

    /// <summary>
    /// The text, shortened to <see cref="MaxAdoptedLength"/> if it is longer.
    /// </summary>
    /// <param name="text">The server's string.</param>
    /// <param name="ellipsis">
    /// True to mark a shortened result with three periods, so a reader can tell it was cut. False
    /// where the value is matched or compared, and a marker would corrupt it.
    /// </param>
    public static string Clamp(string text, bool ellipsis = false)
    {
        if (text.Length <= MaxAdoptedLength)
            return text;

        var end = MaxAdoptedLength;

        // Never cut inside a surrogate pair: a lone surrogate is invalid UTF-16, draws as a box, and
        // throws in a serializer. Backing off one unit drops the whole character.
        if (char.IsHighSurrogate(text[end - 1]))
            end--;

        // Three periods, not the single "…" glyph; see MainWindow's Verify label for why.
        return ellipsis ? text[..end] + "..." : text[..end];
    }

    /// <summary>
    /// Whether a character is invisible formatting that a display string is safer without.
    /// </summary>
    /// <remarks>
    /// Unicode's Cf category, minus the zero-width joiner, which binds emoji sequences, and the
    /// non-joiner, which Persian and Arabic spelling needs; neither can reorder or hide text. What is
    /// left can lie about its content: bidirectional controls reorder everything after them, and
    /// zero-width spaces split a word invisibly.
    /// </remarks>
    private static bool IsInvisibleFormatting(char character) =>
        CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format
        && character is not ('\u200C' or '\u200D');

    /// <summary>
    /// A server string adopted as one line of display copy: null when it says nothing, and
    /// otherwise bounded, folded onto a single line, and marked if it had to be cut.
    /// </summary>
    /// <remarks>
    /// Null and blank both become null, since a promised value can still arrive empty and callers
    /// already handle "the server said nothing". Folding bounds the shape a length cap cannot: five
    /// hundred newlines fit the cap and would bury the panel, and a control character draws as a box.
    /// </remarks>
    public static string? SingleLine(string? text)
    {
        if (text is null)
            return null;

        // Bound before the fold, which walks every character.
        var cut = text.Length > MaxAdoptedLength;
        var bounded = cut ? Clamp(text) : text;

        var folded = new StringBuilder(bounded.Length);
        var pendingSpace = false;

        foreach (var character in bounded)
        {
            // Invisible formatting takes no width, so it leaves no space: "a<ZWSP>b" stays one word.
            // pendingSpace is untouched, so one inside a whitespace run neither makes nor hides a gap.
            if (IsInvisibleFormatting(character))
                continue;

            // Whitespace and control characters both collapse to at most one space.
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                // Deferred, so a run becomes one space and a trailing run becomes nothing: the trim.
                pendingSpace = folded.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                folded.Append(' ');
                pendingSpace = false;
            }

            folded.Append(character);
        }

        if (folded.Length == 0)
            return null;

        // Folding only shortens, so the bound still holds.
        return cut ? folded.Append("...").ToString() : folded.ToString();
    }
}
