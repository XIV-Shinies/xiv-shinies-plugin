using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The results screen (XBMResult): the run's score, its bonuses, its rank, and what each familiar
// brought gained.
public class CrucibleResultsTests
{
    // Positions in the screen's values that the tests below change.
    private const int Mode = 1;
    private const int BaseScore = 2;
    private const int PerformanceText = 3;
    private const int PerformancePoints = 4;
    private const int EnemiesText = 5;
    private const int EnemyPoints = 6;
    private const int ElitesText = 7;
    private const int ElitePoints = 8;
    private const int BossesText = 9;
    private const int BossPoints = 10;
    private const int RemainingHp = 11;
    private const int BonusTotal = 13;
    private const int BonusCount = 14;
    private const int FirstBonusPoints = 31;
    private const int TotalScore = 68;
    private const int Rank = 69;
    private const int FamiliarCount = 72;

    // The first familiar's place in each of the five familiar runs, and the second familiar's icon.
    private const int FirstFamiliarIcon = 73;
    private const int SecondFamiliarIcon = 74;
    private const int FirstExpBefore = 89;
    private const int FirstExpAfter = 105;
    private const int FirstRankBefore = 121;
    private const int FirstRankAfter = 137;

    // Board 1's results screen: one bonus, eight familiars brought. Records compare by their
    // values, so each `Assert.Equal` on a record checks every field.
    [Fact]
    public void Reads_a_board_one_results_screen()
    {
        var reading = CrucibleResults.Read(WindowFixture.Load("result-board1-willing-sacrifice.json"));

        Assert.NotNull(reading);
        Assert.Equal("Standard", reading.ModeText);
        Assert.Equal(7375, reading.BaseScore);
        Assert.Equal(100, reading.PerformancePercent);
        Assert.Equal(5000, reading.PerformancePoints);
        Assert.Equal(new CrucibleScoreLine(3, 750), reading.Enemies);
        Assert.Equal(new CrucibleScoreLine(1, 625), reading.Elites);
        Assert.Equal(new CrucibleScoreLine(1, 1000), reading.Bosses);
        Assert.Equal(7, reading.RemainingHp);
        Assert.Equal(250, reading.BonusTotal);
        Assert.Equal(7625, reading.TotalScore);
        Assert.Equal("Exemplary", reading.RankText);
        Assert.Equal(new[] { new CrucibleResultBonus("Willing Sacrifice I", 250) }, reading.Bonuses);

        Assert.Equal(8, reading.Familiars.Count);
        Assert.Equal(new CrucibleResultFamiliar(16, 9, 9, 20, 41), reading.Familiars[0]);
    }

    // A familiar that ranked up reads both ranks and its EXP toward the next rank before and after
    // the run. The `!` after `reading` tells the compiler it is not null here, like TypeScript's
    // `!`; `Single` returns the one familiar that matches and throws unless exactly one does.
    [Fact]
    public void A_familiar_that_ranked_up_reads_both_ranks()
    {
        var reading = CrucibleResults.Read(WindowFixture.Load("result-board1-willing-sacrifice.json"));

        Assert.Equal(
            new CrucibleResultFamiliar(40, 1, 3, 0, 55),
            reading!.Familiars.Single(familiar => familiar.PetId == 40));
    }

    // Board 2's results screen, twelve familiars brought.
    [Fact]
    public void Reads_a_board_two_results_screen()
    {
        var reading = CrucibleResults.Read(WindowFixture.Load("result-board2.json"));

        Assert.NotNull(reading);
        Assert.Equal(12, reading.Familiars.Count);
        Assert.Equal(new CrucibleResultFamiliar(8, 7, 8, 65, 62), reading.Familiars[0]);
        Assert.Equal(462, reading.RemainingHp);
        Assert.Equal(7875, reading.TotalScore);
    }

    // A familiar's id is its icon minus 242000, and item icons start at 243000; an icon at or below
    // 242000, or at or above 243000, is not a familiar's.
    [Theory]
    [InlineData(43u)]
    [InlineData(242000u)]
    [InlineData(243000u)]
    public void A_familiar_icon_outside_the_familiar_range_refuses_the_reading(uint icon)
    {
        var values = WindowFixture.Load("result-board2.json");
        values[FirstFamiliarIcon] = AddonValue.FromInteger(icon);

        Assert.Null(CrucibleResults.Read(values));
    }

    // No familiar is brought twice.
    [Fact]
    public void A_familiar_listed_twice_refuses_the_reading()
    {
        var values = WindowFixture.Load("result-board2.json");
        values[SecondFamiliarIcon] = values[FirstFamiliarIcon];

        Assert.Null(CrucibleResults.Read(values));
    }

    // A rank does not fall at the end of a run.
    [Fact]
    public void A_rank_that_falls_refuses_the_reading()
    {
        var values = WindowFixture.Load("result-board2.json");
        values[FirstRankAfter] = AddonValue.FromInteger(6);

        Assert.Null(CrucibleResults.Read(values));
    }

    // The window has sixteen places each for bonuses and familiars.
    [Theory]
    [InlineData(BonusCount)]
    [InlineData(FamiliarCount)]
    public void A_count_beyond_its_sixteen_places_refuses_the_reading(int countIndex)
    {
        var values = WindowFixture.Load("result-board2.json");
        values[countIndex] = AddonValue.FromInteger(17);

        Assert.Null(CrucibleResults.Read(values));
    }

    // A bonus counted but without a name is a misaligned list.
    [Fact]
    public void A_counted_bonus_without_a_name_refuses_the_reading()
    {
        var values = WindowFixture.Load("result-board2.json");
        values[BonusCount] = AddonValue.FromInteger(2);

        Assert.Null(CrucibleResults.Read(values));
    }

    // Every value the screen is read for has to be there; one missing means the layout has moved.
    [Theory]
    [InlineData(Mode)]
    [InlineData(BaseScore)]
    [InlineData(PerformanceText)]
    [InlineData(PerformancePoints)]
    [InlineData(EnemiesText)]
    [InlineData(EnemyPoints)]
    [InlineData(ElitesText)]
    [InlineData(ElitePoints)]
    [InlineData(BossesText)]
    [InlineData(BossPoints)]
    [InlineData(RemainingHp)]
    [InlineData(BonusTotal)]
    [InlineData(TotalScore)]
    [InlineData(Rank)]
    [InlineData(FirstBonusPoints)]
    [InlineData(FirstFamiliarIcon)]
    [InlineData(FirstExpBefore)]
    [InlineData(FirstExpAfter)]
    [InlineData(FirstRankBefore)]
    [InlineData(FirstRankAfter)]
    public void A_missing_value_refuses_the_reading(int index)
    {
        var values = WindowFixture.Load("result-board2.json");
        values[index] = AddonValue.Unreadable;

        Assert.Null(CrucibleResults.Read(values));
    }

    // A number too large for an int is refused rather than turned negative, whichever field it is.
    [Theory]
    [InlineData(TotalScore)]
    [InlineData(FirstExpAfter)]
    public void A_number_too_large_for_an_int_refuses_the_reading(int index)
    {
        var values = WindowFixture.Load("result-board2.json");
        values[index] = AddonValue.FromInteger(uint.MaxValue);

        Assert.Null(CrucibleResults.Read(values));
    }

    [Fact]
    public void A_rank_with_no_text_refuses_the_reading()
    {
        var values = WindowFixture.Load("result-board2.json");
        values[Rank] = AddonValue.FromText("");

        Assert.Null(CrucibleResults.Read(values));
    }

    [Fact]
    public void A_short_value_list_refuses_the_reading()
    {
        Assert.Null(CrucibleResults.Read(WindowFixture.Load("result-board2.json").GetRange(0, 100)));
    }
}
