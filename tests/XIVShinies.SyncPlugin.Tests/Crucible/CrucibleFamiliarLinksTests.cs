using Xunit;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The game's record of the player's pet names a familiar by its Pet sheet row; the familiars snapshot
// names it by its XBMPet row, as the team window does, through the link the XBMPet sheet holds. Each
// `[Fact]` is one test, like `it(...)` in Jest.
public class CrucibleFamiliarLinksTests
{
    /// <summary>The links for three familiars, as the game's sheet holds them.</summary>
    // `[(1u, 47), ...]` is a collection expression of (XBMPet row, Pet row) pairs; the `u` suffix makes
    // a number a `uint`. `!` tells the compiler From returned a map here, which the test relies on.
    private static CrucibleFamiliarLinks Links() =>
        CrucibleFamiliarLinks.From([(1u, 47), (5u, 52), (13u, 60)])!;

    // A Pet row a familiar links names that familiar; one no familiar links names none.
    [Fact]
    public void A_pet_row_maps_to_the_familiar_that_links_it()
    {
        var links = Links();

        Assert.Equal(5u, links.PetIdFor(52));
        Assert.Equal(13u, links.PetIdFor(60));
        Assert.Null(links.PetIdFor(48));
    }

    // Every familiar has its own Pet row, so a column that repeats one is not the link, and nothing
    // read from it is trusted.
    [Fact]
    public void A_column_that_repeats_a_pet_row_is_refused()
    {
        Assert.Null(CrucibleFamiliarLinks.From([(1u, 47), (2u, 48), (3u, 47)]));
    }

    // Row 0 is the sheet's blank padding, and a link of 0, or one no row id can be, is an empty one;
    // none of them names a familiar.
    [Fact]
    public void Blank_rows_and_empty_links_are_skipped()
    {
        var links = CrucibleFamiliarLinks.From(
            [(0u, 47), (1u, 48), (2u, 0), (3u, -5), (4u, 5_000_000_000)]);

        // `Assert.NotNull` fails the test on null, and lets the compiler treat `links` as set after it.
        Assert.NotNull(links);
        Assert.Null(links.PetIdFor(47));
        Assert.Equal(1u, links.PetIdFor(48));
    }

    // A column with no links at all cannot be the link column either.
    [Fact]
    public void A_sheet_with_no_links_is_refused()
    {
        Assert.Null(CrucibleFamiliarLinks.From([(0u, 0), (1u, 0)]));
    }

    // A full reading of a linked pet names the familiar with its HP.
    [Fact]
    public void A_linked_pet_with_hp_is_the_familiar_out()
    {
        Assert.Equal(new CrucibleSummoned(13, 358, 560), Links().Summoned(60, 358, 560));
    }

    // An empty record, a record at 0 HP, a reading the game never draws, and a pet no familiar links
    // are no reading of a Crucible familiar, so each gives none. A `[Theory]` runs once per
    // `[InlineData]` row, like `it.each`.
    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(60u, 0u, 0u)]
    [InlineData(60u, 0u, 560u)]
    [InlineData(60u, 600u, 560u)]
    [InlineData(48u, 300u, 560u)]
    [InlineData(0u, 300u, 560u)]
    public void A_record_with_no_reading_of_a_familiar_is_none(uint petRow, uint currentHp, uint maxHp)
    {
        Assert.Null(Links().Summoned(petRow, currentHp, maxHp));
    }
}
