using System;
using System.Collections.Generic;
// StringBuilder assembles a string piece by piece without making a new string for every piece.
using System.Text;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>Decides where a paragraph's lines break when it is drawn at a given width.</summary>
/// <remarks>
/// <para>
/// ImGui's own wrapping may also break a line just after a period, comma or similar mark, so a
/// website address such as <c>xiv-shinies.com</c> can be split across two lines. Lines broken
/// here break only where the text has a space, so an address, like any word that fits on a line,
/// stays whole. The drawing code puts the line breaks in, and when every line fits it turns ImGui's
/// own wrapping off, because that wrapping can break again a line found here to fit, stranding its
/// last word on a line of its own. When a word is wider than the room, ImGui's wrapping stays on for
/// that text so it can split the word, which has no space to break at; with no room to measure
/// against, no breaks are put in here and ImGui's wrapping makes all of them.
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
    /// <summary>
    /// Puts a line break wherever a line would overrun the width, breaking only at spaces, and says
    /// whether every line of the result fits.
    /// </summary>
    /// <param name="text">The paragraph, which may already hold line breaks of its own.</param>
    /// <param name="width">The room a line has, in the same units the measure gives.</param>
    /// <param name="measure">How wide a piece of text draws.</param>
    /// <param name="everyLineFits">
    /// False when a word is wider than the room, or there is no room to measure against: the cases in
    /// which ImGui's own wrapping is still needed.
    /// </param>
    /// <returns>
    /// Text with no breaks of its own that already fits, and text with no room to measure against,
    /// come back unchanged. Otherwise the text comes back with breaks added, each run of spaces
    /// between words reduced to one and the spaces at either end of a line dropped, as ImGui's own
    /// wrapping skips the spaces at a break; a word wider than the room goes on a line of its own.
    /// </returns>
    // `Func<string, float>` is a function value that takes a string and returns a float, like
    // `(text: string) => number`. `out bool` hands a second result back through the parameter, which
    // the method must set before it returns, like returning `[text, fits]` from a TypeScript function.
    internal static string BreakAtSpaces(
        string text, float width, Func<string, float> measure, out bool everyLineFits)
    {
        // The `f` suffix makes `0f` a `float` literal; a bare `0` would be an `int`.
        if (width <= 0f)
        {
            everyLineFits = false;
            return text;
        }

        everyLineFits = true;
        if (text.Length == 0)
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

            // Each word is measured once and the line's width kept as a running sum of its words and
            // the spaces between them, which decides where a line ends. `RemoveEmptyEntries` drops the
            // empty pieces that leading, trailing and doubled spaces would otherwise leave, so no line
            // starts with a space and no run of spaces becomes a blank line. `List<T>` is a growable
            // array, like a TypeScript `T[]`.
            var line = new List<string>();
            var lineWordWidths = new List<float>();
            var lineWidth = 0f;

            // `foreach (var x in list)` visits each element in turn, like `for (const x of list)`.
            foreach (var word in paragraphs[p].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = measure(word);
                if (wordWidth > width)
                    everyLineFits = false;

                // The line ends before this word; the words its whole width had no room for start
                // the next one, and the word joins them once they leave it room.
                while (line.Count > 0 && lineWidth + spaceWidth + wordWidth > width)
                {
                    EndLine(result, line, lineWordWidths, width, measure);
                    result.Append('\n');
                    lineWidth = WidthOf(lineWordWidths, spaceWidth);
                }

                line.Add(word);
                lineWordWidths.Add(wordWidth);
                lineWidth = WidthOf(lineWordWidths, spaceWidth);
            }

            // The paragraph's last line, and any word its whole width moves down.
            while (line.Count > 0)
            {
                EndLine(result, line, lineWordWidths, width, measure);
                if (line.Count > 0)
                    result.Append('\n');
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Appends a finished line's words, keeping back any words at its end that its whole width has
    /// no room for; those stay in <paramref name="line"/> to start the next line.
    /// </summary>
    /// <remarks>
    /// The running sum decided where the line ends, and the whole line is then measured once more,
    /// because a font that kerns can draw the whole line wider than its words and spaces measured one
    /// by one. With ImGui's own wrapping switched off (see the class remarks), this check is what
    /// keeps the line inside the edge. The first word always stays, so every call appends one.
    /// </remarks>
    private static void EndLine(
        StringBuilder result, List<string> line, List<float> wordWidths, float width,
        Func<string, float> measure)
    {
        var kept = line.Count;
        while (kept > 1 && measure(string.Join(' ', line.GetRange(0, kept))) > width)
            kept--;

        result.AppendJoin(' ', line.GetRange(0, kept));
        line.RemoveRange(0, kept);
        wordWidths.RemoveRange(0, kept);
    }

    /// <summary>A line's width from its words' widths: the words plus one space between each two.</summary>
    private static float WidthOf(List<float> wordWidths, float spaceWidth)
    {
        if (wordWidths.Count == 0)
            return 0f;

        var width = spaceWidth * (wordWidths.Count - 1);
        foreach (var wordWidth in wordWidths)
            width += wordWidth;

        return width;
    }
}
