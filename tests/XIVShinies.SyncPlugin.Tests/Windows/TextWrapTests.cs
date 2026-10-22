using Xunit;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// Where a paragraph's lines break: only where the text has a space (TextWrap's remarks say why).
public class TextWrapTests
{
    // Every character measures one unit wide, so a test can count widths in characters.
    private static string Break(string text, float width) =>
        TextWrap.BreakAtSpaces(text, width, static s => s.Length, out _);

    // A space between two words measures one unit wider than a space alone, the way a kerned font can
    // draw a whole line wider than its words and spaces measured one by one.
    private static float Kerned(string s)
    {
        var width = (float)s.Length;
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == ' ' && s[i - 1] != ' ' && s[i + 1] != ' ')
                width += 1f;
        }

        return width;
    }

    [Fact]
    public void Text_that_fits_is_left_alone()
    {
        Assert.Equal("fill in your run", Break("fill in your run", 40));
    }

    [Fact]
    public void A_line_that_would_overrun_breaks_at_the_last_space_that_fits()
    {
        Assert.Equal("fill in your\nrun", Break("fill in your run", 14));
    }

    // The case TextWrap exists for.
    [Fact]
    public void An_address_is_never_split_at_its_dots()
    {
        Assert.Equal("your run on\nxiv-shinies.com.", Break("your run on xiv-shinies.com.", 22));
    }

    // ImGui's own wrapping then splits the word, since it has no space to break at.
    [Fact]
    public void A_word_wider_than_the_line_stands_on_its_own_line()
    {
        Assert.Equal("on\nunbreakablelongword\nhere", Break("on unbreakablelongword here", 8));
        Assert.Equal("unbreakablelongword\nhere", Break("unbreakablelongword here", 8));
    }

    [Fact]
    public void Existing_line_breaks_are_kept()
    {
        Assert.Equal("one two\nthree four", Break("one two\nthree four", 40));
        Assert.Equal("one\ntwo\nthree\nfour", Break("one two\nthree four", 6));
    }

    [Fact]
    public void An_empty_line_between_paragraphs_is_kept()
    {
        Assert.Equal("one\n\ntwo", Break("one\n\ntwo", 10));
    }

    [Fact]
    public void A_run_of_spaces_at_a_break_leaves_no_blank_line()
    {
        Assert.Equal("aaaa\nbbbb", Break("aaaa  bbbb", 4));
        Assert.Equal("aaaa\nbb", Break("aaaa  bb", 4));
    }

    [Fact]
    public void Spaces_at_either_end_leave_no_blank_line()
    {
        Assert.Equal("abc\ndef", Break(" abc def", 3));
        Assert.Equal("abc\ndef", Break("abc def ", 3));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    public void No_width_leaves_the_text_alone(float width)
    {
        Assert.Equal("fill in your run", Break("fill in your run", width));
    }

    [Fact]
    public void Empty_text_stays_empty()
    {
        Assert.Equal(string.Empty, Break(string.Empty, 10));
    }

    // Covers both the whole-line check and the word-by-word one.
    [Fact]
    public void A_line_exactly_as_wide_as_the_room_fits()
    {
        Assert.Equal("ab cd", Break("ab cd", 5));
        Assert.Equal("ab cd\nef", Break("ab cd\nef", 5));
    }

    // Whether every line fits tells the drawing code whether ImGui's own wrapping is still needed.
    [Theory]
    [InlineData("fill in your run", 40f, true)]
    [InlineData("fill in your run", 14f, true)]
    [InlineData("one two\nthree four", 6f, true)]
    [InlineData("abcde fg", 5f, true)]
    [InlineData("", 10f, true)]
    [InlineData("on unbreakablelongword here", 8f, false)]
    [InlineData("one\nunbreakable", 6f, false)]
    [InlineData("fill in your run", 0f, false)]
    [InlineData("fill in your run", -5f, false)]
    public void It_says_whether_every_line_fits(string text, float width, bool expected)
    {
        TextWrap.BreakAtSpaces(text, width, static s => s.Length, out var fits);

        Assert.Equal(expected, fits);
    }

    // Each finished line is measured whole, so a line whose parts fit but which draws wider as a whole
    // moves its last word down instead of running past the edge.
    [Fact]
    public void A_line_wider_as_a_whole_than_its_parts_moves_its_last_word_down()
    {
        var broken = TextWrap.BreakAtSpaces("ab cd ef", 5f, Kerned, out var fits);

        Assert.Equal("ab\ncd\nef", broken);
        Assert.True(fits);
    }

    // The word moved down still joins the words after it when they fit together.
    [Fact]
    public void A_word_moved_down_starts_the_next_line()
    {
        Assert.Equal("ab\ncd e", TextWrap.BreakAtSpaces("ab cd e", 5f, Kerned, out _));
    }
}
