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
/// <para>
/// The game keeps two copies of the dresser. The item finder's copy is always available once the
/// dresser has been opened, and it carries each slot's stored id plus the bits that say which of an
/// outfit's pieces are still inside it. The live copy is the only one that carries dyes, and it is
/// not always loaded (see <see cref="DresserPiece.Stains"/>). The two line up slot by slot, so slot
/// <c>i</c> in one is slot <c>i</c> in the other. They are still two separate copies, so
/// <see cref="GlamourSnapshot.Build"/> trusts the live copy's dyes only for a slot where both copies
/// store the same id.
/// </para>
/// <para>
/// <see cref="FinderIds"/> and <see cref="FinderBits"/> are parallel arrays and must be the same
/// length; a reading where they differ withholds the whole category (see
/// <see cref="GlamourSnapshot.Build"/>).
/// </para>
/// <para>
/// <b>The two stain bytes are dyes only on a loose slot.</b> On a slot holding an outfit, the game
/// keeps that slot's outfit-piece field in the same two bytes, low byte first: item finder bits of
/// 1904 appear here as 112 and 7, because 112 + 7 × 256 = 1904. Those bytes must never be read as
/// dyes, so <see cref="GlamourSnapshot.Build"/> reads them only for loose slots.
/// </para>
/// <para>
/// The ids are stored ids, in the high-quality encoding both copies share (see
/// <see cref="GameItemId"/>).
/// </para>
/// </remarks>
// A "positional" record: the parameter list after the type name declares the properties and the
// constructor in one line, so `new DresserReading(ids, bits, null, null, null)` builds one and
// `reading.FinderIds` reads a property back. The closest TypeScript picture is a readonly object
// type whose fields are filled in order by a constructor.
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
/// For a <see cref="HeldPlaces.Retainer"/> slot, the raw id of the retainer whose saved copy it came
/// from; 0 for every other place. A <see cref="HeldPlaces.Market"/> slot needs none: the listings
/// read belong to one retainer, which the roster names.
/// </param>
// A `readonly record struct`: a small immutable value, copied rather than shared, compared field by
// field. The `= 0` gives the last parameter a default, so a call outside a retainer leaves it out:
// `new HeldSlot(id, 1, HeldPlaces.Bags)`.
public readonly record struct HeldSlot(uint StoredId, uint Quantity, string Place, ulong RetainerId = 0)
{
    // A record writes its own ToString listing every field, which would print the raw retainer id
    // wherever a slot is logged or shown in a failed test. This one leaves the id out; the raw id
    // must never reach a log. `override` replaces the generated version.
    public override string ToString() =>
        $"HeldSlot {{ StoredId = {StoredId}, Quantity = {Quantity}, Place = {Place} }}";
}

/// <summary>The server's ceilings for the <c>glamour</c> category.</summary>
/// <remarks>
/// A list past its ceiling, a held count past <see cref="HeldCount"/>, more retainers than
/// <see cref="Retainers"/> or a retainer name past <see cref="RetainerName"/> makes the server drop
/// the whole <c>glamour</c> key and name it in the response's <c>rejectedCategories</c>; the rest of
/// the upload still applies. The list and count ceilings sit well above what a character can hold
/// (the dresser has 800 slots, and a piece of gear never stacks); <see cref="Retainers"/> is the
/// game's own limit. <see cref="GlamourSnapshot.Build"/> checks the list, count and retainer
/// ceilings before anything is sent and withholds the category past one (see
/// <see cref="GlamourSnapshot"/>'s class remarks).
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
    /// The longest retainer name the server accepts; <see cref="RetainerRoster"/> leaves a longer one
    /// off.
    /// </summary>
    public const int RetainerName = 32;
}

/// <summary>
/// What <see cref="GlamourSnapshot.Build"/> produced: the category's facts, or the reason the whole
/// category is withheld this pass.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="Facts"/> and <see cref="WithheldReason"/> is set. The constructor is
/// private and the two factory methods are the only way to make a result, so one can never carry
/// both or neither.
/// </para>
/// <para>
/// The reason is a <see cref="CollectSkipReasons"/> value, so the collector hands it straight to
/// <see cref="CollectResult.Skipped"/>. Two reasons can come back, kept apart so that a pasted
/// diagnostic tells a misread from a reading the plugin does not understand:
/// <see cref="CollectSkipReasons.OverCap"/> when a list, a held count or the number of retainers
/// passed its ceiling, and <see cref="CollectSkipReasons.UnexpectedLayout"/> when the dresser was
/// read but cannot be interpreted (see <see cref="GlamourSnapshot.Build"/> for both).
/// </para>
/// </remarks>
// The TypeScript picture is a union of two shapes, `{ facts } | { withheldReason }`. The C# this
// project builds with has no union type of that kind, so this record carries both properties as
// nullable, and the private constructor with its two factories is what keeps exactly one of them set.
public sealed record GlamourBuildResult
{
    // Private: a result is made only through Built or Withheld below.
    private GlamourBuildResult()
    {
    }

    /// <summary>The facts to send, or null when the category is withheld.</summary>
    // `private init` lets the property be set only inside this type, while the object is being
    // created, so nothing outside can build a result in a state the factories would not produce.
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
/// Assembles the <c>glamour</c> category's facts from plain readings of the dresser, the Armoire and
/// the character's held gear.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure on purpose.</b> Everything here takes plain lists and returns a plain record, with no
/// game service in sight, so every rule below is unit-tested. The collector that copies those lists
/// out of the game cannot be tested outside it and is verified in game instead; keeping it to
/// "read and hand over" leaves as little as possible to that slower check.
/// </para>
/// <para>
/// <b>Absent is not empty.</b> A container that was not read stays <c>null</c> and is omitted from
/// the JSON, while one that was read and holds nothing becomes an empty list, sent as <c>[]</c>;
/// <see cref="GlamourFacts"/> says why the difference matters.
/// </para>
/// <para>
/// <b>Withheld, never truncated or clamped.</b> If any list would exceed its
/// <see cref="GlamourCaps"/> ceiling, any held count would exceed
/// <see cref="GlamourCaps.HeldCount"/>, or more retainers were read than
/// <see cref="GlamourCaps.Retainers"/>, <see cref="Build"/> withholds the category
/// (<see cref="CollectSkipReasons.OverCap"/>) and the caller sends no <c>glamour</c> key at all.
/// The ceilings sit at or well above anything a character can hold, so a value past one is far more
/// likely a misread than a real collection. Cutting a list to fit would be worse than useless here:
/// under current-holdings semantics every piece left off would read as removed. Clamping a count is
/// no better: a gear count past the cap can only be a misread, and the clamped number would be a
/// confident wrong one.
/// </para>
/// <para>
/// <b>A dresser it cannot interpret withholds the category too</b>
/// (<see cref="CollectSkipReasons.UnexpectedLayout"/>; <see cref="Build"/>'s return value names the
/// two cases). Dropping just the dresser lists would not be safe: the collector reports the dresser
/// as read whenever the item finder's copy was, and a source reported as read must never arrive
/// without its list.
/// </para>
/// <para>
/// <b>No outfit rules of its own.</b> This class does not decide what an outfit is or contains. A
/// dresser slot is an outfit only when its id is a row of the game's own outfit sheet (the set index
/// built by <see cref="MirageSetIndex.Build"/>), and its pieces are whatever that row lists and the
/// slot's own bits say is still inside. The plugin reports what the game stores; any meaning is the
/// server's to derive.
/// </para>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports plain functions.
public static class GlamourSnapshot
{
    /// <summary>
    /// Builds the category's facts, or withholds the whole category when they cannot be sent as they
    /// are.
    /// </summary>
    /// <param name="dresser">
    /// The dresser read this pass, or null when it was not read. A reading that cannot be
    /// interpreted withholds the category (see the return value).
    /// </param>
    /// <param name="setIndex">
    /// Outfit set id → its 11 piece columns, as built by <see cref="MirageSetIndex.Build"/>. Consulted
    /// only to interpret a dresser reading; an empty index with a dresser to interpret withholds the
    /// category (see the return value).
    /// </param>
    /// <param name="armoireItemIds">
    /// The item ids the Armoire holds, or null when it was not read this pass because the game did
    /// not have it loaded.
    /// </param>
    /// <param name="heldSlots">
    /// One entry per occupied slot of every container the character holds gear in, tagged with its
    /// place. Raw stored ids: repeats and the high-quality encoding are both expected, and the copies
    /// of each base id in one place are summed whatever their quality.
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
    // `Func<uint, bool>` is the type of a function that takes a uint and returns a bool — the
    // TypeScript `(id: number) => boolean`. Passing it in keeps the item sheet, which only exists
    // inside the game, out of this class.
    public static GlamourBuildResult Build(
        DresserReading? dresser,
        IReadOnlyDictionary<uint, uint[]> setIndex,
        IEnumerable<uint>? armoireItemIds,
        IEnumerable<HeldSlot> heldSlots,
        RetainerRoster retainers,
        Func<uint, bool> isGlamourGear)
    {
        // Both dresser lists stay null unless the dresser was read: they come from one container,
        // so they are either both known or both unknown.
        List<DresserPiece>? loose = null;
        List<OutfitGlamour>? outfits = null;

        if (dresser is not null)
        {
            // Checked before anything else is built, so a reading that cannot be interpreted sends
            // nothing at all (see CanInterpret for the two cases).
            if (!CanInterpret(dresser, setIndex))
                return GlamourBuildResult.Withheld(CollectSkipReasons.UnexpectedLayout);

            // Deconstruction: the helper returns a pair, and this assigns its two halves to the two
            // variables at once — like `[loose, outfits] = readDresser(...)` in TypeScript.
            (loose, outfits) = ReadDresser(dresser, setIndex);
        }

        // A LINQ chain: each step takes the sequence the previous one produced, like chaining
        // `.filter().map()` on an array in TypeScript. `Distinct()` drops repeats, `Order()` sorts
        // ascending, and `ToList()` runs the chain and collects the result (LINQ is lazy until then).
        // Zeros are padding in the game's data, never an item.
        var armoire = armoireItemIds?.Where(id => id != 0).Distinct().Order().ToList();

        // The gear question, remembered per base id, so it is asked once per distinct item however
        // many places hold it. A local function is a function declared inside a method, visible
        // only there, like a nested function in TypeScript; it can read and write the method's
        // variables, as a closure does.
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

        // Held copies summed per item, place and retainer, then narrowed to gear and put in wire
        // order: by id, then place, then retainer key. Iterating a Dictionary yields key/value pairs,
        // like `Object.entries`: `entry.Key` is the (base id, place, retainer) triple and
        // `entry.Value` its summed copies. `ThenBy` adds a tie-breaker to the sort before it, like a
        // second comparison inside a JavaScript sort comparator.
        var heldGear = SumHeldCopies(heldSlots, retainers)
            .Where(entry => IsGear(entry.Key.BaseId))
            .OrderBy(entry => entry.Key.BaseId)
            .ThenBy(entry => PlaceOrder(entry.Key.Place))
            .ThenBy(entry => entry.Key.Retainer ?? 0)
            .ToList();

        // The count cap is checked on gear only, after the filter. Materials stack, so a character
        // can genuinely hold far more copies of one than any gear piece could reach; they never go
        // on the wire, so they must not withhold the snapshot.
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

    // Whether a dresser reading can be split into loose pieces and outfit slots at all. Either
    // failure withholds the whole category (see the class remarks for why not just the dresser).
    //
    // The ids and their bits are parallel arrays, one entry per slot in each. If their lengths
    // differ the reading is inconsistent and cannot vouch for every slot. Reading only the slots
    // both arrays cover would send a shortened list, and under current holdings every slot left off
    // would read as a piece removed.
    //
    // With an empty set index no outfit slot could be recognized: every outfit would go out as a
    // loose piece under its set id, and the outfit list would arrive empty, reading as every outfit
    // glamour gone. The game always defines outfits, so an index with no rows is a sheet that did not
    // read as expected rather than a real answer.
    private static bool CanInterpret(
        DresserReading dresser,
        IReadOnlyDictionary<uint, uint[]> setIndex) =>
        dresser.FinderIds.Count == dresser.FinderBits.Count && setIndex.Count > 0;

    // Sums the copies of each held item per place, and per retainer for a retainer or market place.
    // Splitting comes first, so a high-quality copy and a normal one in the same place count toward
    // the same entry. A base id of 0 is an empty slot (see GameItemId), and a quantity of 0 holds
    // nothing; neither adds an entry, so the gear question is never asked about them. A retainer or
    // market slot the roster gives no key is left out (see RetainerRoster).
    //
    // The sums are `ulong` (an unsigned 64-bit integer) rather than `uint`. A misread quantity near
    // the top of the 32-bit range would make a 32-bit sum wrap around to a small, plausible count;
    // in 64 bits it stays enormous, and the count cap then withholds the snapshot.
    //
    // The dictionary's key is a named tuple: two tuples with the same three values are the same key,
    // so every slot of one item in one place, and on one retainer for a retainer or market place,
    // lands on one entry.
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

            // The retainer key the entry carries: its own retainer's for a retainer slot, the market
            // retainer's for a listing, none for every other place. A `switch` expression picks one
            // value by matching against each arm in turn; `_` is the catch-all arm.
            var retainer = slot.Place switch
            {
                HeldPlaces.Retainer => retainers.KeyOf(slot.RetainerId),
                HeldPlaces.Market => retainers.MarketKey,
                _ => null,
            };

            // `is A or B` tests the value against each constant in turn, like
            // `place === A || place === B` in TypeScript.
            var namesRetainer = slot.Place is HeldPlaces.Retainer or HeldPlaces.Market;
            if (namesRetainer && retainer is null)
                continue;

            // TryGetValue leaves `sum` at 0 when the entry has not been seen yet.
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
    /// An item in several places counts once, and a held piece counts once however many copies of
    /// it are held: this is a number of distinct pieces, not of copies. An outfit's own set id is not
    /// counted — only the pieces stored inside it are. A container that was not read contributes
    /// nothing.
    /// </remarks>
    public static int CountPieces(GlamourFacts facts)
    {
        // A HashSet keeps each value once, like a JavaScript `Set`; its Count is the answer.
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

    // Splits one dresser read into its loose pieces and its outfit slots, each in wire order. The
    // caller has already checked that the ids and bits are the same length, so indexing both by the
    // same slot is always in range.
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
            // Splitting turns a stored id into the item id plus its quality. A base id of 0 is an
            // empty slot (see GameItemId).
            var (baseId, isHq) = GameItemId.Split(ids[slot]);
            if (baseId == 0)
                continue;

            // An id that names a row of the outfit sheet is an outfit slot. The lookup uses the base
            // id, so an outfit glamour stored at high quality resolves to its set as well. `out var`
            // declares the variable that receives the found value — TryGetValue returns whether the
            // key existed and hands the value back through it.
            //
            // An outfit slot never reads the stain bytes (DresserReading says why).
            if (setIndex.TryGetValue(baseId, out var pieceColumns))
            {
                outfits.Add(new OutfitGlamour
                {
                    OutfitId = baseId,

                    // Distinct because the wire lists which pieces the outfit holds, not its
                    // columns: each stored item is listed once, whatever the row's columns hold.
                    // The list may be empty: a slot whose pieces have all been withdrawn still
                    // stores the outfit, so it is reported as stored.
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

                // Normal quality is null rather than false so the serializer omits the key. The
                // `? true : null` form works because the compiler types the whole expression from
                // the property it is assigned to (bool?).
                Hq = isHq ? true : null,

                Stains = StainsAt(dresser, slot),
            });
        }

        // List.Sort takes a comparison function, like Array.prototype.sort in JavaScript, and does
        // not keep equal entries in order. Each comparison looks at every field the builder fills,
        // so entries it calls equal are identical and their order cannot show.
        loose.Sort(CompareLoose);
        outfits.Sort(CompareOutfits);

        return (loose, outfits);
    }

    // A LOOSE slot's two dye channels from the live copy, or null when the live copy cannot vouch for
    // this slot. All three live arrays must exist and reach the slot, and the live copy must store
    // the very same id there (raw, quality encoding included) — a different id means the dyes belong
    // to a different piece.
    //
    // Called for loose slots only (DresserReading says why).
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

    // Loose pieces: by id, then normal quality before high quality, then by dyes. The dye key makes
    // the order depend only on what is stored, never on which slot holds it.
    private static int CompareLoose(DresserPiece a, DresserPiece b)
    {
        // CompareTo returns a negative number, zero or a positive number — the same contract as a
        // JavaScript sort comparator.
        var byId = a.Id.CompareTo(b.Id);
        if (byId != 0)
            return byId;

        // `Hq == true` folds null and false into "normal quality"; false sorts before true.
        var byQuality = (a.Hq == true).CompareTo(b.Hq == true);
        if (byQuality != 0)
            return byQuality;

        // Unknown dyes sort before known ones; two unknowns are equal.
        if (a.Stains is null || b.Stains is null)
            return (a.Stains is not null).CompareTo(b.Stains is not null);

        return CompareElementwise(a.Stains, b.Stains);
    }

    // Outfits: by outfit id, then by their stored pieces.
    private static int CompareOutfits(OutfitGlamour a, OutfitGlamour b)
    {
        var byId = a.OutfitId.CompareTo(b.OutfitId);
        return byId != 0 ? byId : CompareElementwise(a.PieceIds, b.PieceIds);
    }

    // Compares two lists element by element, the way a dictionary orders words: the first differing
    // element decides, and when one list runs out first, the shorter one sorts first.
    // `<T>` makes this generic (one method for lists of any element type, like a TypeScript generic),
    // and `where T : IComparable<T>` restricts T to types that know how to compare themselves, which
    // is what makes `CompareTo` callable on the elements.
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

    // True when a list was read and holds more than its ceiling. An unread (null) list never does.
    private static bool Exceeds<T>(IReadOnlyCollection<T>? list, int cap) =>
        list is not null && list.Count > cap;
}
