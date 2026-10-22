using Xunit;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// Turning the results screen's drawn text into the ids the contract sends: the board's Degree, the
// run's rank, and each bonus. The three row sets below are the game's own, in the English client.
public class CrucibleNamesTests
{
    /// <summary>The four Degree names, as the <c>Addon</c> sheet holds them.</summary>
    private static readonly CrucibleSheetText[] DegreeRows =
    [
        new(17870, "Standard"),
        new(17871, "First Degree"),
        new(17872, "Second Degree"),
        new(17873, "Third Degree"),
    ];

    /// <summary>The nine rank names, as <c>XBMScoreRank</c> holds them: highest first.</summary>
    private static readonly CrucibleSheetText[] RankRows =
    [
        new(0, "Legendary"),
        new(1, "Apex"),
        new(2, "Elite"),
        new(3, "Renowned"),
        new(4, "Exemplary"),
        new(5, "Adept"),
        new(6, "Journeyman"),
        new(7, "Novice"),
        new(8, "Apprentice"),
    ];

    /// <summary>A few bonus names, as <c>XBMScoreBonus</c> holds them, tier included.</summary>
    private static readonly CrucibleSheetText[] BonusRows =
    [
        new(0, "Willing Sacrifice I"),
        new(1, "Willing Sacrifice II"),
        new(2, "Willing Sacrifice III"),
        new(3, "No Beast Left Behind"),
        new(30, "First Degree"),
    ];

    private static CrucibleNames Names() => new(DegreeRows, RankRows, BonusRows);

    // The Degree is the row's place after the first Degree row: Standard is 0.
    [Theory]
    [InlineData("Standard", 0)]
    [InlineData("First Degree", 1)]
    [InlineData("Third Degree", 3)]
    public void A_drawn_mode_resolves_to_its_degree(string drawn, int degree)
    {
        Assert.Equal(degree, Names().Degree(drawn));
    }

    // The sheet lists ranks highest first and the contract counts from the lowest, so the sheet's
    // last row is rank 0.
    [Theory]
    [InlineData("Apprentice", 0)]
    [InlineData("Exemplary", 4)]
    [InlineData("Legendary", 8)]
    public void A_drawn_rank_resolves_to_its_index_counted_from_the_lowest(string drawn, int rankIndex)
    {
        Assert.Equal(rankIndex, Names().RankIndex(drawn));
    }

    // A bonus is its row, and the tier is part of the name, so each tier is its own row.
    [Theory]
    [InlineData("Willing Sacrifice I", 0u)]
    [InlineData("Willing Sacrifice III", 2u)]
    [InlineData("No Beast Left Behind", 3u)]
    public void A_drawn_bonus_resolves_to_its_row(string drawn, uint bonusId)
    {
        Assert.Equal(bonusId, Names().BonusId(drawn));
    }

    [Fact]
    public void Each_name_resolves_only_against_its_own_sheet()
    {
        var names = Names();

        Assert.Equal(1, names.Degree("First Degree"));
        Assert.Equal(30u, names.BonusId("First Degree"));
        Assert.Null(names.RankIndex("First Degree"));
        Assert.Null(names.Degree("Exemplary"));
    }

    // Matching is exact, so a near match, such as a different case or a missing tier, resolves to nothing.
    [Fact]
    public void Text_no_sheet_holds_resolves_to_nothing()
    {
        var names = Names();

        Assert.Null(names.Degree("Fourth Degree"));
        Assert.Null(names.RankIndex("exemplary"));
        Assert.Null(names.BonusId("Willing Sacrifice"));
    }

    // A row outside the ranges would turn into a Degree or rank that does not exist.
    [Fact]
    public void Rows_outside_the_degree_and_rank_ranges_are_not_read()
    {
        var names = new CrucibleNames(
            [new(17869, "Before"), new(17874, "After")],
            [new(9, "Mythic")],
            []);

        Assert.Null(names.Degree("Before"));
        Assert.Null(names.Degree("After"));
        Assert.Null(names.RankIndex("Mythic"));
    }

    // The range is applied first, so the outside row never makes the name ambiguous.
    [Fact]
    public void A_row_outside_the_range_does_not_share_a_name_with_one_inside_it()
    {
        var names = new CrucibleNames([new(17000, "Standard"), new(17870, "Standard")], [], []);

        Assert.Equal(0, names.Degree("Standard"));
    }

    // Each such row could be the one drawn, so the name resolves to none of them, however many.
    [Fact]
    public void A_name_several_rows_share_resolves_to_nothing()
    {
        var names = new CrucibleNames(
            [], [], [new(5, "Twin"), new(6, "Twin"), new(7, "Single"), new(8, "Twin")]);

        Assert.Null(names.BonusId("Twin"));
        Assert.Equal(7u, names.BonusId("Single"));
    }

    // A row of only formatting codes names nothing either. `\u0002` and `\u0003` open and close a
    // formatting code.
    [Fact]
    public void A_row_with_no_text_names_nothing()
    {
        var names = new CrucibleNames([], [], [new(9, ""), new(10, "\u0002H\u0003"), new(11, "Selfless")]);

        Assert.Null(names.BonusId(""));
        Assert.Equal(11u, names.BonusId("Selfless"));
    }

    // The sheet's text is compared as the window's is read: formatting codes removed, ends trimmed.
    [Fact]
    public void Sheet_text_is_compared_without_its_formatting_codes()
    {
        var names = new CrucibleNames([], [], [new(4, " \u0002H\u0003Beast Squad I ")]);

        Assert.Equal(4u, names.BonusId("Beast Squad I"));
    }

    [Fact]
    public void No_names_resolve_nothing()
    {
        Assert.Null(CrucibleNames.None.Degree("Standard"));
        Assert.Null(CrucibleNames.None.RankIndex("Exemplary"));
        Assert.Null(CrucibleNames.None.BonusId("Willing Sacrifice I"));
    }
}
