using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The three windows that offer items: a treasure coffer (XBMContentsTreasure), a fight's loot
// (XBMContentsBooty) and a shop (XBMContentsItemShop).
public class CrucibleOffersTests
{
    // Positions in the treasure and loot windows' values that the tests below change.
    private const int ChestBalance = 2;
    private const int TreasureFirstShown = 3;
    private const int TreasureFirstId = 6;
    private const int LootEarned = 4;
    private const int LootFirstShown = 6;
    private const int LootFirstId = 9;

    // Positions in the shop's values: the balance, the count, then the first offer's five values.
    private const int ShopBalance = 1;
    private const int ShopCount = 2;
    private const int ShopFirstShown = 3;
    private const int ShopFirstId = 4;
    private const int ShopFirstPrice = 5;
    private const int ShopFirstDiscounted = 6;
    private const int ShopFirstBought = 7;

    // A coffer: 450 tokens, four offers.
    [Fact]
    public void Reads_a_treasure_coffer()
    {
        var reading = CrucibleOffers.ReadTreasure(WindowFixture.Load("treasure.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleOfferSource.Treasure, reading.Source);
        Assert.Equal(450, reading.Tokens);
        Assert.Null(reading.TokensEarned);

        // `Select` maps each offer to its id, like `Array.map` in TypeScript.
        Assert.Equal(new uint[] { 12, 53, 140, 37 }, reading.Offers.Select(offer => offer.ItemId));
        Assert.All(reading.Offers, offer => Assert.Null(offer.Price));
    }

    // A loot window: the fight paid 225 tokens onto a balance of 0, and three offers are shown; the
    // fourth place is flagged unshown and carries no id.
    [Fact]
    public void Reads_a_loot_window_with_the_tokens_it_paid()
    {
        var reading = CrucibleOffers.ReadLoot(WindowFixture.Load("booty.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleOfferSource.Loot, reading.Source);
        Assert.Equal(0, reading.Tokens);
        Assert.Equal(225, reading.TokensEarned);
        Assert.Equal(new uint[] { 63, 133, 138 }, reading.Offers.Select(offer => offer.ItemId));
    }

    // A shop: 1,328 tokens and sixteen offers, some discounted, one already bought. Records compare
    // by their values, so one `Assert.Equal` checks every field of an offer.
    [Fact]
    public void Reads_a_shop_with_prices_discounts_and_purchases()
    {
        var reading = CrucibleOffers.ReadShop(WindowFixture.Load("shop.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleOfferSource.Shop, reading.Source);
        Assert.Equal(1328, reading.Tokens);
        Assert.Equal(16, reading.Offers.Count);

        Assert.Equal(new CrucibleOffer(162, 125, false, false), reading.Offers[0]);
        Assert.Equal(new CrucibleOffer(157, 25, true, false), reading.Offers[1]);
        Assert.Equal(new CrucibleOffer(172, 65, true, true), reading.Offers[7]);
        Assert.Equal(new CrucibleOffer(64, 564, false, false), reading.Offers[15]);
    }

    // A count of 0 is a shelf with nothing on it, read as such.
    [Fact]
    public void A_shop_count_of_zero_reads_an_empty_shelf()
    {
        var values = WindowFixture.Load("shop.json");
        values[ShopCount] = AddonValue.FromInteger(0);

        var reading = CrucibleOffers.ReadShop(values);

        Assert.NotNull(reading);
        Assert.Empty(reading.Offers);
    }

    // The shop's own count is the number of offers to read. A count beyond the window's thirty
    // places is not a count this reader understands.
    [Fact]
    public void A_shop_count_beyond_its_places_refuses_the_reading()
    {
        var values = WindowFixture.Load("shop.json");
        values[ShopCount] = AddonValue.FromInteger(31);

        Assert.Null(CrucibleOffers.ReadShop(values));
    }

    // Each of a shown offer's five values has to be the kind the layout puts there; one that is not
    // means the offer's slots have moved.
    [Theory]
    [InlineData(ShopFirstShown)]
    [InlineData(ShopFirstId)]
    [InlineData(ShopFirstPrice)]
    [InlineData(ShopFirstDiscounted)]
    [InlineData(ShopFirstBought)]
    public void A_shop_offer_value_of_the_wrong_kind_refuses_the_reading(int index)
    {
        var values = WindowFixture.Load("shop.json");
        values[index] = AddonValue.Unreadable;

        Assert.Null(CrucibleOffers.ReadShop(values));
    }

    // A price that is text but not a number means the offer's slots have moved.
    [Fact]
    public void A_shop_price_that_is_not_a_number_refuses_the_reading()
    {
        var values = WindowFixture.Load("shop.json");
        values[ShopFirstPrice] = AddonValue.FromText("Feed");

        Assert.Null(CrucibleOffers.ReadShop(values));
    }

    // A balance that does not read refuses each window's reading: the token balance is part of what
    // every offer window is read for.
    [Theory]
    [InlineData("treasure.json", ChestBalance)]
    [InlineData("booty.json", ChestBalance)]
    [InlineData("shop.json", ShopBalance)]
    public void An_unreadable_balance_refuses_the_reading(string fixture, int balanceIndex)
    {
        var values = WindowFixture.Load(fixture);
        values[balanceIndex] = AddonValue.FromText("Spoils of battle lie at your feet.");

        Assert.Null(Read(fixture, values));
    }

    // The tokens a fight paid are the loot window's own fact; without them the reading is refused.
    [Fact]
    public void An_unreadable_loot_payout_refuses_the_reading()
    {
        var values = WindowFixture.Load("booty.json");
        values[LootEarned] = AddonValue.Unreadable;

        Assert.Null(CrucibleOffers.ReadLoot(values));
    }

    // A "shown" flag or a shown offer's id holding the wrong kind of value means the layout has
    // moved, in the treasure and loot windows alike.
    [Theory]
    [InlineData("treasure.json", TreasureFirstShown)]
    [InlineData("treasure.json", TreasureFirstId)]
    [InlineData("booty.json", LootFirstShown)]
    [InlineData("booty.json", LootFirstId)]
    public void A_chest_offer_value_of_the_wrong_kind_refuses_the_reading(string fixture, int index)
    {
        var values = WindowFixture.Load(fixture);
        values[index] = AddonValue.FromText("Ring of Protection");

        Assert.Null(Read(fixture, values));
    }

    // A shown offer always names an item, so an id of 0 is a slot read out of place.
    [Theory]
    [InlineData("treasure.json", TreasureFirstId)]
    [InlineData("booty.json", LootFirstId)]
    [InlineData("shop.json", ShopFirstId)]
    public void A_shown_offer_with_item_id_zero_refuses_the_reading(string fixture, int index)
    {
        var values = WindowFixture.Load(fixture);
        values[index] = AddonValue.FromInteger(0);

        Assert.Null(Read(fixture, values));
    }

    // A window handed too few values for its layout is refused, not read short.
    [Fact]
    public void A_short_value_list_refuses_each_reading()
    {
        Assert.Null(CrucibleOffers.ReadTreasure(WindowFixture.Load("treasure.json").GetRange(0, 10)));
        Assert.Null(CrucibleOffers.ReadLoot(WindowFixture.Load("booty.json").GetRange(0, 10)));
        Assert.Null(CrucibleOffers.ReadShop(WindowFixture.Load("shop.json").GetRange(0, 40)));
        Assert.Null(CrucibleOffers.ReadShop(WindowFixture.Load("shop.json").GetRange(0, 2)));
    }

    /// <summary>Reads a fixture with the reader for its window.</summary>
    // A `switch` expression picks a value by matching, like a lookup table of arrow functions; `_`
    // is the fallback arm.
    private static CrucibleOfferReading? Read(string fixture, List<AddonValue> values) =>
        fixture switch
        {
            "treasure.json" => CrucibleOffers.ReadTreasure(values),
            "booty.json" => CrucibleOffers.ReadLoot(values),
            _ => CrucibleOffers.ReadShop(values),
        };
}
