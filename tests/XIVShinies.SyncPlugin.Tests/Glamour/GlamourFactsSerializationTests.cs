using System;
using System.Linq;
using System.Reflection;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// Pins the glamour category's wire shape exactly. The server reads this category as CURRENT
// holdings, so the difference between an omitted key ("not read this pass") and an empty array
// ("read, and it holds nothing") decides whether the server keeps or clears what it knew — these
// tests are the tripwire that fires if a key is renamed, a casing slips, or an unread container
// starts serializing as [].
public class GlamourFactsSerializationTests
{
    // Serializes exactly the way a collector will: into a JSON node through SyncFacts, then to text
    // under the shared policy. Comparing whole strings pins key names, key order and omissions at once.
    private static string Wire(GlamourFacts facts) =>
        SyncFacts.Glamour(facts).ToJsonString(ApiJson.Options);

    // A stand-in for a retainer's hashed id: 64 lowercase hex characters, like the real digest.
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void The_full_shape_serializes_to_the_contract_json()
    {
        var facts = new GlamourFacts
        {
            Dresser = new[] { new DresserPiece { Id = 2642, Hq = true, Stains = new[] { 12, 0 } } },
            OutfitGlamours = new[]
            {
                new OutfitGlamour { OutfitId = 45094, PieceIds = new uint[] { 2642, 2965 } },
            },
            Armoire = new uint[] { 3747 },
            Held = new[]
            {
                new HeldPiece { Id = 1601, Place = HeldPlaces.Bags, Count = 1 },
                new HeldPiece { Id = 2965, Place = HeldPlaces.Retainer, Retainer = 1, Count = 2 },
                new HeldPiece { Id = 2965, Place = HeldPlaces.Market, Retainer = 1, Count = 1 },
            },
            Retainers = new[]
            {
                new RetainerEntry { Key = 1, Id = Digest, Name = "Mogwin", Market = true },
            },
        };

        // `$$"""…"""` is an interpolated raw string: `{{Digest}}` inserts the value, while a single
        // brace stays a literal brace, so the JSON's own braces need no escaping.
        Assert.Equal(
            $$"""{"version":1,"dresser":[{"id":2642,"hq":true,"stains":[12,0]}],"outfitGlamours":[{"outfitId":45094,"pieceIds":[2642,2965]}],"armoire":[3747],"held":[{"id":1601,"place":"bags","count":1},{"id":2965,"place":"retainer","retainer":1,"count":2},{"id":2965,"place":"market","retainer":1,"count":1}],"retainers":[{"key":1,"id":"{{Digest}}","name":"Mogwin","market":true}]}""",
            Wire(facts));
    }

    // The place names and their order are the server's: the wire spells them exactly so, and the
    // held list sorts by this order.
    [Fact]
    public void The_places_are_the_contracts_in_its_order()
    {
        Assert.Equal(
            new[] { "bags", "armory", "equipped", "saddlebag", "retainer", "market" },
            HeldPlaces.InWireOrder);
    }

    // Every place constant appears in the order list exactly once, so a place added later cannot be
    // left out of it and sort last unnoticed. The constants are read by reflection: `IsLiteral` keeps
    // only `const` fields, and `GetRawConstantValue` reads a constant's value.
    [Fact]
    public void Every_place_constant_is_in_the_order_list_once()
    {
        var constants = typeof(HeldPlaces)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order();

        Assert.Equal(constants, HeldPlaces.InWireOrder.Order());
    }

    // A held entry outside a retainer is an id, a place and a count, with no retainer key: the count
    // is what tells the server how many outfit glamours the copies can fill, so it is never omitted,
    // even at 1.
    [Fact]
    public void A_held_piece_carries_its_id_place_and_count()
    {
        var facts = new GlamourFacts
        {
            Held = new[] { new HeldPiece { Id = 2965, Place = HeldPlaces.Bags, Count = 1 } },
        };

        Assert.Equal("""{"version":1,"held":[{"id":2965,"place":"bags","count":1}]}""", Wire(facts));
    }

    // A retainer the game has no name for, whose listings were not read, is its key and its id.
    [Fact]
    public void A_retainer_without_a_name_or_listings_carries_only_its_key_and_id()
    {
        var facts = new GlamourFacts
        {
            Held = Array.Empty<HeldPiece>(),
            Retainers = new[] { new RetainerEntry { Key = 2, Id = Digest } },
        };

        Assert.Equal(
            $$"""{"version":1,"held":[],"retainers":[{"key":2,"id":"{{Digest}}"}]}""", Wire(facts));
    }

    // A normal-quality piece whose dyes could not be read this pass carries its id and nothing
    // else: "hq" appears only when true, and "stains" only when they were read.
    [Fact]
    public void A_plain_dresser_piece_carries_only_its_id()
    {
        var facts = new GlamourFacts
        {
            Dresser = new[] { new DresserPiece { Id = 2642, Hq = null, Stains = null } },
            Held = Array.Empty<HeldPiece>(),
        };

        Assert.Equal("""{"version":1,"dresser":[{"id":2642}],"held":[]}""", Wire(facts));
    }

    // A container that was not read is absent from the JSON entirely, so the server keeps what it
    // already knew about it instead of reading the silence as "everything is gone".
    [Fact]
    public void Unread_containers_are_omitted()
    {
        var facts = new GlamourFacts
        {
            Dresser = null,
            OutfitGlamours = null,
            Armoire = null,
            Held = new[] { new HeldPiece { Id = 1601, Place = HeldPlaces.Bags, Count = 1 } },
        };

        Assert.Equal("""{"version":1,"held":[{"id":1601,"place":"bags","count":1}]}""", Wire(facts));
    }

    // The other half of the same rule: a container that WAS read and holds nothing must still be
    // sent, as [], because that is how the server learns its old contents are gone.
    [Fact]
    public void Read_empty_containers_stay_empty_arrays()
    {
        var facts = new GlamourFacts
        {
            Dresser = Array.Empty<DresserPiece>(),
            OutfitGlamours = Array.Empty<OutfitGlamour>(),
            Armoire = Array.Empty<uint>(),
            Held = Array.Empty<HeldPiece>(),
        };

        Assert.Equal(
            """{"version":1,"dresser":[],"outfitGlamours":[],"armoire":[],"held":[]}""",
            Wire(facts));
    }
}
