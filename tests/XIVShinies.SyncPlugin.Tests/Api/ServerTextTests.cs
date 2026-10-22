using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Every server string passes through ServerText before the plugin keeps, draws or logs it. Its
// callers' tests cover most of it; this file pins the cut marker and invisible characters. The
// backend URL is user-overridable, so a hostile or misconfigured server's text is a reachable case.
public class ServerTextTests
{
    // --- The cut marker ------------------------------------------------------------------------

    // The marker tells a reader the server sent more than is shown.

    [Fact]
    public void A_marked_cut_ends_with_the_marker()
    {
        var clamped = ServerText.Clamp(new string('x', ServerText.MaxAdoptedLength + 50), ellipsis: true);

        Assert.EndsWith("...", clamped);
        Assert.Equal(ServerText.MaxAdoptedLength + 3, clamped.Length);
    }

    // The marker says something the text does not, so it is added past the bound, not fitted inside.
    [Fact]
    public void A_marked_cut_keeps_the_full_bound_of_server_text()
    {
        var clamped = ServerText.Clamp(new string('x', ServerText.MaxAdoptedLength + 50), ellipsis: true);

        Assert.Equal(new string('x', ServerText.MaxAdoptedLength), clamped[..^3]);
    }

    [Fact]
    public void Text_within_the_bound_is_never_marked()
    {
        Assert.Equal("short", ServerText.Clamp("short", ellipsis: true));
    }

    // An unmarked cut is for a value that is matched or compared rather than read, which a marker
    // would corrupt.
    [Fact]
    public void An_unmarked_cut_carries_no_marker()
    {
        var clamped = ServerText.Clamp(new string('x', ServerText.MaxAdoptedLength + 50));

        Assert.Equal(ServerText.MaxAdoptedLength, clamped.Length);
        Assert.DoesNotContain(".", clamped);
    }

    // --- Invisible characters ------------------------------------------------------------------

    // These characters leave no visible trace and reach the fold's format-category branch. A "\n" never
    // reaches it: a newline is a control character, handled by the whitespace-and-control branch.

    // A zero-width space can split a word invisibly. It is neither whitespace nor a control character.
    [Fact]
    public void A_zero_width_space_is_folded_away()
    {
        Assert.Equal("ab", ServerText.SingleLine("a​b"));
    }

    // A right-to-left override reverses the reading order of everything after it, so a note could
    // display as something other than what was sent, and the log would not match the screen.
    [Fact]
    public void A_right_to_left_override_is_folded_away()
    {
        Assert.Equal("abc", ServerText.SingleLine("a‮bc"));
    }

    // Dropped, not folded to a space: a gap would insert a break the server never sent.
    [Fact]
    public void Format_characters_between_words_do_not_become_extra_spaces()
    {
        Assert.Equal("one two", ServerText.SingleLine("one​ ​two"));
    }

    // Null, so a caller does not draw an empty line.
    [Fact]
    public void Text_made_only_of_invisible_characters_becomes_null()
    {
        Assert.Null(ServerText.SingleLine("​‮"));
    }

    // The bound applies before the fold walks every character, so a hostile string's cost is capped
    // however much of it is invisible.
    [Fact]
    public void A_long_line_of_invisible_characters_is_still_bounded()
    {
        var folded = ServerText.SingleLine(new string('​', 10_000) + "tail");

        // Only the first MaxAdoptedLength characters are kept, and all of them fold away.
        Assert.Null(folded);
    }

    // --- What the fold keeps --------------------------------------------------------------------

    // A zero-width joiner binds an emoji sequence into one glyph, so dropping it splits the picture.
    // It cannot reorder or hide anything.
    [Fact]
    public void A_zero_width_joiner_survives_the_fold()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

        Assert.Equal(family, ServerText.SingleLine(family));
    }

    // The zero-width non-joiner is required spelling in Persian and Arabic, so dropping it misspells.
    [Fact]
    public void A_zero_width_non_joiner_survives_the_fold()
    {
        Assert.Equal("a\u200Cb", ServerText.SingleLine("a\u200Cb"));
    }

    // The two joiners are kept by name, not by category, so the override still folds away. A string
    // of joiners alone is kept too, though it shows nothing.
    [Fact]
    public void The_kept_characters_do_not_reopen_the_reordering_hole()
    {
        Assert.Equal("a\u200Db", ServerText.SingleLine("a\u200Db"));
        Assert.Equal("ab", ServerText.SingleLine("a\u202Eb"));
    }
}
