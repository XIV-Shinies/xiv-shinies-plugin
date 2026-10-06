using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The items category's rules for a slot read from one of the item finder's copies (the saddlebag,
// a retainer, the glamour dresser): how a stored id is split into an item and its quality, which
// slots are tallied at all, and how a dresser slot holding an outfit stands in for the pieces
// inside it. Reading those copies out of the game is verified by in-game QA.
public class CachedTallyTests
{
    // A realistic outfit: set 45094 stores a Head piece (column 2) and a Body piece (column 3).
    private const uint OutfitId = 45094;
    private const uint Hat = 2642;
    private const uint Coat = 2965;

    // The offset the item finder adds to a high-quality copy's id (see GameItemId).
    private const uint HqOffset = 1_000_000;

    // The set index a dresser slot is expanded through: set id -> its 11 piece columns, empty
    // columns as 0, exactly as MirageSetIndex.Build produces it.
    private static readonly IReadOnlyDictionary<uint, uint[]> SetIndex = new Dictionary<uint, uint[]>
    {
        [OutfitId] = new uint[] { 0, 0, Hat, Coat, 0, 0, 0, 0, 0, 0, 0 },
    };

    // A manifest as the collector holds it: a set of the item ids the server asked about.
    // `params uint[] ids` lets a call list ids directly, like a TypeScript rest parameter.
    private static HashSet<uint> Manifest(params uint[] ids) => new(ids);

    // --- Stored ids ------------------------------------------------------------------------------

    // The item finder keeps no quality flag, so a high-quality copy is recognized by its encoded id
    // and tallied in the Hq bucket under the plain item id the manifest lists.
    [Fact]
    public void An_hq_encoded_stored_id_tallies_as_hq_under_its_base_id()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddStoredId(cached, Manifest(Coat), HqOffset + Coat, quantity: 2);

        // One entry, keyed by the base id: iterating a Dictionary yields key/value pairs.
        var entry = Assert.Single(cached);
        Assert.Equal(Coat, entry.Key);
        Assert.Equal(new ItemTally(Nq: 0, Hq: 2, Collectable: 0), entry.Value);
    }

    [Fact]
    public void A_plain_stored_id_tallies_as_normal_quality()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddStoredId(cached, Manifest(Coat), Coat, quantity: 3);

        Assert.Equal(new ItemTally(Nq: 3, Hq: 0, Collectable: 0), cached[Coat]);
    }

    // Both forms of an empty slot, a bare one million and a raw 0 (see GameItemId), are skipped even
    // when a malformed manifest lists 0.
    [Fact]
    public void An_empty_slot_is_never_tallied()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddStoredId(cached, Manifest(0, Coat), HqOffset, quantity: 1);
        ItemTallies.AddStoredId(cached, Manifest(0, Coat), 0, quantity: 1);

        Assert.Empty(cached);
    }

    // Only what the server asked about is counted; everything else the copy holds is ignored.
    [Fact]
    public void An_id_outside_the_manifest_is_not_tallied()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddStoredId(cached, Manifest(Hat), Coat, quantity: 1);

        Assert.Empty(cached);
    }

    // The same item can sit in several slots and several containers; every copy adds to one tally,
    // each in its own quality bucket.
    [Fact]
    public void Copies_in_several_slots_sum_into_one_tally()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddStoredId(cached, Manifest(Coat), Coat, quantity: 2);
        ItemTallies.AddStoredId(cached, Manifest(Coat), HqOffset + Coat, quantity: 1);
        ItemTallies.AddStoredId(cached, Manifest(Coat), Coat, quantity: 1);

        Assert.Equal(new ItemTally(Nq: 3, Hq: 1, Collectable: 0), cached[Coat]);
    }

    // --- Glamour dresser slots -------------------------------------------------------------------

    [Fact]
    public void A_loose_hq_dresser_slot_tallies_as_hq()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddDresserSlot(cached, Manifest(Hat), SetIndex, HqOffset + Hat, setBits: 0);

        Assert.Equal(new ItemTally(Nq: 0, Hq: 1, Collectable: 0), cached[Hat]);
    }

    // Every piece of an outfit glamour shares one quality (a game rule), so the pieces stored in a
    // high-quality outfit slot are tallied as high quality, one copy each.
    [Fact]
    public void An_hq_outfit_slots_stored_pieces_tally_as_hq()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddDresserSlot(
            cached, Manifest(Hat, Coat), SetIndex, HqOffset + OutfitId, setBits: 0);

        Assert.Equal(new ItemTally(Nq: 0, Hq: 1, Collectable: 0), cached[Hat]);
        Assert.Equal(new ItemTally(Nq: 0, Hq: 1, Collectable: 0), cached[Coat]);
        Assert.Equal(2, cached.Count);
    }

    // Bit 2 is the Head column, and a set bit means that piece has been taken out of the outfit,
    // so only the piece still inside is counted.
    [Fact]
    public void A_withdrawn_piece_is_not_tallied()
    {
        var cached = new Dictionary<uint, ItemTally>();

        ItemTallies.AddDresserSlot(cached, Manifest(Hat, Coat), SetIndex, OutfitId, setBits: 0b100);

        Assert.False(cached.ContainsKey(Hat));
        Assert.Equal(new ItemTally(Nq: 1, Hq: 0, Collectable: 0), cached[Coat]);
    }

    // The slot's own id is a set id, not one of the pieces. It is counted only when the manifest
    // asks about that id in its own right, and asking about it never stops the pieces from counting.
    [Fact]
    public void An_outfit_slots_own_set_id_is_tallied_only_when_the_manifest_lists_it()
    {
        var withoutSetId = new Dictionary<uint, ItemTally>();
        var withSetId = new Dictionary<uint, ItemTally>();

        ItemTallies.AddDresserSlot(withoutSetId, Manifest(Hat), SetIndex, OutfitId, setBits: 0);
        ItemTallies.AddDresserSlot(withSetId, Manifest(Hat, OutfitId), SetIndex, OutfitId, setBits: 0);

        Assert.False(withoutSetId.ContainsKey(OutfitId));
        Assert.Equal(new ItemTally(Nq: 1, Hq: 0, Collectable: 0), withSetId[OutfitId]);
        Assert.Equal(new ItemTally(Nq: 1, Hq: 0, Collectable: 0), withSetId[Hat]);
    }
}
