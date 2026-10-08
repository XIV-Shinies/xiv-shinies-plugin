using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// GlamourSnapshot turns plain readings of the dresser, the Armoire and the character's held gear
// into the glamour category's facts. It is pure, so every rule it applies is pinned here; reading
// those containers out of the game is verified by in-game QA.
//
// Lists are compared as serialized JSON rather than as records: a record compares a list property
// by reference (is it the same list instance?), not by contents, and the JSON is also exactly what
// the server will see.
public class GlamourSnapshotTests
{
    // A realistic outfit: set 45094 stores a Head piece (column 2) and a Body piece (column 3).
    private const uint OutfitId = 45094;
    private const uint Hat = 2642;
    private const uint Coat = 2965;

    // A second outfit whose columns list its pieces in DESCENDING id order, so a test can tell
    // whether the builder sorts the pieces or merely copies them in column order.
    private const uint OtherOutfitId = 45100;

    // A test-only row, not a real outfit: it names one item in two columns, so a test can check
    // that a stored item is listed once whatever the row's columns hold.
    private const uint RepeatedPieceOutfitId = 45200;
    private const uint RepeatedPiece = 4100;

    // The set index the builder expands outfit slots through: set id -> its 11 piece columns, empty
    // columns as 0, exactly as MirageSetIndex.Build produces it.
    private static readonly IReadOnlyDictionary<uint, uint[]> SetIndex = new Dictionary<uint, uint[]>
    {
        [OutfitId] = new uint[] { 0, 0, Hat, Coat, 0, 0, 0, 0, 0, 0, 0 },
        [OtherOutfitId] = new uint[] { 0, 0, 3100, 1200, 0, 0, 0, 0, 0, 0, 0 },
        [RepeatedPieceOutfitId] = new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, RepeatedPiece, RepeatedPiece },
    };

    // A dresser read with no live copy loaded: only the item finder's ids, every outfit complete
    // (all-zero bits). `params uint[] ids` lets a call list ids directly — FinderOnly(1601, 2642) —
    // and receive them as one array, like a TypeScript rest parameter `...ids: number[]`.
    private static DresserReading FinderOnly(params uint[] ids) =>
        new(ids, new ushort[ids.Length], LiveIds: null, LiveStain0: null, LiveStain1: null);

    // Held slots in the bags, one copy each — the common case for gear, which never stacks.
    private static HeldSlot[] OneEach(params uint[] storedIds) =>
        storedIds.Select(id => new HeldSlot(id, 1, HeldPlaces.Bags)).ToArray();

    // Held slots in the bags with their quantities. `(uint StoredId, uint Quantity)[]` is an array of
    // named tuples, so a call can write the pairs inline as `(Coat, 2)`.
    private static HeldSlot[] InBags(params (uint StoredId, uint Quantity)[] slots) =>
        slots.Select(slot => new HeldSlot(slot.StoredId, slot.Quantity, HeldPlaces.Bags)).ToArray();

    // No held slots at all.
    private static readonly HeldSlot[] NothingHeld = Array.Empty<HeldSlot>();

    // Two retainers read this pass, the second one's market listings read too.
    private const ulong Mogwin = 33_000_000_001;
    private const ulong Kupo = 33_000_000_002;

    private static RetainerRoster TwoRetainers() => RetainerRoster.Build(
        new[] { Mogwin, Kupo },
        new Dictionary<ulong, string> { [Mogwin] = "Mogwin" },
        marketRetainerId: Kupo);

    // Calls the builder with "nothing read" defaults for every input a test does not care about.
    // The `= null` defaults make those parameters optional, like `dresser?: DresserReading` in
    // TypeScript, and the `??` operator supplies the fallback when one is omitted. Every caller
    // stays within the caps and hands over a consistent reading, so a withheld result is a
    // failure; the asserts report it as one, naming the reason, rather than as a null dereference
    // somewhere later in the test.
    private static GlamourFacts BuildFrom(
        DresserReading? dresser = null,
        IEnumerable<uint>? armoire = null,
        IEnumerable<HeldSlot>? held = null,
        RetainerRoster? roster = null,
        Func<uint, bool>? isGear = null)
    {
        var built = GlamourSnapshot.Build(
            dresser,
            SetIndex,
            armoire,
            held ?? NothingHeld,
            roster ?? RetainerRoster.Empty,
            isGear ?? (_ => true));

        Assert.Null(built.WithheldReason);
        Assert.NotNull(built.Facts);
        return built.Facts;
    }

    // Serializes any value under the plugin's shared JSON policy, so an assertion compares exactly
    // the text the server would receive.
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ApiJson.Options);

    // --- Absent vs empty -------------------------------------------------------------------------

    // The server reads this category as current holdings, so an unread container must stay null
    // (omitted on the wire) — an empty list in its place would tell the server everything is gone.
    [Fact]
    public void Unread_containers_stay_null()
    {
        var facts = BuildFrom(dresser: null, armoire: null);

        Assert.Null(facts.Dresser);
        Assert.Null(facts.OutfitGlamours);
        Assert.Null(facts.Armoire);

        // Held has no "not read" state: with no held slots given, it is an empty list, never null.
        Assert.Empty(facts.Held);
    }

    // The other half of the rule: a container that was read and holds nothing is an empty list, which
    // is how the server learns its old contents are gone.
    [Fact]
    public void A_read_empty_container_is_an_empty_list()
    {
        var emptyDresser = new DresserReading(new uint[800], new ushort[800], null, null, null);

        var facts = BuildFrom(dresser: emptyDresser, armoire: Array.Empty<uint>());

        Assert.NotNull(facts.Dresser);
        Assert.Empty(facts.Dresser);
        Assert.NotNull(facts.OutfitGlamours);
        Assert.Empty(facts.OutfitGlamours);
        Assert.NotNull(facts.Armoire);
        Assert.Empty(facts.Armoire);
    }

    // --- Slots -----------------------------------------------------------------------------------

    // Both forms of an empty slot, a raw 0 and a bare one million (see GameItemId), are skipped.
    [Fact]
    public void Empty_slots_are_skipped()
    {
        var facts = BuildFrom(dresser: FinderOnly(0, 1_000_000, 1601));

        Assert.Equal("""[{"id":1601}]""", Json(facts.Dresser));
        Assert.Empty(facts.OutfitGlamours!);
    }

    [Fact]
    public void A_normal_quality_loose_piece_omits_hq()
    {
        var piece = Assert.Single(BuildFrom(dresser: FinderOnly(Hat)).Dresser!);

        Assert.Equal(Hat, piece.Id);
        Assert.Null(piece.Hq);
    }

    [Fact]
    public void An_hq_loose_piece_reports_its_base_id_and_hq()
    {
        var piece = Assert.Single(BuildFrom(dresser: FinderOnly(1_000_000 + Hat)).Dresser!);

        Assert.Equal(Hat, piece.Id);
        Assert.True(piece.Hq);
    }

    // A slot whose id is a set id holds an outfit: it is expanded through the set index into the
    // pieces it stores, and it never also appears as a loose piece.
    [Fact]
    public void An_outfit_slot_reports_its_stored_pieces()
    {
        var facts = BuildFrom(dresser: FinderOnly(OutfitId));

        Assert.Equal("""[{"outfitId":45094,"pieceIds":[2642,2965]}]""", Json(facts.OutfitGlamours));
        Assert.Empty(facts.Dresser!);
    }

    // Bit 2 is the Head column, and a set bit means that piece has been taken out of the outfit.
    [Fact]
    public void A_withdrawn_piece_is_not_in_its_outfit()
    {
        var reading = new DresserReading(new[] { OutfitId }, new ushort[] { 0b100 }, null, null, null);

        var outfit = Assert.Single(BuildFrom(dresser: reading).OutfitGlamours!);

        Assert.Equal(new uint[] { Coat }, outfit.PieceIds);
    }

    // The game stores a high-quality outfit glamour as its set id plus one million; the slot is still
    // an outfit, reported under its plain set id.
    [Fact]
    public void An_hq_outfit_slot_is_still_an_outfit()
    {
        var facts = BuildFrom(dresser: FinderOnly(1_000_000 + OutfitId));

        var outfit = Assert.Single(facts.OutfitGlamours!);
        Assert.Equal(OutfitId, outfit.OutfitId);
        Assert.Empty(facts.Dresser!);
    }

    // The other outfit lists its pieces in descending column order; the wire lists them ascending.
    [Fact]
    public void Outfit_pieces_are_listed_ascending()
    {
        var outfit = Assert.Single(BuildFrom(dresser: FinderOnly(OtherOutfitId)).OutfitGlamours!);

        Assert.Equal(new uint[] { 1200, 3100 }, outfit.PieceIds);
    }

    // The wire lists which pieces an outfit glamour holds, not its columns, so each stored item is
    // listed once whatever the row's columns hold. Pinned with the test-only row that names one item
    // in two columns.
    [Fact]
    public void An_outfit_lists_each_piece_once()
    {
        var outfit = Assert.Single(BuildFrom(dresser: FinderOnly(RepeatedPieceOutfitId)).OutfitGlamours!);

        Assert.Equal(new uint[] { RepeatedPiece }, outfit.PieceIds);
    }

    // With both of its pieces withdrawn (bits 2 and 3), the slot still stores the outfit, so it is
    // reported with an empty piece list rather than dropped.
    [Fact]
    public void A_fully_withdrawn_outfit_is_still_reported()
    {
        var reading = new DresserReading(new[] { OutfitId }, new ushort[] { 0b1100 }, null, null, null);

        Assert.Equal(
            """[{"outfitId":45094,"pieceIds":[]}]""",
            Json(BuildFrom(dresser: reading).OutfitGlamours));
    }

    // Each slot is a separate physical copy, so the same item in two slots is two entries.
    [Fact]
    public void Two_slots_holding_the_same_item_are_two_copies()
    {
        Assert.Equal(2, BuildFrom(dresser: FinderOnly(Hat, Hat)).Dresser!.Count);
    }

    [Fact]
    public void Two_slots_holding_the_same_outfit_are_two_entries()
    {
        var outfits = BuildFrom(dresser: FinderOnly(OutfitId, OutfitId)).OutfitGlamours!;

        Assert.Equal(2, outfits.Count);
        Assert.All(outfits, outfit => Assert.Equal(OutfitId, outfit.OutfitId));
    }

    // --- A dresser reading that cannot be interpreted --------------------------------------------

    // A length mismatch is a reading the plugin cannot interpret (see GlamourSnapshot.CanInterpret),
    // so the whole category is withheld.
    [Fact]
    public void Finder_bits_shorter_than_its_ids_withhold_the_whole_snapshot()
    {
        var bitsShorter = new DresserReading(
            FinderIds: new uint[] { 1601, Coat, 3747 },
            FinderBits: new ushort[] { 0, 0 },
            LiveIds: null,
            LiveStain0: null,
            LiveStain1: null);

        var built = GlamourSnapshot.Build(
            bitsShorter, SetIndex, null, OneEach(Hat), RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.UnexpectedLayout, built.WithheldReason);
    }

    // The same rule with the other array as the shorter one.
    [Fact]
    public void Finder_ids_shorter_than_its_bits_withhold_the_whole_snapshot()
    {
        var idsShorter = new DresserReading(
            FinderIds: new uint[] { 1601, Coat },
            FinderBits: new ushort[] { 0, 0, 0 },
            LiveIds: null,
            LiveStain0: null,
            LiveStain1: null);

        var built = GlamourSnapshot.Build(
            idsShorter, SetIndex, null, OneEach(Hat), RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.UnexpectedLayout, built.WithheldReason);
    }

    // An empty set index makes a read dresser a reading the plugin cannot interpret (see
    // GlamourSnapshot.CanInterpret), so the whole category is withheld.
    [Fact]
    public void An_empty_set_index_withholds_a_read_dresser()
    {
        var noOutfitRows = new Dictionary<uint, uint[]>();

        var built = GlamourSnapshot.Build(
            FinderOnly(OutfitId, Hat), noOutfitRows, null, NothingHeld, RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.UnexpectedLayout, built.WithheldReason);
    }

    // The set index is consulted only to interpret a dresser reading. With the dresser not read this
    // pass there is nothing to misinterpret, so the containers that were read still go out.
    [Fact]
    public void An_empty_set_index_does_not_withhold_when_the_dresser_was_not_read()
    {
        var noOutfitRows = new Dictionary<uint, uint[]>();

        var built = GlamourSnapshot.Build(
            null, noOutfitRows, new uint[] { 3747 }, OneEach(Hat), RetainerRoster.Empty, _ => true);

        Assert.Null(built.WithheldReason);
        Assert.NotNull(built.Facts);
        Assert.Null(built.Facts.Dresser);
        Assert.Equal(new uint[] { 3747 }, built.Facts.Armoire);
    }

    // --- Stains ----------------------------------------------------------------------------------

    [Fact]
    public void Stains_attach_when_the_live_copy_matches_the_slot()
    {
        var reading = new DresserReading(
            FinderIds: new[] { Hat },
            FinderBits: new ushort[] { 0 },
            LiveIds: new[] { Hat },
            LiveStain0: new byte[] { 12 },
            LiveStain1: new byte[] { 0 });

        var piece = Assert.Single(BuildFrom(dresser: reading).Dresser!);

        Assert.Equal(new[] { 12, 0 }, piece.Stains);
    }

    // A live slot holding a different item describes a different piece, so its dyes are not this
    // piece's. The piece itself is still a known fact and is still reported.
    [Fact]
    public void A_mismatched_live_slot_drops_the_stains_not_the_piece()
    {
        var reading = new DresserReading(
            FinderIds: new[] { Hat },
            FinderBits: new ushort[] { 0 },
            LiveIds: new[] { Coat },
            LiveStain0: new byte[] { 12 },
            LiveStain1: new byte[] { 0 });

        var piece = Assert.Single(BuildFrom(dresser: reading).Dresser!);

        Assert.Equal(Hat, piece.Id);
        Assert.Null(piece.Stains);
    }

    // The live copy is loaded only in the zone where the dresser was opened. Missing any one of its
    // three arrays means the dyes are unknown this pass.
    [Fact]
    public void No_live_copy_means_no_stains()
    {
        var ids = new[] { Hat };
        var bits = new ushort[] { 0 };
        var stains = new byte[] { 12 };

        var noIds = new DresserReading(ids, bits, null, stains, stains);
        var noFirstChannel = new DresserReading(ids, bits, ids, null, stains);
        var noSecondChannel = new DresserReading(ids, bits, ids, stains, null);

        Assert.Null(Assert.Single(BuildFrom(dresser: noIds).Dresser!).Stains);
        Assert.Null(Assert.Single(BuildFrom(dresser: noFirstChannel).Dresser!).Stains);
        Assert.Null(Assert.Single(BuildFrom(dresser: noSecondChannel).Dresser!).Stains);
    }

    // A live array too short to reach a slot says nothing about that slot, while slots it does reach
    // keep their dyes.
    [Fact]
    public void A_live_copy_too_short_for_the_slot_drops_its_stains()
    {
        var reading = new DresserReading(
            FinderIds: new uint[] { 1601, Hat },
            FinderBits: new ushort[] { 0, 0 },
            LiveIds: new uint[] { 1601, Hat },
            LiveStain0: new byte[] { 1, 5 },
            LiveStain1: new byte[] { 2 });

        Assert.Equal(
            """[{"id":1601,"stains":[1,2]},{"id":2642}]""",
            Json(BuildFrom(dresser: reading).Dresser));
    }

    // On an outfit slot the live copy's two "stain" bytes are not dyes: they are the low and high
    // byte of the slot's outfit-piece field (112 + 7 × 256 = 1904, the same value the item finder's
    // bits hold). They must never surface as dyes, so the outfit stays stain-free and out of the
    // loose pieces, while a loose slot beside it still gets its real dyes.
    [Fact]
    public void An_outfit_slots_live_bytes_are_never_read_as_dyes()
    {
        var reading = new DresserReading(
            FinderIds: new[] { OutfitId, Hat },
            FinderBits: new ushort[] { 1904, 0 },
            LiveIds: new[] { OutfitId, Hat },
            LiveStain0: new byte[] { 112, 12 },
            LiveStain1: new byte[] { 7, 0 });

        var facts = BuildFrom(dresser: reading);

        // Bits 1904 leave columns 2 and 3 clear, so both of the outfit's pieces are stored.
        Assert.Equal("""[{"outfitId":45094,"pieceIds":[2642,2965]}]""", Json(facts.OutfitGlamours));
        Assert.Equal("""[{"id":2642,"stains":[12,0]}]""", Json(facts.Dresser));
        Assert.DoesNotContain("[112,7]", Json(facts));
    }

    // Both copies store a high-quality piece in the same encoded form, so the comparison is made on
    // the raw stored ids: the same piece at a different quality is a different stored id.
    [Fact]
    public void Stains_compare_the_raw_stored_ids()
    {
        var hqHat = 1_000_000 + Hat;
        var stain = new byte[] { 7 };

        var sameQuality = new DresserReading(
            new[] { hqHat }, new ushort[] { 0 }, new[] { hqHat }, stain, stain);
        var otherQuality = new DresserReading(
            new[] { hqHat }, new ushort[] { 0 }, new[] { Hat }, stain, stain);

        Assert.Equal(new[] { 7, 7 }, Assert.Single(BuildFrom(dresser: sameQuality).Dresser!).Stains);
        Assert.Null(Assert.Single(BuildFrom(dresser: otherQuality).Dresser!).Stains);
    }

    // --- Armoire and held gear -------------------------------------------------------------------

    [Fact]
    public void Armoire_drops_zeros_and_duplicates_and_sorts()
    {
        var facts = BuildFrom(armoire: new uint[] { 3747, 0, 1601, 3747 });

        Assert.Equal(new uint[] { 1601, 3747 }, facts.Armoire);
    }

    // Held slots arrive raw: some in the high-quality encoding, some repeated, some empty, some not
    // gear at all. Each gear base id survives once per place, carrying every copy held there, and the
    // list is ordered by id.
    [Fact]
    public void Held_keeps_gear_once_per_base_id_with_its_copies_in_id_order()
    {
        const uint potion = 5;
        var stored = InBags(
            (Coat, 1), (1601, 1), (potion, 5), (0, 3), (1_000_000, 1), (Hat, 1), (1_000_000 + Coat, 1));

        var facts = BuildFrom(held: stored, isGear: id => id != potion);

        Assert.Equal(
            """[{"id":1601,"place":"bags","count":1},{"id":2642,"place":"bags","count":1},""" +
            """{"id":2965,"place":"bags","count":2}]""",
            Json(facts.Held));
    }

    // Every outfit glamour needs a copy of its own, so the count is what the server builds on.
    // Copies of one item in one place are summed across its slots whatever their quality: a
    // normal-quality and a high-quality copy are both copies of the same piece.
    [Fact]
    public void Held_sums_copies_across_slots_and_qualities()
    {
        var stored = InBags((Coat, 1), (1_000_000 + Coat, 1), (Coat, 2));

        var piece = Assert.Single(BuildFrom(held: stored).Held);

        Assert.Equal(Coat, piece.Id);
        Assert.Equal(4u, piece.Count);
    }

    // A slot reporting no copies holds nothing, so it adds no entry, and the gear question is never
    // asked about it.
    [Fact]
    public void A_zero_quantity_slot_is_ignored()
    {
        var asked = new List<uint>();

        var facts = BuildFrom(
            held: InBags((Coat, 0), (Hat, 1)),
            isGear: id =>
            {
                asked.Add(id);
                return true;
            });

        Assert.Equal("""[{"id":2642,"place":"bags","count":1}]""", Json(facts.Held));
        Assert.Equal(new uint[] { Hat }, asked);
    }

    // The gear question is about the item, so the caller's predicate is asked about the base id. Were
    // it handed the raw high-quality id it would not recognize it, and the piece would be lost.
    [Fact]
    public void Held_asks_about_base_ids()
    {
        var facts = BuildFrom(held: OneEach(1_000_000 + Coat), isGear: id => id == Coat);

        Assert.Equal("""[{"id":2965,"place":"bags","count":1}]""", Json(facts.Held));
    }

    // The predicate's contract, as Build documents it: asked only about non-zero base ids, once per
    // distinct id. The caller answers from the game's item sheet, so asking once per copy would
    // repeat that lookup for nothing, and asking about 0 would ask about an item that does not exist.
    [Fact]
    public void The_gear_question_is_asked_once_per_distinct_base_id()
    {
        // A list that records every id it is asked about. The lambda below adds to it and answers
        // true; a C# lambda can change a variable it captures, just as a JavaScript closure can.
        var asked = new List<uint>();

        BuildFrom(
            held: OneEach(Coat, 1_000_000 + Coat, Coat, 0),
            isGear: id =>
            {
                asked.Add(id);
                return true;
            });

        Assert.Equal(new uint[] { Coat }, asked);
    }

    // --- Places and retainers --------------------------------------------------------------------

    // One item held in several places is one entry per place, so the server can show where each
    // copy sits. The places come in the contract's order: bags, armory, equipped, saddlebag,
    // retainer, market.
    [Fact]
    public void One_item_in_several_places_is_one_entry_per_place()
    {
        var held = new[]
        {
            new HeldSlot(Coat, 1, HeldPlaces.Saddlebag),
            new HeldSlot(Coat, 1, HeldPlaces.Equipped),
            new HeldSlot(Coat, 2, HeldPlaces.Bags),
            new HeldSlot(Coat, 1, HeldPlaces.Armory),
        };

        Assert.Equal(
            """[{"id":2965,"place":"bags","count":2},{"id":2965,"place":"armory","count":1},""" +
            """{"id":2965,"place":"equipped","count":1},{"id":2965,"place":"saddlebag","count":1}]""",
            Json(BuildFrom(held: held).Held));
    }

    // A copy on a retainer carries that retainer's key from the roster, and the same item on two
    // retainers is two entries, in key order.
    [Fact]
    public void Retainer_copies_carry_their_retainers_key()
    {
        var roster = TwoRetainers();
        var held = new[]
        {
            new HeldSlot(Coat, 2, HeldPlaces.Retainer, RetainerId: Mogwin),
            new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: Kupo),
        };

        var pieces = BuildFrom(held: held, roster: roster).Held;

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Equal(HeldPlaces.Retainer, piece.Place));
        Assert.Equal(2u, pieces.Single(piece => piece.Retainer == roster.KeyOf(Mogwin)).Count);
        Assert.Equal(1u, pieces.Single(piece => piece.Retainer == roster.KeyOf(Kupo)).Count);
        Assert.True(pieces[0].Retainer < pieces[1].Retainer);
    }

    // Copies in places that are not a retainer's carry no retainer key at all.
    [Fact]
    public void Copies_outside_a_retainer_carry_no_key()
    {
        var piece = Assert.Single(BuildFrom(held: OneEach(Coat), roster: TwoRetainers()).Held);

        Assert.Null(piece.Retainer);
        Assert.Equal("""[{"id":2965,"place":"bags","count":1}]""", Json(new[] { piece }));
    }

    // Listed copies belong to the retainer whose listings were read, and carry its key.
    [Fact]
    public void Market_copies_carry_the_market_retainers_key()
    {
        var roster = TwoRetainers();

        var piece = Assert.Single(
            BuildFrom(held: new[] { new HeldSlot(Coat, 1, HeldPlaces.Market) }, roster: roster).Held);

        Assert.Equal(HeldPlaces.Market, piece.Place);
        Assert.Equal(roster.MarketKey, piece.Retainer);
    }

    // With no retainer to tie them to, listed copies cannot be reported: a market entry must name
    // the retainer it was listed on.
    [Fact]
    public void Market_copies_are_dropped_without_a_market_retainer()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin }, new Dictionary<ulong, string>(), null);

        var facts = BuildFrom(held: new[] { new HeldSlot(Coat, 1, HeldPlaces.Market) }, roster: roster);

        Assert.Empty(facts.Held);
    }

    // A retainer slot whose retainer is not on the roster has no key to carry, so it is dropped
    // rather than sent pointing at nothing.
    [Fact]
    public void A_retainer_copy_from_a_retainer_not_on_the_roster_is_dropped()
    {
        var held = new[] { new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: 99) };

        Assert.Empty(BuildFrom(held: held, roster: TwoRetainers()).Held);
    }

    // The gear question is about the item, so it is asked once per distinct base id however many
    // places hold it.
    [Fact]
    public void The_gear_question_is_asked_once_across_places()
    {
        var asked = new List<uint>();
        var held = new[]
        {
            new HeldSlot(Coat, 1, HeldPlaces.Bags),
            new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: Mogwin),
            new HeldSlot(Coat, 1, HeldPlaces.Market),
        };

        BuildFrom(
            held: held,
            roster: TwoRetainers(),
            isGear: id =>
            {
                asked.Add(id);
                return true;
            });

        Assert.Equal(new uint[] { Coat }, asked);
    }

    // The facts list every retainer on the roster, exactly as the roster built them.
    [Fact]
    public void The_retainers_list_is_the_roster()
    {
        var roster = TwoRetainers();

        var facts = BuildFrom(roster: roster);

        Assert.Equal(Json(roster.Entries), Json(facts.Retainers));
    }

    // With no retainer read, the list is left out rather than sent empty.
    [Fact]
    public void No_retainers_read_leaves_the_list_out()
    {
        Assert.Null(BuildFrom(roster: RetainerRoster.Empty).Retainers);
    }

    // The contract allows keys 1 to 10, and the game allows ten retainers, so more than ten is a
    // misread and the whole category is withheld rather than sent with a list the server rejects.
    [Fact]
    public void More_than_ten_retainers_withhold_the_whole_snapshot()
    {
        var eleven = Enumerable.Range(1, GlamourCaps.Retainers + 1).Select(i => (ulong)i).ToArray();
        var roster = RetainerRoster.Build(eleven, new Dictionary<ulong, string>(), null);

        var built = GlamourSnapshot.Build(null, SetIndex, null, NothingHeld, roster, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    // The retainer cap is inclusive, like the others: ten retainers are sent whole.
    [Fact]
    public void Ten_retainers_are_kept()
    {
        var ten = Enumerable.Range(1, GlamourCaps.Retainers).Select(i => (ulong)i).ToArray();
        var roster = RetainerRoster.Build(ten, new Dictionary<ulong, string>(), null);

        Assert.Equal(GlamourCaps.Retainers, BuildFrom(roster: roster).Retainers!.Count);
    }

    // One item on two retainers and in the market listings: the retainer entries come in key order,
    // then the market entry, whatever order the slots arrived in. The listing stays its own entry even
    // though the same retainer also holds a copy.
    [Fact]
    public void Retainer_entries_sort_by_key_and_come_before_the_market()
    {
        var roster = TwoRetainers();
        var held = new[]
        {
            new HeldSlot(Coat, 1, HeldPlaces.Market),
            new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: Kupo),
            new HeldSlot(Coat, 2, HeldPlaces.Retainer, RetainerId: Mogwin),
        };

        // `$$"""…"""` is an interpolated raw string: `{{…}}` inserts a value, while a single brace
        // stays a literal brace, so the JSON needs no escaping.
        Assert.Equal(
            $$"""[{"id":2965,"place":"retainer","retainer":{{roster.KeyOf(Mogwin)}},"count":2},""" +
            $$"""{"id":2965,"place":"retainer","retainer":{{roster.KeyOf(Kupo)}},"count":1},""" +
            $$"""{"id":2965,"place":"market","retainer":{{roster.KeyOf(Kupo)}},"count":1}]""",
            Json(BuildFrom(held: held, roster: roster).Held));
        Assert.True(roster.KeyOf(Mogwin) < roster.KeyOf(Kupo));
    }

    // A retainer's saved copy stores a high-quality piece in the encoded form, and both qualities in
    // one retainer count toward one entry.
    [Fact]
    public void Both_qualities_in_one_retainer_are_one_entry()
    {
        var held = new[]
        {
            new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: Mogwin),
            new HeldSlot(1_000_000 + Coat, 1, HeldPlaces.Retainer, RetainerId: Mogwin),
        };

        var piece = Assert.Single(BuildFrom(held: held, roster: TwoRetainers()).Held);

        Assert.Equal(2u, piece.Count);
    }

    // The count cap applies to each entry: copies split across places are each within it, so the
    // snapshot is built even though their total would pass it.
    [Fact]
    public void The_count_cap_applies_per_place()
    {
        var held = new[]
        {
            new HeldSlot(Coat, GlamourCaps.HeldCount, HeldPlaces.Bags),
            new HeldSlot(Coat, GlamourCaps.HeldCount, HeldPlaces.Retainer, RetainerId: Mogwin),
        };

        var pieces = BuildFrom(held: held, roster: TwoRetainers()).Held;

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Equal((uint)GlamourCaps.HeldCount, piece.Count));
    }

    // From the raw ids the collector reads to the bytes that go on the wire: each retainer is sent
    // as its digest, and no raw id appears anywhere in the payload.
    [Fact]
    public void Raw_retainer_ids_never_reach_the_wire()
    {
        var roster = TwoRetainers();
        var held = new[]
        {
            new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: Mogwin),
            new HeldSlot(Hat, 1, HeldPlaces.Market),
        };

        var wire = SyncFacts.Glamour(BuildFrom(held: held, roster: roster)).ToJsonString(ApiJson.Options);

        Assert.DoesNotContain(Mogwin.ToString(), wire);
        Assert.DoesNotContain(Kupo.ToString(), wire);
        Assert.Contains(RetainerIdHash.Compute(Mogwin), wire);
        Assert.Contains(RetainerIdHash.Compute(Kupo), wire);
    }

    // --- Order -----------------------------------------------------------------------------------

    // The same holdings must always produce the same payload, whatever slots they sit in.
    [Fact]
    public void The_dresser_is_ordered_by_id_then_quality()
    {
        var facts = BuildFrom(dresser: FinderOnly(Coat, 1_000_000 + Hat, Hat, 1601));

        Assert.Equal(
            """[{"id":1601},{"id":2642},{"id":2642,"hq":true},{"id":2965}]""",
            Json(facts.Dresser));
    }

    // Copies sharing an id and quality are ordered by their dyes, unknown dyes first, so the order
    // depends only on what is stored and never on which slot holds it.
    [Fact]
    public void Copies_of_one_piece_are_ordered_by_their_stains()
    {
        var reading = new DresserReading(
            FinderIds: new[] { Hat, Hat, Hat, Hat },
            FinderBits: new ushort[] { 0, 0, 0, 0 },
            LiveIds: new uint[] { Hat, Hat, Hat, 9999 },
            LiveStain0: new byte[] { 5, 0, 5, 1 },
            LiveStain1: new byte[] { 3, 7, 1, 1 });

        Assert.Equal(
            """[{"id":2642},{"id":2642,"stains":[0,7]},{"id":2642,"stains":[5,1]},{"id":2642,"stains":[5,3]}]""",
            Json(BuildFrom(dresser: reading).Dresser));
    }

    // By outfit id, then by piece list compared element by element, where a list that runs out
    // first sorts first ([2642] before [2642, 2965]).
    [Fact]
    public void Outfits_are_ordered_by_id_then_pieces()
    {
        var reading = new DresserReading(
            FinderIds: new[] { OtherOutfitId, OutfitId, OutfitId, OutfitId },
            FinderBits: new ushort[] { 0, 0b100, 0, 0b1000 },
            LiveIds: null,
            LiveStain0: null,
            LiveStain1: null);

        Assert.Equal(
            """[{"outfitId":45094,"pieceIds":[2642]},{"outfitId":45094,"pieceIds":[2642,2965]},{"outfitId":45094,"pieceIds":[2965]},{"outfitId":45100,"pieceIds":[1200,3100]}]""",
            Json(BuildFrom(dresser: reading).OutfitGlamours));
    }

    // --- Caps ------------------------------------------------------------------------------------

    // The ceilings are the server's, written into docs/api-contract.md, and nothing in the plugin
    // would notice one drifting: a cap set too high sends a list the server rejects, and one set too
    // low withholds a real collection. So each value is spelled out here beside the contract's.
    //
    // Compared as a whole map against every constant GlamourCaps declares, read by reflection the
    // way CategoryKeyReflection reads the category keys, so a cap whose value drifts and a cap
    // nobody added a line for both fail. `IsLiteral` keeps only `const` fields, and
    // `GetRawConstantValue` reads a constant's value out of the compiled assembly.
    [Fact]
    public void Every_cap_matches_the_contract()
    {
        var contract = new Dictionary<string, int>
        {
            [nameof(GlamourCaps.Dresser)] = 1_000,
            [nameof(GlamourCaps.OutfitGlamours)] = 1_000,
            [nameof(GlamourCaps.Armoire)] = 5_000,
            [nameof(GlamourCaps.Held)] = 20_000,
            [nameof(GlamourCaps.HeldCount)] = 9_999,
            [nameof(GlamourCaps.Retainers)] = 10,
            [nameof(GlamourCaps.RetainerName)] = 32,
        };

        var declared = typeof(GlamourCaps)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);

        Assert.Equal(contract, declared);
    }

    // Which list a cap test fills. An enum is a named set of constants, like a TypeScript string
    // union `'dresser' | 'outfitGlamours' | ...`; it is public because the public test methods below
    // take it as a parameter.
    public enum CappedList
    {
        Dresser,
        OutfitGlamours,
        Armoire,
        Held,
    }

    // Builds a snapshot whose chosen list holds exactly `length` entries. Ids 1..length are non-zero,
    // distinct, and none of them is a set id in the index, so each one survives as its own entry.
    private static GlamourBuildResult BuildWithLength(CappedList list, int length)
    {
        var ids = Enumerable.Range(1, length).Select(i => (uint)i).ToArray();

        // A `switch` expression picks one result by matching the value against each arm in turn,
        // like a lookup object of functions in TypeScript; `_` is the catch-all arm.
        return list switch
        {
            CappedList.Dresser =>
                GlamourSnapshot.Build(
                    FinderOnly(ids), SetIndex, null, NothingHeld, RetainerRoster.Empty, _ => true),
            CappedList.OutfitGlamours =>
                GlamourSnapshot.Build(
                    FinderOnly(Enumerable.Repeat(OutfitId, length).ToArray()),
                    SetIndex,
                    null,
                    NothingHeld,
                    RetainerRoster.Empty,
                    _ => true),
            CappedList.Armoire =>
                GlamourSnapshot.Build(
                    null, SetIndex, ids, NothingHeld, RetainerRoster.Empty, _ => true),
            CappedList.Held =>
                GlamourSnapshot.Build(
                    null, SetIndex, null, OneEach(ids), RetainerRoster.Empty, _ => true),
            _ => throw new ArgumentOutOfRangeException(nameof(list)),
        };
    }

    // How many entries the chosen list of a built snapshot holds. The `!` tells the compiler a list
    // it types as nullable is not null here: every cap test reads the very container it filled.
    private static int LengthOf(GlamourFacts facts, CappedList list) => list switch
    {
        CappedList.Dresser => facts.Dresser!.Count,
        CappedList.OutfitGlamours => facts.OutfitGlamours!.Count,
        CappedList.Armoire => facts.Armoire!.Count,
        CappedList.Held => facts.Held.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(list)),
    };

    // The caps are inclusive: a list exactly at the server's ceiling is accepted whole.
    // A [Theory] runs its body once per [InlineData] row, like `it.each([...])` in Jest.
    [Theory]
    [InlineData(CappedList.Dresser, GlamourCaps.Dresser)]
    [InlineData(CappedList.OutfitGlamours, GlamourCaps.OutfitGlamours)]
    [InlineData(CappedList.Armoire, GlamourCaps.Armoire)]
    [InlineData(CappedList.Held, GlamourCaps.Held)]
    public void A_list_exactly_at_its_cap_is_kept(CappedList list, int cap)
    {
        var built = BuildWithLength(list, cap);

        Assert.NotNull(built.Facts);
        Assert.Equal(cap, LengthOf(built.Facts, list));
    }

    // One past the ceiling and the server would reject the key, and a truncated list would read as
    // "the rest is gone" — so nothing is sent rather than something wrong. The reason names the cap,
    // so a pasted diagnostic tells this apart from a reading the plugin could not interpret.
    [Theory]
    [InlineData(CappedList.Dresser, GlamourCaps.Dresser)]
    [InlineData(CappedList.OutfitGlamours, GlamourCaps.OutfitGlamours)]
    [InlineData(CappedList.Armoire, GlamourCaps.Armoire)]
    [InlineData(CappedList.Held, GlamourCaps.Held)]
    public void An_over_cap_list_withholds_the_whole_snapshot(CappedList list, int cap)
    {
        var built = BuildWithLength(list, cap + 1);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    // The held cap counts entries on the wire, and only gear goes on the wire. A character holding
    // more distinct non-gear items than the cap (materials, consumables, minions) holds nothing the
    // cap is about, so the snapshot is built, with the one piece of gear held beside them.
    [Fact]
    public void More_distinct_non_gear_items_than_the_held_cap_do_not_withhold_the_snapshot()
    {
        const uint gear = 50_000;
        var nonGear = Enumerable.Range(1, GlamourCaps.Held + 1).Select(i => (uint)i).ToArray();

        // `Append` adds one more element to the end of a sequence, like `[...nonGear, gear]`.
        var facts = BuildFrom(held: OneEach(nonGear.Append(gear).ToArray()), isGear: id => id == gear);

        Assert.Equal("""[{"id":50000,"place":"bags","count":1}]""", Json(facts.Held));
    }

    // The count cap is inclusive too. The copies are split across two slots so the cap is pinned
    // against the summed count, which is what goes on the wire.
    [Fact]
    public void A_held_count_exactly_at_its_cap_is_kept()
    {
        var stored = InBags((Coat, GlamourCaps.HeldCount - 1), (1_000_000 + Coat, 1));

        var piece = Assert.Single(BuildFrom(held: stored).Held);

        Assert.Equal((uint)GlamourCaps.HeldCount, piece.Count);
    }

    // A summed count one past the cap withholds the whole snapshot rather than being clamped
    // (GlamourSnapshot's class remarks say why).
    [Fact]
    public void A_held_count_past_its_cap_withholds_the_whole_snapshot()
    {
        var stored = InBags((Coat, GlamourCaps.HeldCount), (1_000_000 + Coat, 1));

        var built = GlamourSnapshot.Build(null, SetIndex, null, stored, RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    // A count past the cap on something that is not gear is dropped with it, and does not withhold
    // the snapshot (GlamourSnapshot.Build says why the cap applies to gear only).
    [Fact]
    public void A_large_count_of_something_that_is_not_gear_is_dropped_not_withheld()
    {
        const uint material = 5;
        var stored = InBags((material, 999), (material, GlamourCaps.HeldCount), (Hat, 1));

        var facts = BuildFrom(held: stored, isGear: id => id != material);

        Assert.Equal("""[{"id":2642,"place":"bags","count":1}]""", Json(facts.Held));
    }

    // Two quantities whose 32-bit sum would wrap to 0 are withheld as over the cap
    // (GlamourSnapshot.SumHeldCopies says why the sum is 64-bit).
    [Fact]
    public void Summing_misread_quantities_cannot_wrap_into_a_plausible_count()
    {
        var stored = InBags((Coat, uint.MaxValue), (Coat, 1));

        var built = GlamourSnapshot.Build(null, SetIndex, null, stored, RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    // --- CountPieces -----------------------------------------------------------------------------

    // One piece in several places is still one piece. An outfit's own set id is not a piece — only
    // the pieces stored inside it are. A held piece counts once whatever its count: the number is
    // of distinct pieces, not of copies.
    [Fact]
    public void CountPieces_counts_each_piece_once()
    {
        var facts = new GlamourFacts
        {
            Dresser = new[]
            {
                new DresserPiece { Id = Hat },
                new DresserPiece { Id = Hat, Hq = true },
            },
            OutfitGlamours = new[]
            {
                new OutfitGlamour { OutfitId = OutfitId, PieceIds = new[] { Hat, Coat } },
            },
            Armoire = new uint[] { 3747, Coat },
            Held = new[]
            {
                new HeldPiece { Id = 1601, Place = HeldPlaces.Bags, Count = 3 },
                new HeldPiece { Id = Coat, Place = HeldPlaces.Retainer, Retainer = 1, Count = 1 },
            },
        };

        // Hat, Coat, 3747 and 1601.
        Assert.Equal(4, GlamourSnapshot.CountPieces(facts));
    }

    [Fact]
    public void CountPieces_skips_unread_containers()
    {
        var oneHeld = new GlamourFacts
        {
            Held = new[]
            {
                new HeldPiece { Id = 1601, Place = HeldPlaces.Bags, Count = 2 },
                new HeldPiece { Id = 1601, Place = HeldPlaces.Equipped, Count = 1 },
            },
        };
        var nothingHeld = new GlamourFacts { Held = Array.Empty<HeldPiece>() };

        Assert.Equal(1, GlamourSnapshot.CountPieces(oneHeld));
        Assert.Equal(0, GlamourSnapshot.CountPieces(nothingHeld));
    }
}
