using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Collectors;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Pins the exact set of collections that upload as soon as the game announces a new entry, the set
/// that lets the sync card promise uploads "within seconds".
/// </summary>
/// <remarks>
/// What the flag means, and why being registered for the unlock signal is not enough, is documented
/// on <see cref="CategoryInfo.UploadsOnUnlock"/>. The categories are read through
/// <see cref="CategoryInfoReflection"/>.
/// </remarks>
// Each `[Fact]` is one test, like `it(...)` in Jest.
public class UnlockUploadDeclarationTests
{
    /// <summary>The collections the game announces the moment a new entry is earned.</summary>
    // `static readonly` builds the value once, before the class is first used, and never reassigns
    // it. `new() { a, b }` builds the set with these entries, like `new Set([a, b])`.
    private static readonly HashSet<string> Announced = new()
    {
        CategoryKeys.Achievements,
        CategoryKeys.Minions,
        CategoryKeys.Mounts,
        CategoryKeys.OrchestrionRolls,
        CategoryKeys.Quests,
    };

    // Comparing the whole set fails both ways: a collection that stops declaring, and one that starts,
    // such as one copied from an announced entry with the flag left on. `.Where` and `.Select` work
    // like the array methods `filter` and `map`; `.ToHashSet()` copies the keys into a set, so the
    // comparison ignores order.
    [Fact]
    public void Exactly_the_announced_collections_upload_on_unlock()
    {
        var actual = CategoryInfoReflection.All()
            .Where(info => info.UploadsOnUnlock)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(Announced, actual);
    }

    // Triple Triad cards are the case CategoryInfo.UploadsOnUnlock names, so they must not claim to
    // upload on unlock. `.Single(...)` returns the one item that matches, and throws unless exactly one
    // does.
    [Fact]
    public void Triple_triad_cards_do_not_claim_to_upload_on_unlock()
    {
        var cards = CategoryInfoReflection.All().Single(info => info.Key == CategoryKeys.TripleTriadCards);

        Assert.False(cards.UploadsOnUnlock);
    }
}
