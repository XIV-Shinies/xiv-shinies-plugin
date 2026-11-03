using System;
using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>One read of the glamour dresser, as plain arrays copied out of the game.</summary>
/// <param name="FinderIds">The item finder's dresser ids, one per slot.</param>
/// <param name="FinderBits">
/// The item finder's per-slot outfit-piece bits, paired with FinderIds by index.
/// </param>
/// <param name="LiveIds">The live dresser copy's ids, or null when it is not loaded in this zone.</param>
/// <param name="LiveStain0">
/// The live copy's first stain byte per slot, or null. A dye channel on a loose slot only (see
/// remarks).
/// </param>
/// <param name="LiveStain1">
/// The live copy's second stain byte per slot, or null. A dye channel on a loose slot only (see
/// remarks).
/// </param>
/// <remarks>
/// The item finder's copy holds each slot's id and outfit bits; only the live copy holds dyes, and it
/// is not always loaded (see <see cref="DresserPiece.Stains"/>), so <see cref="GlamourSnapshot.Build"/>
/// trusts a slot's dyes only where both copies store the same id. On an outfit slot the two stain
/// bytes hold the outfit-piece bits, low byte first (bits of 1904 read as 112 and 7), so they are
/// never read as dyes there. Ids are stored ids, in the high-quality encoding both copies share (see
/// <see cref="GameItemId"/>).
/// </remarks>
public sealed record DresserReading(
    IReadOnlyList<uint> FinderIds,
    IReadOnlyList<ushort> FinderBits,
    IReadOnlyList<uint>? LiveIds,
    IReadOnlyList<byte>? LiveStain0,
    IReadOnlyList<byte>? LiveStain1);

/// <summary>One occupied slot of a container that can hold the character's gear.</summary>
/// <param name="StoredId">
/// The slot's stored id, in the item finder's high-quality encoding when it came from a saved copy
/// (see <see cref="GameItemId"/>).
/// </param>
/// <param name="Quantity">How many copies the slot holds.</param>
/// <param name="Place">Which place the slot is in, one of the <see cref="HeldPlaces"/> values.</param>
/// <param name="RetainerId">
/// For a <see cref="HeldPlaces.Retainer"/> slot, the raw id of the retainer whose saved copy it
/// came from; 0 for every other place. A <see cref="HeldPlaces.Market"/> slot needs none: the
/// listings read belong to one retainer, which the roster names.
/// </param>
public readonly record struct HeldSlot(uint StoredId, uint Quantity, string Place, ulong RetainerId = 0)
{
    // The generated ToString would print the raw retainer id wherever a slot is logged, and the raw
    // id must never reach a log.
    public override string ToString() =>
        $"HeldSlot {{ StoredId = {StoredId}, Quantity = {Quantity}, Place = {Place} }}";
}

/// <summary>The server's ceilings for the <c>glamour</c> category.</summary>
/// <remarks>
/// Past any ceiling, the server drops the whole <c>glamour</c> key and names it in the response's
/// <c>rejectedCategories</c>; the rest of the upload still applies. <see cref="GlamourSnapshot"/>
/// withholds the category rather than send past a list, count or retainer ceiling. The list and count
/// ceilings sit well above what a character can hold (the dresser has 800 slots, and gear never
/// stacks), and <see cref="Retainers"/> is the game's own limit.
/// </remarks>
public static class GlamourCaps
{
    /// <summary>The most loose dresser pieces the server accepts.</summary>
    public const int Dresser = 1_000;

    /// <summary>The most outfit glamours the server accepts.</summary>
    public const int OutfitGlamours = 1_000;

    /// <summary>The most Armoire ids the server accepts.</summary>
    public const int Armoire = 5_000;

    /// <summary>
    /// The most held entries (one per item per place, and per retainer) the server accepts.
    /// </summary>
    public const int Held = 20_000;

    /// <summary>The largest copy count the server accepts on one held entry.</summary>
    public const int HeldCount = 9_999;

    /// <summary>
    /// The most retainers the server accepts in one upload: keys run from 1 to 10, as many as the
    /// game lets a character employ.
    /// </summary>
    public const int Retainers = 10;

    /// <summary>
    /// The longest retainer name the server accepts; <see cref="RetainerRoster"/> leaves a longer
    /// one off.
    /// </summary>
    public const int RetainerName = 32;
}

/// <summary>
/// What <see cref="GlamourSnapshot.Build"/> produced: the category's facts, or the reason the whole
/// category is withheld this pass.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Facts"/> and <see cref="WithheldReason"/> is set, which the two
/// factories guarantee. The reason is <see cref="CollectSkipReasons.OverCap"/> or
/// <see cref="CollectSkipReasons.UnexpectedLayout"/> (see <see cref="GlamourSnapshot.Build"/>),
/// kept apart so a pasted diagnostic tells a value past a ceiling from a reading the plugin cannot
/// interpret.
/// </remarks>
public sealed record GlamourBuildResult
{
    private GlamourBuildResult()
    {
    }

    /// <summary>The facts to send, or null when the category is withheld.</summary>
    public GlamourFacts? Facts { get; private init; }

    /// <summary>
    /// Why the category is withheld, as a <see cref="CollectSkipReasons"/> value, or null when the
    /// facts were built.
    /// </summary>
    public string? WithheldReason { get; private init; }

    /// <summary>A snapshot built in full, ready to send.</summary>
    /// <param name="facts">The category's facts.</param>
    public static GlamourBuildResult Built(GlamourFacts facts) => new() { Facts = facts };

    /// <summary>A snapshot withheld whole, so no <c>glamour</c> key is sent this pass.</summary>
    /// <param name="reason">The <see cref="CollectSkipReasons"/> value that says why.</param>
    public static GlamourBuildResult Withheld(string reason) => new() { WithheldReason = reason };
}

/// <summary>
/// Assembles the <c>glamour</c> category's facts from plain readings of the dresser, the Armoire
/// and the character's held gear.
/// </summary>
/// <remarks>
/// A list, held count or retainer count past its <see cref="GlamourCaps"/> ceiling withholds the
/// whole category (<see cref="CollectSkipReasons.OverCap"/>), so no <c>glamour</c> key is sent; it is
/// never truncated or clamped. No character holds past a ceiling (see <see cref="GlamourCaps"/>), so
/// a value past one is far more likely a misread than a real collection. A cut list would read every
/// piece left off as removed, and a clamped count would be a confident wrong number.
/// </remarks>
public static class GlamourSnapshot
{
    /// <summary>
    /// Builds the category's facts, or withholds the whole category when they cannot be sent as
    /// they are.
    /// </summary>
    /// <param name="dresser">
    /// The dresser read this pass, or null when it was not read. A reading that cannot be
    /// interpreted withholds the category (see the return value).
    /// </param>
    /// <param name="setIndex">
    /// Outfit set id to its 11 piece columns, as built by <see cref="MirageSetIndex.Build"/>.
    /// Consulted only to interpret a dresser reading (see the return value).
    /// </param>
    /// <param name="armoireItemIds">
    /// The item ids the Armoire holds, or null when it was not read this pass because the game did
    /// not have it loaded.
    /// </param>
    /// <param name="heldSlots">
    /// One entry per occupied slot of every container the character holds gear in, tagged with its
    /// place. Raw stored ids: repeats and the high-quality encoding are both expected, and the
    /// copies of each base id in one place are summed whatever their quality.
    /// </param>
    /// <param name="retainers">
    /// The retainers read this pass. A retainer slot takes its retainer's key from it and a market
    /// slot takes its market key; a slot with no key to take is left out (see
    /// <see cref="RetainerRoster"/>). Its entries become the facts' <c>retainers</c> list.
    /// </param>
    /// <param name="isGlamourGear">
    /// The caller's answer to "is this base item id a piece of gear?", taken from the game's item
    /// sheet. Asked only about non-zero base ids held at least once, once per distinct id.
    /// </param>
    /// <returns>
    /// The facts, built in full; or a withheld result, which sends no <c>glamour</c> key this pass,
    /// naming one of two reasons. <see cref="CollectSkipReasons.UnexpectedLayout"/> when a dresser
    /// was read but cannot be interpreted: its two finder arrays differ in length, or
    /// <paramref name="setIndex"/> is empty.
    /// <see cref="CollectSkipReasons.OverCap"/> when a list, a held count or the number of
    /// retainers would exceed its cap.
    /// </returns>
    public static GlamourBuildResult Build(
        DresserReading? dresser,
        IReadOnlyDictionary<uint, uint[]> setIndex,
        IEnumerable<uint>? armoireItemIds,
        IEnumerable<HeldSlot> heldSlots,
        RetainerRoster retainers,
        Func<uint, bool> isGlamourGear)
    {
        // Both stay null unless the dresser was read: one container feeds both.
        List<DresserPiece>? loose = null;
        List<OutfitGlamour>? outfits = null;

        if (dresser is not null)
        {
            if (!CanInterpret(dresser, setIndex))
                return GlamourBuildResult.Withheld(CollectSkipReasons.UnexpectedLayout);

            (loose, outfits) = ReadDresser(dresser, setIndex);
        }

        // Zero is padding in the game's data, never an item.
        var armoire = armoireItemIds?.Where(id => id != 0).Distinct().Order().ToList();

        // Asks once per distinct item, however many places hold it.
        var gearAnswers = new Dictionary<uint, bool>();
        bool IsGear(uint baseId)
        {
            if (!gearAnswers.TryGetValue(baseId, out var answer))
            {
                answer = isGlamourGear(baseId);
                gearAnswers[baseId] = answer;
            }

            return answer;
        }

        // In the wire order GlamourFacts.Held documents.
        var heldGear = SumHeldCopies(heldSlots, retainers)
            .Where(entry => IsGear(entry.Key.BaseId))
            .OrderBy(entry => entry.Key.BaseId)
            .ThenBy(entry => PlaceOrder(entry.Key.Place))
            .ThenBy(entry => entry.Key.Retainer ?? 0)
            .ToList();

        // Counts are capped after the gear filter: materials can stack far past the cap, but they
        // never go on the wire, so they must not withhold the snapshot.
        if (Exceeds(loose, GlamourCaps.Dresser)
            || Exceeds(outfits, GlamourCaps.OutfitGlamours)
            || Exceeds(armoire, GlamourCaps.Armoire)
            || Exceeds(heldGear, GlamourCaps.Held)
            || heldGear.Any(entry => entry.Value > GlamourCaps.HeldCount)
            || retainers.Entries.Count > GlamourCaps.Retainers)
        {
            return GlamourBuildResult.Withheld(CollectSkipReasons.OverCap);
        }

        // Every count is now at most HeldCount, so narrowing the wide sum back to a uint is exact.
        var held = heldGear
            .Select(entry => new HeldPiece
            {
                Id = entry.Key.BaseId,
                Place = entry.Key.Place,
                Retainer = entry.Key.Retainer,
                Count = (uint)entry.Value,
            })
            .ToList();

        return GlamourBuildResult.Built(new GlamourFacts
        {
            Dresser = loose,
            OutfitGlamours = outfits,
            Armoire = armoire,
            Held = held,

            // Left out rather than sent empty when no retainer was read.
            Retainers = retainers.Entries.Count > 0 ? retainers.Entries : null,
        });
    }

    // A place's position in the wire order, so the held list sorts the same way every pass. A place
    // outside HeldPlaces sorts last; the collector never produces one.
    private static int PlaceOrder(string place)
    {
        for (var index = 0; index < HeldPlaces.InWireOrder.Count; index++)
        {
            if (HeldPlaces.InWireOrder[index] == place)
                return index;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// False when a dresser reading cannot be interpreted, so Build withholds the whole category.
    /// </summary>
    /// <remarks>
    /// Not just the dresser lists: the dresser's note says read whenever the item finder's copy was,
    /// and a source noted as read must never arrive without its list. Ids and bits of different
    /// lengths cannot vouch for every slot, and a list cut to the shorter would read each slot left off
    /// as removed. The game always defines outfits, so an empty set index is a misread sheet that would
    /// send every outfit as a loose piece and read it as gone.
    /// </remarks>
    private static bool CanInterpret(
        DresserReading dresser,
        IReadOnlyDictionary<uint, uint[]> setIndex) =>
        dresser.FinderIds.Count == dresser.FinderBits.Count && setIndex.Count > 0;

    // Per item, place and retainer, across qualities; empty slots, zero quantities and keyless retainer
    // or market slots add nothing. 64-bit sums keep a misread quantity huge for the count cap to catch.
    private static Dictionary<(uint BaseId, string Place, int? Retainer), ulong> SumHeldCopies(
        IEnumerable<HeldSlot> heldSlots,
        RetainerRoster retainers)
    {
        var copies = new Dictionary<(uint BaseId, string Place, int? Retainer), ulong>();

        foreach (var slot in heldSlots)
        {
            var baseId = GameItemId.Split(slot.StoredId).BaseId;
            if (baseId == 0 || slot.Quantity == 0)
                continue;

            var retainer = slot.Place switch
            {
                HeldPlaces.Retainer => retainers.KeyOf(slot.RetainerId),
                HeldPlaces.Market => retainers.MarketKey,
                _ => null,
            };

            var namesRetainer = slot.Place is HeldPlaces.Retainer or HeldPlaces.Market;
            if (namesRetainer && retainer is null)
                continue;

            var key = (baseId, slot.Place, retainer);
            copies.TryGetValue(key, out var sum);
            copies[key] = sum + slot.Quantity;
        }

        return copies;
    }

    /// <summary>
    /// How many distinct items the facts report, across the dresser's loose pieces, every outfit's
    /// stored pieces, the Armoire and held gear.
    /// </summary>
    /// <remarks>
    /// An item counts once however many places and copies hold it. An outfit's own set id is not
    /// counted, only the pieces stored inside it.
    /// </remarks>
    public static int CountPieces(GlamourFacts facts)
    {
        var distinct = new HashSet<uint>();

        if (facts.Dresser is not null)
        {
            foreach (var piece in facts.Dresser)
                distinct.Add(piece.Id);
        }

        if (facts.OutfitGlamours is not null)
        {
            foreach (var outfit in facts.OutfitGlamours)
                distinct.UnionWith(outfit.PieceIds);
        }

        if (facts.Armoire is not null)
            distinct.UnionWith(facts.Armoire);

        foreach (var piece in facts.Held)
            distinct.Add(piece.Id);

        return distinct.Count;
    }

    /// <summary>
    /// Splits the dresser into loose pieces and outfits, with no outfit rules of its own.
    /// </summary>
    /// <remarks>
    /// A slot is an outfit only when its id is a row of the game's outfit sheet (see
    /// <see cref="MirageSetIndex.Build"/>), and its pieces are what that row lists and the slot's bits
    /// say is still inside; any further meaning is the server's to derive. The caller has checked that
    /// the ids and bits are the same length, so indexing both by slot stays in range.
    /// </remarks>
    private static (List<DresserPiece> Loose, List<OutfitGlamour> Outfits) ReadDresser(
        DresserReading dresser,
        IReadOnlyDictionary<uint, uint[]> setIndex)
    {
        var loose = new List<DresserPiece>();
        var outfits = new List<OutfitGlamour>();

        var ids = dresser.FinderIds;
        var bits = dresser.FinderBits;

        for (var slot = 0; slot < ids.Count; slot++)
        {
            // Base id 0 is an empty slot (see GameItemId).
            var (baseId, isHq) = GameItemId.Split(ids[slot]);
            if (baseId == 0)
                continue;

            // Looked up by base id, so an outfit stored at high quality still finds its set. An
            // outfit slot never reads the stain bytes (see DresserReading).
            if (setIndex.TryGetValue(baseId, out var pieceColumns))
            {
                outfits.Add(new OutfitGlamour
                {
                    OutfitId = baseId,

                    // Each stored item once, and possibly none (see OutfitGlamour.PieceIds).
                    PieceIds = MirageSetIndex.StoredPieces(pieceColumns, bits[slot])
                        .Distinct()
                        .Order()
                        .ToList(),
                });
                continue;
            }

            loose.Add(new DresserPiece
            {
                Id = baseId,

                // Null for normal quality (see DresserPiece.Hq).
                Hq = isHq ? true : null,

                Stains = StainsAt(dresser, slot),
            });
        }

        // List.Sort is unstable, but each comparison checks every field the builder fills, so
        // entries it calls equal are identical and their order cannot show.
        loose.Sort(CompareLoose);
        outfits.Sort(CompareOutfits);

        return (loose, outfits);
    }

    // A loose slot's dyes, or null unless all three live arrays reach the slot and store the same raw
    // id (another id means another piece's dyes). Outfit slots never call this (see DresserReading).
    private static IReadOnlyList<int>? StainsAt(DresserReading dresser, int slot)
    {
        var liveIds = dresser.LiveIds;
        var stain0 = dresser.LiveStain0;
        var stain1 = dresser.LiveStain1;

        if (liveIds is null || stain0 is null || stain1 is null)
            return null;

        if (slot >= liveIds.Count || slot >= stain0.Count || slot >= stain1.Count)
            return null;

        if (liveIds[slot] != dresser.FinderIds[slot])
            return null;

        // Widened to int to match the wire type (see DresserPiece.Stains).
        return new[] { (int)stain0[slot], (int)stain1[slot] };
    }

    // By id, then normal quality first, then dyes. The dye key makes the order depend only on what
    // is stored, never on which slot holds it.
    private static int CompareLoose(DresserPiece a, DresserPiece b)
    {
        var byId = a.Id.CompareTo(b.Id);
        if (byId != 0)
            return byId;

        var byQuality = (a.Hq == true).CompareTo(b.Hq == true);
        if (byQuality != 0)
            return byQuality;

        // Unknown dyes sort before known ones; two unknowns are equal.
        if (a.Stains is null || b.Stains is null)
            return (a.Stains is not null).CompareTo(b.Stains is not null);

        return CompareElementwise(a.Stains, b.Stains);
    }

    private static int CompareOutfits(OutfitGlamour a, OutfitGlamour b)
    {
        var byId = a.OutfitId.CompareTo(b.OutfitId);
        return byId != 0 ? byId : CompareElementwise(a.PieceIds, b.PieceIds);
    }

    // Dictionary order: the first differing element decides, and a list that runs out first sorts
    // first.
    private static int CompareElementwise<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
        where T : IComparable<T>
    {
        var shared = Math.Min(a.Count, b.Count);

        for (var i = 0; i < shared; i++)
        {
            var byElement = a[i].CompareTo(b[i]);
            if (byElement != 0)
                return byElement;
        }

        return a.Count.CompareTo(b.Count);
    }

    private static bool Exceeds<T>(IReadOnlyCollection<T>? list, int cap) =>
        list is not null && list.Count > cap;
}
