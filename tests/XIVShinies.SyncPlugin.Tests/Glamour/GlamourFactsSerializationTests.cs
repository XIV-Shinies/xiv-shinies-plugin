using System;
using System.Linq;
using System.Reflection;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// The glamour category's wire shape. The server reads it as current holdings, so an omitted key
// ("not read") keeps what the server knew, and an empty array clears it.
public class GlamourFactsSerializationTests
{
    // The collector's own path to the wire. A whole string pins key names, key order and omissions.
    private static string Wire(GlamourFacts facts) =>
        SyncFacts.Glamour(facts).ToJsonString(ApiJson.Options);

    // Shaped like a real retainer digest: 64 lowercase hex characters.
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

        Assert.Equal(
            """{"version":1,"dresser":[{"id":2642,"hq":true,"stains":[12,0]}],"outfitGlamours":""" +
            """[{"outfitId":45094,"pieceIds":[2642,2965]}],"armoire":[3747],"held":""" +
            """[{"id":1601,"place":"bags","count":1},""" +
            """{"id":2965,"place":"retainer","retainer":1,"count":2},""" +
            """{"id":2965,"place":"market","retainer":1,"count":1}],"retainers":""" +
            $$"""[{"key":1,"id":"{{Digest}}","name":"Mogwin","market":true}]}""",
            Wire(facts));
    }

    // The names and their order are the server's: the wire spells them so, and the held list sorts
    // by this order.
    [Fact]
    public void The_places_are_the_contracts_in_its_order()
    {
        Assert.Equal(
            new[] { "bags", "armory", "equipped", "saddlebag", "retainer", "market" },
            HeldPlaces.InWireOrder);
    }

    // A place missing from the order list would sort last unnoticed.
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

    // The count is sent even at 1: it says how many outfit glamours the copies can fill.
    [Fact]
    public void A_held_piece_carries_its_id_place_and_count()
    {
        var facts = new GlamourFacts
        {
            Held = new[] { new HeldPiece { Id = 2965, Place = HeldPlaces.Bags, Count = 1 } },
        };

        Assert.Equal("""{"version":1,"held":[{"id":2965,"place":"bags","count":1}]}""", Wire(facts));
    }

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

    // "hq" is sent only when true, and "stains" only when they were read.
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
