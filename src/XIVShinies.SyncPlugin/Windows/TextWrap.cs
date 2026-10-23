using System;
using System.Collections.Generic;
using System.Text;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>Decides where a paragraph's lines break when it is drawn at a given width.</summary>
/// <remarks>
/// ImGui's own wrapping can break just after a period, splitting an address such as
/// <c>xiv-shinies.com</c>, and can break again a line found here to fit, stranding its last word. So
/// lines break here at spaces, and the drawing code turns ImGui's wrapping off when every line fits,
/// leaving it on only to split a word wider than the room or, with no room to measure against, to
/// make every break. The caller passes the measure, which in the plugin is the ImGui font.
/// </remarks>
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
    /// False when a word is wider than the room or there is no room: the cases that still need
    /// ImGui's own wrapping.
    /// </param>
    /// <returns>
    /// The text unchanged when it fits as it is or there is no room. Otherwise, with breaks added,
    /// runs of spaces collapsed to one and the spaces at either end of a line dropped, as ImGui's
    /// wrapping does; a word wider than the room gets a line of its own.
    /// </returns>
    internal static string BreakAtSpaces(
        string text, float width, Func<string, float> measure, out bool everyLineFits)
    {
        if (width <= 0f)
        {
            everyLineFits = false;
            return text;
        }

        everyLineFits = true;
        if (text.Length == 0)
            return text;

        // The common case, a line that fits as it is, costs one measurement and no new string.
        if (!text.Contains('\n') && measure(text) <= width)
            return text;

        // The result is never longer than the input, so the builder never grows.
        var spaceWidth = measure(" ");
        var result = new StringBuilder(text.Length);

        // Each existing line is wrapped on its own, and its break is kept.
        var paragraphs = text.Split('\n');
        for (var p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0)
                result.Append('\n');

            // Each word is measured once, and a running sum of the words and the spaces between
            // decides where a line ends. Dropping empty entries means no line starts with a space.
            var line = new List<string>();
            var lineWordWidths = new List<float>();
            var lineWidth = 0f;

            foreach (var word in paragraphs[p].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = measure(word);
                if (wordWidth > width)
                    everyLineFits = false;

                // The line ends before this word. Any words EndLine keeps back start the next line,
                // and the word joins them once they leave it room.
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

            // The paragraph's last line, and any words EndLine keeps back from it.
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
    /// Appends a finished line, keeping back the words at its end that its whole width has no room
    /// for; they stay in <paramref name="line"/> to start the next one.
    /// </summary>
    /// <remarks>
    /// A font that kerns can draw a whole line wider than its words and spaces measured one by one,
    /// so the line is measured again here. With ImGui's wrapping off (see the class remarks), this is
    /// what keeps the line inside the edge. The first word always stays, so every call appends one.
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

    /// <summary>A line's width: its words plus one space between each two.</summary>
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
