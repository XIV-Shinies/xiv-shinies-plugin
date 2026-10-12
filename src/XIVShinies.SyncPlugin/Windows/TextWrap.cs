using System;
// StringBuilder assembles a string piece by piece without making a new string for every piece.
using System.Text;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>Decides where a paragraph's lines break when it is drawn at a given width.</summary>
/// <remarks>
/// <para>
/// ImGui's own wrapping may also break a line just after a period, comma or similar mark, so a
/// website address such as <c>xiv-shinies.com</c> can be split across two lines. Lines broken
/// here break only where the text has a space, so an address, like any word that fits on a line,
/// stays whole. The drawing code puts the line breaks in before handing the text to ImGui, whose
/// wrapping then finds every line already fits, except a line holding a single word wider than the
/// room, which ImGui still splits.
/// </para>
/// <para>
/// It measures nothing itself: the caller passes the function that says how wide a piece of text
/// draws, which in the plugin is the current ImGui font.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
// `internal` makes it visible only inside the plugin's own assembly (its compiled DLL), plus the test
// project, which the project file names in an `InternalsVisibleTo` entry.
internal static class TextWrap
{
    /// <summary>Puts a line break wherever a line would overrun the width, breaking only at spaces.</summary>
    /// <param name="text">The paragraph, which may already hold line breaks of its own.</param>
    /// <param name="width">The room a line has, in the same units the measure gives.</param>
    /// <param name="measure">How wide a piece of text draws.</param>
    /// <returns>
    /// Text with no breaks of its own that already fits, and text with no room to measure against,
    /// come back unchanged. Otherwise the text comes back with breaks added, each run of spaces
    /// between words reduced to one and the spaces at either end of a line dropped, as ImGui's own
    /// wrapping skips the spaces at a break; a word wider than the room goes on a line of its own.
    /// </returns>
    // `Func<string, float>` is a function value that takes a string and returns a float, like
    // `(text: string) => number`.
    internal static string BreakAtSpaces(string text, float width, Func<string, float> measure)
    {
        // The `f` suffix makes `0f` a `float` literal; a bare `0` would be an `int`.
        if (width <= 0f || text.Length == 0)
            return text;

        // The common case, a line that fits as it is, costs one measurement and no new string. Single
        // quotes make a `char`, one character, which C# keeps distinct from a one-character string.
        if (!text.Contains('\n') && measure(text) <= width)
            return text;

        // `var` lets the compiler infer the type, like an unannotated `let`. The number given to
        // `new StringBuilder(...)` is its starting capacity, not its contents. The result is never
        // longer than the input (each break replaces a space, and spaces only ever collapse), so the
        // builder never has to grow.
        var spaceWidth = measure(" ");
        var result = new StringBuilder(text.Length);

        // Each existing line is wrapped on its own, and its break is kept. `Split` cuts a string at
        // every occurrence of a character into an array of the pieces, like JavaScript's `split`.
        var paragraphs = text.Split('\n');
        for (var p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0)
                result.Append('\n');

            // Each word is measured once and the line's width kept as a running sum: a line is as
            // wide as its words plus the spaces between them. `RemoveEmptyEntries` drops the empty
            // pieces that leading, trailing and doubled spaces would otherwise leave, so no line
            // starts with a space and no run of spaces becomes a blank line.
            var lineWidth = 0f;
            var lineEmpty = true;
            // `foreach (var x in list)` visits each element in turn, like `for (const x of list)`.
            foreach (var word in paragraphs[p].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = measure(word);
                if (lineEmpty)
                {
                    result.Append(word);
                    lineWidth = wordWidth;
                    lineEmpty = false;
                }
                else if (lineWidth + spaceWidth + wordWidth <= width)
                {
                    result.Append(' ').Append(word);
                    lineWidth += spaceWidth + wordWidth;
                }
                else
                {
                    // The space this word followed becomes the line break.
                    result.Append('\n').Append(word);
                    lineWidth = wordWidth;
                }
            }
        }

        return result.ToString();
    }
}
