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

// The glamour category's facts, built from readings of the dresser, the Armoire and the held gear.
// Lists compare as serialized JSON, what the server sees, since a record compares lists by reference.
public class GlamourSnapshotTests
{
    // A realistic outfit: set 45094 stores a Head piece (column 2) and a Body piece (column 3).
    private const uint OutfitId = 45094;
    private const uint Hat = 2642;
    private const uint Coat = 2965;

    // Its columns list the pieces in descending id order, so a test can tell sorting from copying.
    private const uint OtherOutfitId = 45100;

    // A test-only row that names one item in two columns.
    private const uint RepeatedPieceOutfitId = 45200;
    private const uint RepeatedPiece = 4100;

    // Set id to its 11 piece columns, empty columns as 0, the shape MirageSetIndex.Build produces.
    private static readonly IReadOnlyDictionary<uint, uint[]> SetIndex = new Dictionary<uint, uint[]>
    {
        [OutfitId] = new uint[] { 0, 0, Hat, Coat, 0, 0, 0, 0, 0, 0, 0 },
        [OtherOutfitId] = new uint[] { 0, 0, 3100, 1200, 0, 0, 0, 0, 0, 0, 0 },
        [RepeatedPieceOutfitId] = new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, RepeatedPiece, RepeatedPiece },
    };

    // A dresser read with no live copy loaded, every outfit complete (all bits clear).
    private static DresserReading FinderOnly(params uint[] ids) =>
        new(ids, new ushort[ids.Length], LiveIds: null, LiveStain0: null, LiveStain1: null);

    // One copy each in the bags, the common case: gear never stacks.
    private static HeldSlot[] OneEach(params uint[] storedIds) =>
        storedIds.Select(id => new HeldSlot(id, 1, HeldPlaces.Bags)).ToArray();

    private static HeldSlot[] InBags(params (uint StoredId, uint Quantity)[] slots) =>
        slots.Select(slot => new HeldSlot(slot.StoredId, slot.Quantity, HeldPlaces.Bags)).ToArray();

    private static readonly HeldSlot[] NothingHeld = Array.Empty<HeldSlot>();

    // Two retainers read this pass, with Kupo's market listings read too.
    private const ulong Mogwin = 33_000_000_001;
    private const ulong Kupo = 33_000_000_002;

    private static RetainerRoster TwoRetainers() => RetainerRoster.Build(
        new[] { Mogwin, Kupo },
        new Dictionary<ulong, string> { [Mogwin] = "Mogwin" },
        marketRetainerId: Kupo);

    // An input a test leaves out is unread or empty, and every item counts as gear. Every caller
    // stays within the caps with a consistent reading, so a withheld result fails here, naming why.
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

    // The shared JSON policy, so an assertion compares the text the server receives.
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ApiJson.Options);

    // --- Absent vs empty -------------------------------------------------------------------------

    // GlamourFacts' remarks say why null and [] differ.
    [Fact]
    public void Unread_containers_stay_null()
    {
        var facts = BuildFrom(dresser: null, armoire: null);

        Assert.Null(facts.Dresser);
        Assert.Null(facts.OutfitGlamours);
        Assert.Null(facts.Armoire);

        // Held has no "not read" state, so it is empty, never null.
        Assert.Empty(facts.Held);
    }

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

    // A raw 0 and a bare one million are both empty slots (see GameItemId).
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

    // A slot whose id is a set id holds an outfit, and is never also a loose piece.
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

    // The game stores a high-quality outfit glamour as its set id plus one million.
    [Fact]
    public void An_hq_outfit_slot_is_still_an_outfit()
    {
        var facts = BuildFrom(dresser: FinderOnly(1_000_000 + OutfitId));

        var outfit = Assert.Single(facts.OutfitGlamours!);
        Assert.Equal(OutfitId, outfit.OutfitId);
        Assert.Empty(facts.Dresser!);
    }

    [Fact]
    public void Outfit_pieces_are_listed_ascending()
    {
        var outfit = Assert.Single(BuildFrom(dresser: FinderOnly(OtherOutfitId)).OutfitGlamours!);

        Assert.Equal(new uint[] { 1200, 3100 }, outfit.PieceIds);
    }

    // The wire lists which pieces the outfit holds, not what each column says.
    [Fact]
    public void An_outfit_lists_each_piece_once()
    {
        var outfit = Assert.Single(BuildFrom(dresser: FinderOnly(RepeatedPieceOutfitId)).OutfitGlamours!);

        Assert.Equal(new uint[] { RepeatedPiece }, outfit.PieceIds);
    }

    // Bits 2 and 3 withdraw both pieces, but the slot still stores the outfit.
    [Fact]
    public void A_fully_withdrawn_outfit_is_still_reported()
    {
        var reading = new DresserReading(new[] { OutfitId }, new ushort[] { 0b1100 }, null, null, null);

        Assert.Equal(
            """[{"outfitId":45094,"pieceIds":[]}]""",
            Json(BuildFrom(dresser: reading).OutfitGlamours));
    }

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

    // GlamourSnapshot.CanInterpret says why a length mismatch withholds everything.
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

    // GlamourSnapshot.CanInterpret says why an empty set index withholds everything.
    [Fact]
    public void An_empty_set_index_withholds_a_read_dresser()
    {
        var noOutfitRows = new Dictionary<uint, uint[]>();

        var built = GlamourSnapshot.Build(
            FinderOnly(OutfitId, Hat), noOutfitRows, null, NothingHeld, RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.UnexpectedLayout, built.WithheldReason);
    }

    // The set index only interprets the dresser, so with none read the rest still goes out.
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

    // The live slot's dyes belong to a different piece, but this piece is still a known fact.
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
    // three arrays leaves the dyes unknown.
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

    // The slots it does reach keep their dyes.
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

    // On an outfit slot the two stain bytes are the low and high byte of its outfit-piece field
    // (112 + 7 × 256 = 1904, the item finder's bits). A loose slot beside it still gets real dyes.
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

    // Both copies share the high-quality encoding, so a different quality is a different stored id.
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

    // Held slots arrive raw: high-quality encoded, repeated, empty, or not gear at all.
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

    // Every outfit glamour needs a copy of its own, and either quality is a copy of the same piece.
    [Fact]
    public void Held_sums_copies_across_slots_and_qualities()
    {
        var stored = InBags((Coat, 1), (1_000_000 + Coat, 1), (Coat, 2));

        var piece = Assert.Single(BuildFrom(held: stored).Held);

        Assert.Equal(Coat, piece.Id);
        Assert.Equal(4u, piece.Count);
    }

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

    // Handed the raw high-quality id, the caller would not recognize it and the piece is lost.
    [Fact]
    public void Held_asks_about_base_ids()
    {
        var facts = BuildFrom(held: OneEach(1_000_000 + Coat), isGear: id => id == Coat);

        Assert.Equal("""[{"id":2965,"place":"bags","count":1}]""", Json(facts.Held));
    }

    // Each answer is an item sheet lookup, so asking per copy repeats it for nothing, and 0 is no
    // item at all.
    [Fact]
    public void The_gear_question_is_asked_once_per_distinct_base_id()
    {
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

    // So the server can show where each copy sits. The entries come in the contract's place order.
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

    [Fact]
    public void Copies_outside_a_retainer_carry_no_key()
    {
        var piece = Assert.Single(BuildFrom(held: OneEach(Coat), roster: TwoRetainers()).Held);

        Assert.Null(piece.Retainer);
        Assert.Equal("""[{"id":2965,"place":"bags","count":1}]""", Json(new[] { piece }));
    }

    [Fact]
    public void Market_copies_carry_the_market_retainers_key()
    {
        var roster = TwoRetainers();

        var piece = Assert.Single(
            BuildFrom(held: new[] { new HeldSlot(Coat, 1, HeldPlaces.Market) }, roster: roster).Held);

        Assert.Equal(HeldPlaces.Market, piece.Place);
        Assert.Equal(roster.MarketKey, piece.Retainer);
    }

    // A market entry must name the retainer it was listed on.
    [Fact]
    public void Market_copies_are_dropped_without_a_market_retainer()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin }, new Dictionary<ulong, string>(), null);

        var facts = BuildFrom(held: new[] { new HeldSlot(Coat, 1, HeldPlaces.Market) }, roster: roster);

        Assert.Empty(facts.Held);
    }

    // With no key to carry, it would point at nothing.
    [Fact]
    public void A_retainer_copy_from_a_retainer_not_on_the_roster_is_dropped()
    {
        var held = new[] { new HeldSlot(Coat, 1, HeldPlaces.Retainer, RetainerId: 99) };

        Assert.Empty(BuildFrom(held: held, roster: TwoRetainers()).Held);
    }

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

    [Fact]
    public void The_retainers_list_is_the_roster()
    {
        var roster = TwoRetainers();

        var facts = BuildFrom(roster: roster);

        Assert.Equal(Json(roster.Entries), Json(facts.Retainers));
    }

    [Fact]
    public void No_retainers_read_leaves_the_list_out()
    {
        Assert.Null(BuildFrom(roster: RetainerRoster.Empty).Retainers);
    }

    // The game allows ten retainers and the contract keys 1 to 10, so an eleventh is a misread the
    // server would reject.
    [Fact]
    public void More_than_ten_retainers_withhold_the_whole_snapshot()
    {
        var eleven = Enumerable.Range(1, GlamourCaps.Retainers + 1).Select(i => (ulong)i).ToArray();
        var roster = RetainerRoster.Build(eleven, new Dictionary<ulong, string>(), null);

        var built = GlamourSnapshot.Build(null, SetIndex, null, NothingHeld, roster, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    [Fact]
    public void Ten_retainers_are_kept()
    {
        var ten = Enumerable.Range(1, GlamourCaps.Retainers).Select(i => (ulong)i).ToArray();
        var roster = RetainerRoster.Build(ten, new Dictionary<ulong, string>(), null);

        Assert.Equal(GlamourCaps.Retainers, BuildFrom(roster: roster).Retainers!.Count);
    }

    // Slots arrive in reverse. The listing stays its own entry though Kupo also holds a copy.
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

        Assert.Equal(
            $$"""[{"id":2965,"place":"retainer","retainer":{{roster.KeyOf(Mogwin)}},"count":2},""" +
            $$"""{"id":2965,"place":"retainer","retainer":{{roster.KeyOf(Kupo)}},"count":1},""" +
            $$"""{"id":2965,"place":"market","retainer":{{roster.KeyOf(Kupo)}},"count":1}]""",
            Json(BuildFrom(held: held, roster: roster).Held));
        Assert.True(roster.KeyOf(Mogwin) < roster.KeyOf(Kupo));
    }

    // A retainer's saved copy stores a high-quality piece in the encoded form.
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

    // Each entry is within the cap, though their total passes it.
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

    // A raw retainer id must never leave the process, so this follows it to the wire text.
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

    // Unknown dyes first, so the order never depends on which slot holds a copy.
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
            """[{"id":2642},{"id":2642,"stains":[0,7]},""" +
            """{"id":2642,"stains":[5,1]},{"id":2642,"stains":[5,3]}]""",
            Json(BuildFrom(dresser: reading).Dresser));
    }

    // Piece lists compare element by element, so [2642] sorts before [2642, 2965].
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
            """[{"outfitId":45094,"pieceIds":[2642]},{"outfitId":45094,"pieceIds":[2642,2965]},""" +
            """{"outfitId":45094,"pieceIds":[2965]},{"outfitId":45100,"pieceIds":[1200,3100]}]""",
            Json(BuildFrom(dresser: reading).OutfitGlamours));
    }

    // --- Caps ------------------------------------------------------------------------------------

    // The ceilings are the server's (docs/api-contract.md): too high sends a list it rejects, too low
    // withholds a real collection. Every constant is compared, so a cap with no line here fails too.
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

    // Which list a cap test fills. Public because the test methods take it as a parameter.
    public enum CappedList
    {
        Dresser,
        OutfitGlamours,
        Armoire,
        Held,
    }

    // Fills the chosen list with exactly `length` entries. Ids 1 to length are non-zero, distinct
    // and not set ids, so each survives as its own entry.
    private static GlamourBuildResult BuildWithLength(CappedList list, int length)
    {
        var ids = Enumerable.Range(1, length).Select(i => (uint)i).ToArray();

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

    // Each cap test reads the list it filled, so none is null here.
    private static int LengthOf(GlamourFacts facts, CappedList list) => list switch
    {
        CappedList.Dresser => facts.Dresser!.Count,
        CappedList.OutfitGlamours => facts.OutfitGlamours!.Count,
        CappedList.Armoire => facts.Armoire!.Count,
        CappedList.Held => facts.Held.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(list)),
    };

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

    // GlamourSnapshot's remarks say why it is withheld, not truncated, and GlamourBuildResult's why
    // OverCap is its own reason.
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

    // The held cap counts wire entries, and only gear goes on the wire.
    [Fact]
    public void More_distinct_non_gear_items_than_the_held_cap_do_not_withhold_the_snapshot()
    {
        const uint gear = 50_000;
        var nonGear = Enumerable.Range(1, GlamourCaps.Held + 1).Select(i => (uint)i).ToArray();

        var facts = BuildFrom(held: OneEach(nonGear.Append(gear).ToArray()), isGear: id => id == gear);

        Assert.Equal("""[{"id":50000,"place":"bags","count":1}]""", Json(facts.Held));
    }

    // Split across two slots, so the cap is pinned against the summed count that goes on the wire.
    [Fact]
    public void A_held_count_exactly_at_its_cap_is_kept()
    {
        var stored = InBags((Coat, GlamourCaps.HeldCount - 1), (1_000_000 + Coat, 1));

        var piece = Assert.Single(BuildFrom(held: stored).Held);

        Assert.Equal((uint)GlamourCaps.HeldCount, piece.Count);
    }

    // Withheld rather than clamped (GlamourSnapshot's remarks say why).
    [Fact]
    public void A_held_count_past_its_cap_withholds_the_whole_snapshot()
    {
        var stored = InBags((Coat, GlamourCaps.HeldCount), (1_000_000 + Coat, 1));

        var built = GlamourSnapshot.Build(null, SetIndex, null, stored, RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    // An inline comment in GlamourSnapshot.Build says why the count cap applies to gear only.
    [Fact]
    public void A_large_count_of_something_that_is_not_gear_is_dropped_not_withheld()
    {
        const uint material = 5;
        var stored = InBags((material, 999), (material, GlamourCaps.HeldCount), (Hat, 1));

        var facts = BuildFrom(held: stored, isGear: id => id != material);

        Assert.Equal("""[{"id":2642,"place":"bags","count":1}]""", Json(facts.Held));
    }

    // Their 32-bit sum would wrap to 0 (GlamourSnapshot.SumHeldCopies says why the sum is 64-bit).
    [Fact]
    public void Summing_misread_quantities_cannot_wrap_into_a_plausible_count()
    {
        var stored = InBags((Coat, uint.MaxValue), (Coat, 1));

        var built = GlamourSnapshot.Build(null, SetIndex, null, stored, RetainerRoster.Empty, _ => true);

        Assert.Null(built.Facts);
        Assert.Equal(CollectSkipReasons.OverCap, built.WithheldReason);
    }

    // --- CountPieces -----------------------------------------------------------------------------

    // Distinct pieces, not copies, and an outfit's own set id is not a piece.
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
