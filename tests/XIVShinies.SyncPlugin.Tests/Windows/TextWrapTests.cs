using Xunit;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// Where a paragraph's lines break: only where the text has a space (TextWrap's remarks say why).
public class TextWrapTests
{
    // Every character measures one unit wide, so a test can count widths in characters.
    private static string Break(string text, float width) =>
        TextWrap.BreakAtSpaces(text, width, static s => s.Length);

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
}
