using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// ServerText is the door every string that came from the server passes through before the plugin
// keeps, renders, or logs it. Most of its behaviour is pinned where it is used — through
// ConfigResponse.CategoryNote, the settings view's group labels, and the upload log's complaints.
// Two things are pinned against the type itself instead: the shape of a marked cut, which is a rule
// about this type rather than about any one caller, and what the fold does to a character with no
// visible trace at all. The backend URL is user-overridable, so "what a hostile or misconfigured
// server can put on screen" is a reachable state rather than a hypothetical.
public class ServerTextTests
{
    // --- The cut marker ------------------------------------------------------------------------

    // Clamp's ellipsis argument is the difference between "the server said this" and "the server
    // said this and there was more". Pinned here, against Clamp itself, so the marker's shape is
    // stated where the rule lives rather than only inside whichever caller happens to ask for it.

    [Fact]
    public void A_marked_cut_ends_with_the_marker()
    {
        var clamped = ServerText.Clamp(new string('x', ServerText.MaxAdoptedLength + 50), ellipsis: true);

        Assert.EndsWith("...", clamped);
        Assert.Equal(ServerText.MaxAdoptedLength + 3, clamped.Length);
    }

    // The marker says something the text does not, so it is added rather than fitted inside the
    // bound — the kept text is still a full MaxAdoptedLength of what the server sent.
    [Fact]
    public void A_marked_cut_keeps_the_full_bound_of_server_text()
    {
        var clamped = ServerText.Clamp(new string('x', ServerText.MaxAdoptedLength + 50), ellipsis: true);

        Assert.Equal(new string('x', ServerText.MaxAdoptedLength), clamped[..^3]);
    }

    // Nothing was cut, so there is nothing to mark — a marker here would report a truncation that
    // never happened.
    [Fact]
    public void Text_within_the_bound_is_never_marked()
    {
        Assert.Equal("short", ServerText.Clamp("short", ellipsis: true));
    }

    // Where the value is matched or compared rather than read, a marker would corrupt it.
    [Fact]
    public void An_unmarked_cut_carries_no_marker()
    {
        var clamped = ServerText.Clamp(new string('x', ServerText.MaxAdoptedLength + 50));

        Assert.Equal(ServerText.MaxAdoptedLength, clamped.Length);
        Assert.DoesNotContain(".", clamped);
    }

    // --- Invisible characters ------------------------------------------------------------------

    // The fold drops three kinds of character, and the format category is the one with no visible
    // trace at all. A test using "\n" would not reach it — a newline is a control character, not a
    // format one, so it falls to the whitespace-and-control branch instead.

    // A zero-width space splits a word for no visible reason, so a sentence can be made to read as
    // two. It is neither whitespace nor a control character.
    [Fact]
    public void A_zero_width_space_is_folded_away()
    {
        Assert.Equal("ab", ServerText.SingleLine("a​b"));
    }

    // The serious one: a right-to-left override reverses the reading order of everything after it,
    // so a note could be made to display as something other than what was sent — and what a
    // reviewer reads in a log would not be what a user reads on screen.
    [Fact]
    public void A_right_to_left_override_is_folded_away()
    {
        Assert.Equal("abc", ServerText.SingleLine("a‮bc"));
    }

    // Dropped rather than folded to a space: the characters are invisible, so replacing them with
    // a gap would insert a break the server never sent.
    [Fact]
    public void Format_characters_between_words_do_not_become_extra_spaces()
    {
        Assert.Equal("one two", ServerText.SingleLine("one​ ​two"));
    }

    // A string with nothing left after the fold has nothing to say, and a caller that draws it
    // would be drawing an empty line.
    [Fact]
    public void Text_made_only_of_invisible_characters_becomes_null()
    {
        Assert.Null(ServerText.SingleLine("​‮"));
    }

    // The bound is applied before the fold — the part that walks every character — so the work a
    // hostile string can cause is capped no matter how much of it is invisible.
    [Fact]
    public void A_long_line_of_invisible_characters_is_still_bounded()
    {
        var folded = ServerText.SingleLine(new string('​', 10_000) + "tail");

        // Every kept character came from the first MaxAdoptedLength, all of which fold away.
        Assert.Null(folded);
    }

    // --- What the fold deliberately keeps -------------------------------------------------------

    // Not every invisible character is a trick. A zero-width joiner is what binds an emoji sequence
    // into a single glyph, so dropping it turns one picture into several and mangles copy that was
    // doing nothing wrong. It cannot reorder or conceal anything, so there is nothing to be safe from.
    [Fact]
    public void A_zero_width_joiner_survives_the_fold()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

        Assert.Equal(family, ServerText.SingleLine(family));
    }

    // The zero-width non-joiner is required orthography in Persian and Arabic rather than decoration,
    // so dropping it would misspell words in the only scripts that need it.
    [Fact]
    public void A_zero_width_non_joiner_survives_the_fold()
    {
        Assert.Equal("a\u200Cb", ServerText.SingleLine("a\u200Cb"));
    }

    // The two kept characters are kept for their meaning, not by accident of category — a string of
    // them alone still says nothing a reader can see, but it is no longer this method's to discard.
    [Fact]
    public void The_kept_characters_do_not_reopen_the_reordering_hole()
    {
        // A joiner between two letters is kept; an override in the same position is not.
        Assert.Equal("a\u200Db", ServerText.SingleLine("a\u200Db"));
        Assert.Equal("ab", ServerText.SingleLine("a\u202Eb"));
    }
}
