using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>One counted line of the score: how many the run beat, and the points they paid.</summary>
public sealed record CrucibleScoreLine(int Count, int Points);

/// <summary>One bonus the run earned, as the results screen drew it.</summary>
/// <param name="Name">The bonus's name in the client's language, formatting codes removed.</param>
public sealed record CrucibleResultBonus(string Name, int Points);

/// <summary>What one familiar the run brought gained.</summary>
/// <param name="PetId">The familiar's <c>XBMPet</c> row id.</param>
/// <param name="RankBefore">Its true rank before the run, not the board's cap.</param>
/// <param name="RankAfter">Its true rank after the run.</param>
/// <param name="ExpBefore">Its EXP toward the next rank before the run.</param>
/// <param name="ExpAfter">Its EXP toward the next rank after the run.</param>
public sealed record CrucibleResultFamiliar(
    uint PetId,
    int RankBefore,
    int RankAfter,
    int ExpBefore,
    int ExpAfter);

/// <summary>What the results screen showed at the end of a run.</summary>
/// <param name="ModeText">
/// The board's mode as drawn, such as "Standard" at Degree 0, formatting codes removed.
/// </param>
/// <param name="BaseScore">The score before bonuses.</param>
/// <param name="PerformancePercent">The performance share, as a percentage.</param>
/// <param name="RemainingHp">
/// The HP the run ended on. The points the screen's HP line pays are part of the base score, a
/// separate figure.
/// </param>
/// <param name="RankText">The run's rank as drawn, such as "Exemplary", formatting codes removed.</param>
/// <param name="Bonuses">The bonuses earned, in the screen's order.</param>
/// <param name="Familiars">
/// The familiars brought, in the screen's order; one left behind is not listed.
/// </param>
public sealed record CrucibleResultsReading(
    string ModeText,
    int BaseScore,
    int PerformancePercent,
    int PerformancePoints,
    CrucibleScoreLine Enemies,
    CrucibleScoreLine Elites,
    CrucibleScoreLine Bosses,
    int RemainingHp,
    int BonusTotal,
    int TotalScore,
    string RankText,
    IReadOnlyList<CrucibleResultBonus> Bonuses,
    IReadOnlyList<CrucibleResultFamiliar> Familiars);

/// <summary>Reads the results screen (<c>XBMResult</c>) from the values the window was given.</summary>
/// <remarks>
/// The screen carries the score at fixed places, with the performance share and counted lines drawn
/// as text such as "100%" and "x 3"; then a bonus count with names and points in two runs of sixteen
/// places, the total and the rank; then a familiar count with five runs of sixteen places: icon, EXP
/// before and after, rank before and after. The mode, rank and bonus names stay as the text drawn,
/// in the client's language, for <see cref="CrucibleNames"/> to resolve to ids.
/// </remarks>
public static class CrucibleResults
{
    // The score.
    private const int ModeIndex = 1;
    private const int BaseScoreIndex = 2;
    private const int PerformanceTextIndex = 3;
    private const int PerformancePointsIndex = 4;
    private const int EnemiesTextIndex = 5;
    private const int EnemyPointsIndex = 6;
    private const int ElitesTextIndex = 7;
    private const int ElitePointsIndex = 8;
    private const int BossesTextIndex = 9;
    private const int BossPointsIndex = 10;
    private const int RemainingHpIndex = 11;
    private const int BonusTotalIndex = 13;

    // The bonuses.
    private const int BonusCountIndex = 14;
    private const int FirstBonusName = 15;
    private const int FirstBonusPoints = 31;

    // The final score and its rank.
    private const int TotalScoreIndex = 68;
    private const int RankIndex = 69;

    // The familiars brought.
    private const int FamiliarCountIndex = 72;
    private const int FirstFamiliarIcon = 73;
    private const int FirstExpBefore = 89;
    private const int FirstExpAfter = 105;
    private const int FirstRankBefore = 121;
    private const int FirstRankAfter = 137;

    /// <summary>How many places the screen has for bonuses, and for familiars.</summary>
    private const int Places = 16;

    /// <summary>A familiar's icon is this number plus its <c>XBMPet</c> id.</summary>
    private const uint IconBase = 242000;

    /// <summary>Where item icons start.</summary>
    private const uint ItemIconBase = 243000;

    /// <summary>The highest position the layout reads: the last familiar place's rank after.</summary>
    private const int LastIndex = FirstRankAfter + Places - 1;

    /// <summary>
    /// Reads the results screen, or null when the values are not the layout this reader knows.
    /// </summary>
    public static CrucibleResultsReading? Read(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= LastIndex)
            return null;

        var mode = ReadText(values, ModeIndex);
        var rankText = ReadText(values, RankIndex);
        if (mode is null || rankText is null)
            return null;

        if (ReadInt(values, BaseScoreIndex) is not { } baseScore
            || CrucibleText.ReadFirstNumber(values[PerformanceTextIndex].Text) is not { } percent
            || ReadInt(values, PerformancePointsIndex) is not { } performancePoints
            || ReadLine(values, EnemiesTextIndex, EnemyPointsIndex) is not { } enemies
            || ReadLine(values, ElitesTextIndex, ElitePointsIndex) is not { } elites
            || ReadLine(values, BossesTextIndex, BossPointsIndex) is not { } bosses
            || ReadInt(values, RemainingHpIndex) is not { } remainingHp
            || ReadInt(values, BonusTotalIndex) is not { } bonusTotal
            || ReadInt(values, TotalScoreIndex) is not { } totalScore)
        {
            return null;
        }

        var bonuses = ReadBonuses(values);
        var familiars = ReadFamiliars(values);
        if (bonuses is null || familiars is null)
            return null;

        return new CrucibleResultsReading(
            mode,
            baseScore,
            percent,
            performancePoints,
            enemies,
            elites,
            bosses,
            remainingHp,
            bonusTotal,
            totalScore,
            rankText,
            bonuses,
            familiars);
    }

    /// <summary>A counted line: its count from text such as "x 3", and its points.</summary>
    private static CrucibleScoreLine? ReadLine(
        IReadOnlyList<AddonValue> values, int textIndex, int pointsIndex)
    {
        if (CrucibleText.ReadFirstNumber(values[textIndex].Text) is not { } count
            || ReadInt(values, pointsIndex) is not { } points)
        {
            return null;
        }

        return new CrucibleScoreLine(count, points);
    }

    /// <summary>The bonuses earned, or null when the count or a counted place is wrong.</summary>
    private static List<CrucibleResultBonus>? ReadBonuses(IReadOnlyList<AddonValue> values)
    {
        if (values[BonusCountIndex].Number is not { } count || count > Places)
            return null;

        var bonuses = new List<CrucibleResultBonus>((int)count);
        for (var place = 0; place < count; place++)
        {
            if (ReadText(values, FirstBonusName + place) is not { } name
                || ReadInt(values, FirstBonusPoints + place) is not { } points)
            {
                return null;
            }

            bonuses.Add(new CrucibleResultBonus(name, points));
        }

        return bonuses;
    }

    /// <summary>The familiars brought, or null when the count or a counted place is wrong.</summary>
    private static List<CrucibleResultFamiliar>? ReadFamiliars(IReadOnlyList<AddonValue> values)
    {
        if (values[FamiliarCountIndex].Number is not { } count || count > Places)
            return null;

        var familiars = new List<CrucibleResultFamiliar>((int)count);
        var seen = new HashSet<uint>();
        for (var place = 0; place < count; place++)
        {
            // A familiar's icon sits between the two icon bases; anything else is not a familiar's.
            if (values[FirstFamiliarIcon + place].Number is not { } icon
                || icon <= IconBase
                || icon >= ItemIconBase
                || ReadInt(values, FirstExpBefore + place) is not { } expBefore
                || ReadInt(values, FirstExpAfter + place) is not { } expAfter
                || ReadInt(values, FirstRankBefore + place) is not { } rankBefore
                || ReadInt(values, FirstRankAfter + place) is not { } rankAfter)
            {
                return null;
            }

            // A rank never falls over a run, and no familiar is brought twice.
            var petId = icon - IconBase;
            if (rankAfter < rankBefore || !seen.Add(petId))
                return null;

            familiars.Add(new CrucibleResultFamiliar(petId, rankBefore, rankAfter, expBefore, expAfter));
        }

        return familiars;
    }

    /// <summary>
    /// The number at a place as an <c>int</c>, or null when there is none or it is above
    /// <c>int.MaxValue</c>, which a cast would turn negative.
    /// </summary>
    private static int? ReadInt(IReadOnlyList<AddonValue> values, int index) =>
        values[index].Number is { } number && number <= int.MaxValue ? (int)number : null;

    /// <summary>The text at a place with formatting codes removed, or null when there is none.</summary>
    private static string? ReadText(IReadOnlyList<AddonValue> values, int index)
    {
        if (values[index].Text is not { } raw)
            return null;

        var text = CrucibleText.StripCodes(raw);
        return text.Length > 0 ? text : null;
    }
}
