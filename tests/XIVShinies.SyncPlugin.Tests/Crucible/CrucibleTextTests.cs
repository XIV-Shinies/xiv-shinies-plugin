using Xunit;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The Crucible windows draw numbers as text: token balances with thousands separators, prices with
// a discount note, HP as "current/max", ranks behind a glyph. These pin how each is read, from the
// raw text the client hands over, formatting codes included.
//
// Characters outside ASCII are written as `\uXXXX` escapes so each one can be told apart in
// source: \u00A0 is a no-break space, \u202F a narrow no-break space, \uE036 the paw glyph,
// \uE0BC the rank-sync glyph, \uFFFD the replacement character UTF-8 decoding leaves inside a
// formatting code's payload, and \u00D7 a multiplication sign.
public class CrucibleTextTests
{
    // A `[Theory]` runs once per `[InlineData]` row, with the row's values as the arguments, like
    // `test.each` in Jest.
    //
    // A formatting code runs from \u0002 to the next \u0003. The first row is the game's color wrap
    // around an item's name; each code's payload holds \u0002 bytes, which do not start a new code.
    [Theory]
    [InlineData(
        "\u0002H\u0004\uFFFD\u0002%\u0003\u0002I\u0004\uFFFD\u0002&\u0003Faded Remnant"
            + "\u0002I\u0002\u0001\u0003\u0002H\u0002\u0001\u0003",
        "Faded Remnant")]
    [InlineData("Standard", "Standard")]
    [InlineData("", "")]
    public void Formatting_codes_are_removed(string raw, string expected)
    {
        Assert.Equal(expected, CrucibleText.StripCodes(raw));
    }

    // A `[Fact]` is a single test with no arguments, like a plain `test(...)` in Jest.
    //
    // A code with no end is cut at its start rather than kept, so its payload never reads as text.
    [Fact]
    public void An_unterminated_code_is_cut_at_its_start()
    {
        Assert.Equal("Gil", CrucibleText.StripCodes("Gil\u0002H\u0004"));
    }

    [Theory]
    [InlineData("764", 764)]
    [InlineData("1,478", 1478)]
    [InlineData("1.478", 1478)]
    [InlineData("1 478", 1478)]
    [InlineData("1\u00A0478", 1478)]
    [InlineData("1\u202F478", 1478)]
    [InlineData("25 (-50%)", 25)]
    [InlineData("  47 (-50%)", 47)]
    [InlineData("\u0002H\u0004\uFFFD\u0002%\u0003125\u0002H\u0002\u0001\u0003", 125)]
    [InlineData("0", 0)]
    public void A_leading_number_is_read_through_its_separators(string text, int expected)
    {
        Assert.Equal(expected, CrucibleText.ReadNumber(text));
    }

    // Nothing to read, or text that does not start with a number, reads as no number, not zero.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Crucible Item 3")]
    [InlineData(",")]
    public void Text_without_a_leading_number_reads_as_none(string? text)
    {
        Assert.Null(CrucibleText.ReadNumber(text));
    }

    // A number too long for an int reads as none rather than wrapping.
    [Fact]
    public void A_number_too_long_for_an_int_reads_as_none()
    {
        Assert.Null(CrucibleText.ReadNumber("99999999999"));
    }

    // A count drawn after a sign ("x 3") is found wherever its first digit is.
    [Theory]
    [InlineData("x 3", 3)]
    [InlineData("\u00D712", 12)]
    [InlineData("100%", 100)]
    [InlineData("x 1,250", 1250)]
    public void The_first_number_is_found_wherever_it_starts(string text, int expected)
    {
        Assert.Equal(expected, CrucibleText.ReadFirstNumber(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("x")]
    [InlineData("")]
    public void Text_with_no_digit_has_no_first_number(string? text)
    {
        Assert.Null(CrucibleText.ReadFirstNumber(text));
    }

    [Theory]
    [InlineData("1118/1118", 1118, 1118)]
    [InlineData("0/667", 0, 667)]
    [InlineData("1,011/1,011", 1011, 1011)]
    public void A_fraction_reads_as_its_two_numbers(string text, int current, int max)
    {
        Assert.Equal((current, max), CrucibleText.ReadFraction(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1118")]
    [InlineData("a/b")]
    [InlineData("1/2/3")]
    public void Text_that_is_not_a_fraction_reads_as_none(string? text)
    {
        Assert.Null(CrucibleText.ReadFraction(text));
    }

    // The rank label is a paw glyph and the digits, behind a sync glyph when the board has capped
    // the familiar's rank.
    [Theory]
    [InlineData("\uE0369", 9, false)]
    [InlineData("\uE0BC\uE0365", 5, true)]
    [InlineData("\uE03612", 12, false)]
    public void A_rank_label_reads_as_its_rank_and_whether_it_is_capped(
        string label, int rank, bool synced)
    {
        Assert.Equal((rank, synced), CrucibleText.ReadRank(label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\uE036")]
    public void A_rank_label_without_digits_reads_as_none(string? label)
    {
        Assert.Null(CrucibleText.ReadRank(label));
    }
}
