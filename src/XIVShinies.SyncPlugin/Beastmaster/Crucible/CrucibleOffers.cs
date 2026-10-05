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

/// <summary>One item a window offered.</summary>
/// <param name="ItemId">The item's <c>XBMItem</c> id.</param>
/// <param name="Price">A shop offer's price as drawn, discount applied; null outside a shop.</param>
/// <param name="Discounted">Whether a shop offer is discounted; null outside a shop.</param>
/// <param name="Bought">Whether a shop offer has been bought; null outside a shop.</param>
// `= null` gives a parameter a default, so a caller may leave it out, like a TypeScript default
// parameter (`price: number | null = null`): `new CrucibleOffer(12)` is an offer with no price, as
// the treasure and loot windows build them.
public sealed record CrucibleOffer(
    uint ItemId,
    int? Price = null,
    bool? Discounted = null,
    bool? Bought = null);

/// <summary>What one offer window showed.</summary>
/// <param name="Source">Which window it was.</param>
/// <param name="Tokens">The player's token balance as the window drew it.</param>
/// <param name="TokensEarned">The tokens the fight paid, on a loot window; null on the others.</param>
/// <param name="Offers">The shown offers, in the window's order.</param>
public sealed record CrucibleOfferReading(
    CrucibleOfferSource Source,
    int Tokens,
    int? TokensEarned,
    IReadOnlyList<CrucibleOffer> Offers);

/// <summary>
/// Reads the three windows that offer items: a treasure coffer, a fight's loot, and a shop.
/// </summary>
/// <remarks>
/// <para>
/// The treasure and loot windows share a layout: the balance as text, then four offer places of
/// five values each, a "shown" flag and, three values on, the offer's item id. The loot window also
/// carries the tokens the fight paid, two values past the balance, so its offer places start three
/// values later. A place that is not shown carries no id. The shop has its own layout: the balance,
/// a count of offers, then thirty places of five values (shown, item id, price as text, discounted,
/// bought), of which the count are read.
/// </para>
/// <para>
/// Every slot's kind of value is checked (see <see cref="AddonValue"/>), a shown offer must name an
/// item, and any miss refuses the whole reading.
/// </para>
/// </remarks>
public static class CrucibleOffers
{
    /// <summary>How many values each offer place occupies, in all three windows.</summary>
    private const int PlaceStride = 5;

    /// <summary>How many offer places the treasure and loot windows have.</summary>
    private const int ChestPlaces = 4;

    /// <summary>
    /// Where an offer's item id sits, from its "shown" flag, in the treasure and loot windows.
    /// </summary>
    private const int ChestIdOffset = 3;

    /// <summary>Where the balance sits in the treasure and loot windows.</summary>
    private const int ChestBalanceIndex = 2;

    /// <summary>Where the first treasure offer's "shown" flag sits.</summary>
    private const int TreasureFirstPlace = 3;

    /// <summary>Where the loot window keeps the tokens the fight paid.</summary>
    private const int LootEarnedIndex = 4;

    /// <summary>Where the first loot offer's "shown" flag sits.</summary>
    private const int LootFirstPlace = 6;

    /// <summary>Where the shop keeps the balance.</summary>
    private const int ShopBalanceIndex = 1;

    /// <summary>Where the shop keeps how many offers it has.</summary>
    private const int ShopCountIndex = 2;

    /// <summary>Where the first shop offer's "shown" flag sits.</summary>
    private const int ShopFirstPlace = 3;

    /// <summary>How many offer places the shop window has.</summary>
    private const int ShopPlaces = 30;

    // Offsets within one shop place, from its "shown" flag.
    private const int ShopIdOffset = 1;
    private const int ShopPriceOffset = 2;
    private const int ShopDiscountedOffset = 3;
    private const int ShopBoughtOffset = 4;

    // The two chest readers are one-line methods: `=>` makes the call below the whole body.
    // `earnedIndex: null` names the argument it fills, so the call says what the null means.

    /// <summary>
    /// Reads a treasure coffer, or null when the values are not the layout this reader knows.
    /// </summary>
    /// <param name="values">Every value the window was handed, in order.</param>
    public static CrucibleOfferReading? ReadTreasure(IReadOnlyList<AddonValue> values) =>
        ReadChest(values, CrucibleOfferSource.Treasure, TreasureFirstPlace, earnedIndex: null);

    /// <summary>Reads a fight's loot, or null when the values are not the layout this reader knows.</summary>
    /// <param name="values">Every value the window was handed, in order.</param>
    public static CrucibleOfferReading? ReadLoot(IReadOnlyList<AddonValue> values) =>
        ReadChest(values, CrucibleOfferSource.Loot, LootFirstPlace, LootEarnedIndex);

    /// <summary>Reads a shop, or null when the values are not the layout this reader knows.</summary>
    /// <param name="values">Every value the window was handed, in order.</param>
    /// <remarks>A count of 0 reads as a shop with no offers, not as null.</remarks>
    public static CrucibleOfferReading? ReadShop(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= ShopCountIndex)
            return null;

        // `is not { } tokens` is true when the balance does not read as a number, and the `if`
        // returns; past it, `tokens` holds the number itself.
        if (CrucibleText.ReadNumber(values[ShopBalanceIndex].Text) is not { } tokens)
            return null;

        // The shop's own count of offers, which must fit the window's places and the values handed.
        if (values[ShopCountIndex].Number is not { } count || count > ShopPlaces)
            return null;

        // `(int)count` converts the window's unsigned number to the signed `int` that list sizes
        // and positions use. The count is at most thirty here, so nothing is lost.
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

            // `||` stops at the first test that is true, so the `if` returns as soon as one part is
            // missing or the id is 0.
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
    /// <param name="values">Every value the window was handed.</param>
    /// <param name="source">Which window this is.</param>
    /// <param name="firstPlace">Where the first offer's "shown" flag sits.</param>
    /// <param name="earnedIndex">
    /// Where the tokens the fight paid sit, or null when the window has none.
    /// </param>
    private static CrucibleOfferReading? ReadChest(
        IReadOnlyList<AddonValue> values, CrucibleOfferSource source, int firstPlace, int? earnedIndex)
    {
        var lastIndex = firstPlace + ((ChestPlaces - 1) * PlaceStride) + ChestIdOffset;
        if (values.Count <= lastIndex)
            return null;

        if (CrucibleText.ReadNumber(values[ChestBalanceIndex].Text) is not { } tokens)
            return null;

        // The loot window's payout, when this window has one; `int?` holds "no payout" as null.
        int? earned = null;

        // `earnedIndex is { } index` is the positive form: true when there is an index, which is
        // named `index` inside the braces.
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
