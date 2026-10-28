using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Collectors;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>Pins the exact set of collections permitted to declare a complete enumeration.</summary>
/// <remarks>
/// <see cref="CategoryInfo.EnumeratesCompleteDomain"/> documents what the claim licenses and when a
/// collection may make it.
/// </remarks>
public class CompletenessDeclarationTests
{
    /// <summary>
    /// The collections whose collector gets an answer for every candidate the server's catalog may
    /// hold. Each one's reasoning sits beside its <see cref="CategoryInfo"/> in
    /// <see cref="CollectorRegistry"/>.
    /// </summary>
    private static readonly HashSet<string> Declaring = new()
    {
        CategoryKeys.Achievements,
        CategoryKeys.Minions,
        CategoryKeys.Mounts,
        CategoryKeys.OccultRecords,
        CategoryKeys.OrchestrionRolls,
        CategoryKeys.Quests,
        CategoryKeys.TamedBeasts,
        CategoryKeys.TripleTriadCards,
    };

    private static IReadOnlyList<CategoryInfo> AllCategories() => CategoryInfoReflection.All();

    /// <summary>
    /// Comparing the whole set fails in both directions: a category that stops declaring, and a new one
    /// that starts, typically copied from an entry with <c>EnumeratesCompleteDomain = true</c> left in.
    /// </summary>
    [Fact]
    public void Exactly_the_expected_categories_declare_a_complete_enumeration()
    {
        var actual = AllCategories()
            .Where(info => info.EnumeratesCompleteDomain)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(Declaring, actual);
    }

    /// <summary>
    /// Guards the reflection. A <see cref="CategoryInfo"/> the field scan misses, such as one held in a
    /// property, would pass the set test unnoticed, because a missing category looks like one that
    /// does not declare.
    /// </summary>
    [Fact]
    public void Every_registered_category_is_reachable_by_reflection()
    {
        var found = AllCategories().Select(info => info.Key).ToHashSet();

        Assert.Equal(CategoryKeyReflection.All().ToHashSet(), found);
    }

    /// <summary>
    /// The collections that must never declare, named one by one so each reason stays attached to its
    /// category.
    /// </summary>
    /// <remarks>
    /// <c>tripleTriadNpcs</c> withholds for the reason beside its <see cref="CategoryInfo"/> in
    /// <see cref="CollectorRegistry"/>. The others never reach a factory that takes the claim; see
    /// <see cref="CollectResult.CompleteEnumeration"/>.
    /// </remarks>
    [Theory]
    [InlineData(CategoryKeys.TripleTriadNpcs)]
    [InlineData(CategoryKeys.Items)]
    [InlineData(CategoryKeys.QuestSequences)]
    [InlineData(CategoryKeys.OccultProgression)]
    [InlineData(CategoryKeys.AchievementProgress)]
    public void A_category_that_cannot_see_its_whole_domain_withholds_the_claim(string categoryKey)
    {
        var info = AllCategories().Single(candidate => candidate.Key == categoryKey);

        Assert.False(info.EnumeratesCompleteDomain);
    }
}
