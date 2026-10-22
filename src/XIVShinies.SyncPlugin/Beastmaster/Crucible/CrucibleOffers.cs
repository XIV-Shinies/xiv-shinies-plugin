using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>Which window offered the items.</summary>
public enum CrucibleOfferSource
{
    /// <summary>A treasure coffer's offers (<c>XBMContentsTreasure</c>).</summary>
    Treasure,

    /// <summary>The loot a fight left (<c>XBMContentsBooty</c>).</summary>
    Loot,

    /// <summary>A shop's shelf (<c>XBMContentsItemShop</c>).</summary>
    Shop,
}

/// <summary>One item a window offered. The shop fields are null outside a shop.</summary>
/// <param name="ItemId">The item's <c>XBMItem</c> id.</param>
/// <param name="Price">The price as drawn, discount applied.</param>
public sealed record CrucibleOffer(
    uint ItemId,
    int? Price = null,
    bool? Discounted = null,
    bool? Bought = null);

/// <summary>What one offer window showed.</summary>
/// <param name="Tokens">The player's token balance as the window drew it.</param>
/// <param name="TokensEarned">The tokens the fight paid, on a loot window; null on the others.</param>
/// <param name="Offers">The shown offers, in the window's order.</param>
public sealed record CrucibleOfferReading(
    CrucibleOfferSource Source,
    int Tokens,
    int? TokensEarned,
    IReadOnlyList<CrucibleOffer> Offers);

/// <summary>
/// Reads the treasure coffer, loot and shop windows. A value of the wrong kind, or a shown offer
/// naming no item, refuses the whole reading.
/// </summary>
/// <remarks>
/// The treasure and loot windows share a layout: the balance as text, then four places of five values,
/// each a "shown" flag with the item id three values on, which a place not shown does not carry. The
/// loot window also carries the fight's payout two values past the balance, so its places start three
/// values later. The shop has the balance, a count of offers, then thirty places of five values (shown,
/// item id, price as text, discounted, bought), of which the counted ones are read.
/// </remarks>
public static class CrucibleOffers
{
    // A place starts at its "shown" flag, which the offsets count from. "Chest" names the layout the
    // treasure and loot windows share.
    private const int PlaceStride = 5;
    private const int ChestPlaces = 4;
    private const int ChestIdOffset = 3;
    private const int ChestBalanceIndex = 2;
    private const int TreasureFirstPlace = 3;
    private const int LootEarnedIndex = 4;
    private const int LootFirstPlace = 6;
    private const int ShopBalanceIndex = 1;
    private const int ShopCountIndex = 2;
    private const int ShopFirstPlace = 3;
    private const int ShopPlaces = 30;
    private const int ShopIdOffset = 1;
    private const int ShopPriceOffset = 2;
    private const int ShopDiscountedOffset = 3;
    private const int ShopBoughtOffset = 4;

    /// <summary>
    /// Reads a treasure coffer, or null when the values are not the layout this reader knows.
    /// </summary>
    public static CrucibleOfferReading? ReadTreasure(IReadOnlyList<AddonValue> values) =>
        ReadChest(values, CrucibleOfferSource.Treasure, TreasureFirstPlace, earnedIndex: null);

    /// <summary>Reads a fight's loot, or null when the values are not the layout this reader knows.</summary>
    public static CrucibleOfferReading? ReadLoot(IReadOnlyList<AddonValue> values) =>
        ReadChest(values, CrucibleOfferSource.Loot, LootFirstPlace, LootEarnedIndex);

    /// <summary>Reads a shop, or null when the values are not the layout this reader knows.</summary>
    /// <remarks>A count of 0 reads as a shop with no offers, not as null.</remarks>
    public static CrucibleOfferReading? ReadShop(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= ShopCountIndex)
            return null;

        if (CrucibleText.ReadNumber(values[ShopBalanceIndex].Text) is not { } tokens)
            return null;

        // The count must fit the window's places and the values handed.
        if (values[ShopCountIndex].Number is not { } count || count > ShopPlaces)
            return null;

        // The count is at most thirty here, so the cast to `int` loses nothing.
        var lastIndex = ShopFirstPlace + (((int)count - 1) * PlaceStride) + ShopBoughtOffset;
        if (count > 0 && values.Count <= lastIndex)
            return null;

        var offers = new List<CrucibleOffer>((int)count);
        for (var place = 0; place < count; place++)
        {
            var first = ShopFirstPlace + (place * PlaceStride);

            if (values[first].Flag is not { } shown)
                return null;

            if (!shown)
                continue;

            if (values[first + ShopIdOffset].Number is not { } itemId
                || itemId == 0
                || CrucibleText.ReadNumber(values[first + ShopPriceOffset].Text) is not { } price
                || values[first + ShopDiscountedOffset].Flag is not { } discounted
                || values[first + ShopBoughtOffset].Flag is not { } bought)
            {
                return null;
            }

            offers.Add(new CrucibleOffer(itemId, price, discounted, bought));
        }

        return new CrucibleOfferReading(CrucibleOfferSource.Shop, tokens, null, offers);
    }

    /// <summary>
    /// Reads the shared treasure and loot layout, or null when the values do not fit it.
    /// </summary>
    /// <param name="earnedIndex">Where the fight's payout sits, or null when the window has none.</param>
    private static CrucibleOfferReading? ReadChest(
        IReadOnlyList<AddonValue> values, CrucibleOfferSource source, int firstPlace, int? earnedIndex)
    {
        var lastIndex = firstPlace + ((ChestPlaces - 1) * PlaceStride) + ChestIdOffset;
        if (values.Count <= lastIndex)
            return null;

        if (CrucibleText.ReadNumber(values[ChestBalanceIndex].Text) is not { } tokens)
            return null;

        int? earned = null;

        if (earnedIndex is { } index)
        {
            if (CrucibleText.ReadNumber(values[index].Text) is not { } paid)
                return null;

            earned = paid;
        }

        var offers = new List<CrucibleOffer>(ChestPlaces);
        for (var place = 0; place < ChestPlaces; place++)
        {
            var first = firstPlace + (place * PlaceStride);

            if (values[first].Flag is not { } shown)
                return null;

            if (!shown)
                continue;

            if (values[first + ChestIdOffset].Number is not { } itemId || itemId == 0)
                return null;

            offers.Add(new CrucibleOffer(itemId));
        }

        return new CrucibleOfferReading(source, tokens, earned, offers);
    }
}
