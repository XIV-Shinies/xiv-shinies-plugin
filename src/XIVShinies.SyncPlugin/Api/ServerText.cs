// StringBuilder, for folding a server string onto one line.
using System.Text;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// How a string that came from the server is made safe to keep, render, or log.
/// </summary>
/// <remarks>
/// <para>
/// The backend URL is user-overridable — a Dalamud requirement, not a convenience — so the server
/// is untrusted input. Anything it says can be arbitrarily long, contain any character, or be
/// missing where the contract promises a value. These helpers are the one place that is dealt
/// with, so every adoption site treats it the same way and a new one has something to reach for.
/// </para>
/// <para>
/// Adoption happens at the door: the value is bounded when it enters the plugin, not each time it
/// is drawn. A clamp at the render site only protects the render it guards, and the next surface
/// to show the same string starts the problem over.
/// </para>
/// <para>
/// Every operation here bounds the input <b>before</b> doing work proportional to its length. The
/// only ceiling on an incoming string is the response body limit, so a transform that walks the
/// whole value first — a trim, a copy, a fold — hands a hostile server several megabytes of work,
/// and on the draw path that is several megabytes of work per frame.
/// </para>
/// </remarks>
internal static class ServerText
{
    /// <summary>The longest server string the plugin will keep, in UTF-16 code units.</summary>
    /// <remarks>
    /// Generous for any legitimate value — a note, a validation complaint, a content hash — and
    /// small enough that a hostile one cannot fill a window or a log file.
    /// </remarks>
    public const int MaxAdoptedLength = 500;

    /// <summary>
    /// The text, shortened to <see cref="MaxAdoptedLength"/> if it is longer.
    /// </summary>
    /// <param name="text">The server's string.</param>
    /// <param name="ellipsis">
    /// True to mark a shortened result with three periods, so a reader can tell the text was cut
    /// rather than written that way. False where the value is matched or compared rather than
    /// read, and a marker would corrupt it.
    /// </param>
    public static string Clamp(string text, bool ellipsis = false)
    {
        if (text.Length <= MaxAdoptedLength)
            return text;

        var end = MaxAdoptedLength;

        // A char in .NET is a UTF-16 code unit, and characters outside the basic multilingual
        // plane — emoji, the rarer CJK blocks — are stored as a PAIR of them. Cutting between the
        // halves leaves a lone surrogate: not valid UTF-16, drawn as a replacement box, and an
        // exception the day the value is handed to a serializer. Backing off one unit drops the
        // whole character instead of half of one.
        if (char.IsHighSurrogate(text[end - 1]))
            end--;

        // Three periods, not the single "…" glyph — see MainWindow's Verify label for why.
        return ellipsis ? text[..end] + "..." : text[..end];
    }

    /// <summary>
    /// A server string adopted as one line of display copy: null when it says nothing, and
    /// otherwise bounded, folded onto a single line, and marked if it had to be cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null and blank both become null. A value the contract says is always present can still
    /// arrive missing or empty, and "the server said nothing" is a state the caller already has to
    /// handle — folding the two together means no caller has to tell them apart.
    /// </para>
    /// <para>
    /// Line breaks, repeated spaces and control characters are collapsed because a length cap does
    /// not bound the SHAPE of what is drawn. Five hundred newlines sit well inside the cap and
    /// would still bury everything below them, and a control character draws as a placeholder box;
    /// between them a server could lay out its own copy inside a panel it does not own. A one-line
    /// explanation has no use for a second line, so nothing the field is for is lost.
    /// </para>
    /// </remarks>
    public static string? SingleLine(string? text)
    {
        if (text is null)
            return null;

        // Bounded before the fold below, which is the part that walks every character — the
        // bound-before-work rule this type is built on.
        var cut = text.Length > MaxAdoptedLength;
        var bounded = cut ? Clamp(text) : text;

        var folded = new StringBuilder(bounded.Length);
        var pendingSpace = false;

        foreach (var character in bounded)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                // Deferred rather than appended: a run becomes one space, and a run at the very
                // end becomes nothing at all, which is the trim.
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

        // Whitespace and control characters alone leave nothing to say.
        if (folded.Length == 0)
            return null;

        // Folding only ever shortens, so the bound above still holds and the marker is the one
        // thing left to add — it tells a reader the sentence stops because it was cut.
        return cut ? folded.Append("...").ToString() : folded.ToString();
    }
}
