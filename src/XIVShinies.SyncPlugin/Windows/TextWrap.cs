using System;
using System.Text;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>Decides where a paragraph's lines break when it is drawn at a given width.</summary>
/// <remarks>
/// ImGui's own wrapping can break just after a period, splitting an address such as
/// <c>xiv-shinies.com</c>. Breaking at spaces before ImGui sees the text leaves it only a word wider
/// than the room to split. The caller passes the measure, which in the plugin is the ImGui font.
/// </remarks>
internal static class TextWrap
{
    /// <summary>Puts a line break wherever a line would overrun the width, breaking only at spaces.</summary>
    /// <param name="text">The paragraph, which may already hold line breaks of its own.</param>
    /// <param name="width">The room a line has, in the same units the measure gives.</param>
    /// <param name="measure">How wide a piece of text draws.</param>
    /// <returns>
    /// The text unchanged when it fits as it is or there is no room. Otherwise, with breaks added,
    /// runs of spaces collapsed to one and the spaces at either end of a line dropped, as ImGui's
    /// wrapping does; a word wider than the room gets a line of its own.
    /// </returns>
    internal static string BreakAtSpaces(string text, float width, Func<string, float> measure)
    {
        if (width <= 0f || text.Length == 0)
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

            // Each word is measured once, and a line's width is its words plus the spaces between.
            // Dropping empty entries means no line starts with a space.
            var lineWidth = 0f;
            var lineEmpty = true;
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
