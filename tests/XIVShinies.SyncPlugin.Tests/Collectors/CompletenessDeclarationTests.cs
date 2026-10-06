using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Collectors;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Pins the exact set of collections permitted to declare a complete enumeration.
/// </summary>
/// <remarks>
/// <para>
/// What the claim licenses, and the condition a collection must meet to make it, are documented on
/// <see cref="CategoryInfo.EnumeratesCompleteDomain"/>.
/// </para>
/// <para>
/// The flag is reachable from a unit test through <see cref="CategoryInfoReflection"/>.
/// </para>
/// </remarks>
public class CompletenessDeclarationTests
{
    /// <summary>
    /// The collections entitled to declare a complete enumeration, each because its collector gets
    /// an answer for every candidate the server's catalog may hold. The reasoning for each lives
    /// beside its <see cref="CategoryInfo"/> in <see cref="CollectorRegistry"/>.
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

    /// <summary>Every category the registry declares (see <see cref="CategoryInfoReflection"/>).</summary>
    private static IReadOnlyList<CategoryInfo> AllCategories() => CategoryInfoReflection.All();

    /// <summary>
    /// Asserts the whole declaring set at once.
    /// </summary>
    /// <remarks>
    /// Comparing the whole set is what makes this fail in both directions: a category that stops
    /// declaring, and a new one that starts — typically one copied from an existing entry with
    /// <c>EnumeratesCompleteDomain = true</c> left intact.
    /// </remarks>
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
    /// Guards the reflection itself. The set comparison above only sees categories the field scan
    /// found, so a <see cref="CategoryInfo"/> stored somewhere the scan misses — a property, a
    /// nested type — is invisible to it, and invisible in the safe-looking direction: a category
    /// that does not declare simply never shows up. Comparing against the declared keys catches it.
    /// </summary>
    [Fact]
    public void Every_registered_category_is_reachable_by_reflection()
    {
        var found = AllCategories().Select(info => info.Key).ToHashSet();

        Assert.Equal(CategoryKeyReflection.All().ToHashSet(), found);
    }

    /// <summary>
    /// The collections that must never declare completeness, named individually so the reason each
    /// one withholds stays attached to the category rather than living only in the set above.
    /// </summary>
    /// <remarks>
    /// <c>tripleTriadNpcs</c> withholds for the reason recorded beside its
    /// <see cref="CategoryInfo"/> in <see cref="CollectorRegistry"/>. The other three never reach
    /// a factory that takes the claim at all, which is why they cannot declare — see
    /// <see cref="CollectResult.CompleteEnumeration"/>.
    /// </remarks>
    [Theory]
    [InlineData(CategoryKeys.TripleTriadNpcs)]
    [InlineData(CategoryKeys.Items)]
    [InlineData(CategoryKeys.QuestSequences)]
    [InlineData(CategoryKeys.OccultProgression)]
    public void A_category_that_cannot_see_its_whole_domain_withholds_the_claim(string categoryKey)
    {
        var info = AllCategories().Single(candidate => candidate.Key == categoryKey);

        Assert.False(info.EnumeratesCompleteDomain);
    }
}
