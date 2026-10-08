using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Appearance;

namespace XIVShinies.SyncPlugin.Tests.Appearance;

// Pins the appearance category's wire shape: every customize byte under its frozen name, the
// size check that turns a misread into no category at all, the crest bits, the two glasses slots,
// the four toggles, and the exact JSON the sync upload carries.
public class AppearanceSnapshotTests
{
    // The four toggle keys, in wire order.
    private static readonly string[] ToggleKeys =
        { "weaponHidden", "hatHidden", "visorToggled", "vieraEarsHidden" };

    // 26 bytes with a distinct value at every index, so a byte that lands under the wrong name
    // cannot match by coincidence. Ordinary bytes count up from 100; each of the five packed bytes
    // has bit 7 (0x80) set, which proves they travel raw rather than being unpacked or masked.
    private static byte[] DistinctCustomize()
    {
        var bytes = new byte[26];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i + 100);

        bytes[7] = 0x80; // HasHighlights: highlights on.
        bytes[12] = 0xC1; // FaceFeatures: features 1 and 7, plus the legacy tattoo.
        bytes[16] = 0x85; // EyeShape: shape 5 with the small iris.
        bytes[19] = 0x83; // LipStyle: style 3 with lipstick.
        bytes[24] = 0x81; // Facepaint: paint 1, reversed.

        return bytes;
    }

    // Builds with sensible defaults for whatever a test does not care about, and fails the test if
    // the builder declines. `int crestBits` rather than `byte` keeps InlineData's int literals
    // simple to pass through.
    private static JsonObject Build(
        byte[]? customize = null,
        AppearanceToggles toggles = default,
        int crestBits = 0,
        ushort[]? glasses = null)
    {
        var facts = AppearanceSnapshot.Build(
            customize ?? DistinctCustomize(),
            toggles,
            (byte)crestBits,
            glasses ?? new ushort[] { 0, 0 });

        Assert.NotNull(facts);
        return facts;
    }

    // --- customize ------------------------------------------------------------------------------

    [Fact]
    public void Every_byte_lands_under_its_name_with_its_raw_value()
    {
        var bytes = DistinctCustomize();

        var customize = Build(customize: bytes)["customize"]!.AsObject();

        Assert.Equal(26, customize.Count);
        for (var i = 0; i < bytes.Length; i++)
            Assert.Equal((int)bytes[i], customize[CustomizeFields.Names[i]]!.GetValue<int>());

        // Spot checks against literal names, so the loop above cannot pass by pairing a wrong table
        // with itself.
        Assert.Equal(100, customize["Race"]!.GetValue<int>());
        Assert.Equal(0x85, customize["EyeShape"]!.GetValue<int>());
        Assert.Equal(125, customize["FacepaintColor"]!.GetValue<int>());
    }

    // A JSON object's key order carries no meaning to a parser, but the server and anyone reading a
    // pasted payload see the bytes in the game's own order, which keeps the two easy to line up.
    [Fact]
    public void The_customize_keys_follow_byte_order()
    {
        var customize = Build()["customize"]!.AsObject();

        Assert.Equal(CustomizeFields.Names, customize.Select(pair => pair.Key));
    }

    // A read of any other size is a misread, so nothing is built and the category is not sent.
    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(27)]
    public void A_customize_read_of_the_wrong_size_builds_nothing(int length)
    {
        var facts = AppearanceSnapshot.Build(
            new byte[length], default, crestBits: 0, new ushort[] { 0, 0 });

        Assert.Null(facts);
    }

    // --- fcCrest --------------------------------------------------------------------------------

    // 0x01 is the off-hand, 0x02 the head and 0x04 the body. Each bit is checked alone, so a bit
    // wired to the wrong slot shows up as the wrong slot answering true. The other five bits mean
    // nothing, so a stray one (0x80 below) must leave every answer unchanged.
    [Theory]
    [InlineData(0x01, false, false, true)]
    [InlineData(0x02, true, false, false)]
    [InlineData(0x04, false, true, false)]
    [InlineData(0x07, true, true, true)]
    [InlineData(0x00, false, false, false)]
    [InlineData(0x80, false, false, false)]
    [InlineData(0x85, false, true, true)]
    public void Each_crest_bit_lands_under_its_own_slot(
        int crestBits, bool head, bool body, bool offHand)
    {
        var crest = Build(crestBits: crestBits)["fcCrest"]!.AsObject();

        Assert.Equal(head, crest["head"]!.GetValue<bool>());
        Assert.Equal(body, crest["body"]!.GetValue<bool>());
        Assert.Equal(offHand, crest["offHand"]!.GetValue<bool>());
    }

    // --- glasses --------------------------------------------------------------------------------

    // The wire always carries exactly two slots, whatever length the read produced.
    [Fact]
    public void A_single_glasses_entry_is_padded_with_zero()
    {
        var facts = Build(glasses: new ushort[] { 7 });

        Assert.Equal("[7,0]", facts["glasses"]!.ToJsonString());
    }

    [Fact]
    public void No_glasses_entries_send_two_zeros()
    {
        var facts = Build(glasses: Array.Empty<ushort>());

        Assert.Equal("[0,0]", facts["glasses"]!.ToJsonString());
    }

    [Fact]
    public void Glasses_entries_past_the_second_are_ignored()
    {
        var facts = Build(glasses: new ushort[] { 7, 9, 11 });

        Assert.Equal("[7,9]", facts["glasses"]!.ToJsonString());
    }

    // --- toggles --------------------------------------------------------------------------------

    // One toggle on at a time: with exactly one true value per build, a toggle wired to the wrong
    // key shows up as the wrong key being true. A pattern where two keys share a value could hide
    // a swap between them.
    [Theory]
    [InlineData("weaponHidden")]
    [InlineData("hatHidden")]
    [InlineData("visorToggled")]
    [InlineData("vieraEarsHidden")]
    public void Each_toggle_lands_under_its_own_key(string onKey)
    {
        var toggles = new AppearanceToggles(
            WeaponHidden: onKey == "weaponHidden",
            HatHidden: onKey == "hatHidden",
            VisorToggled: onKey == "visorToggled",
            VieraEarsHidden: onKey == "vieraEarsHidden");

        var facts = Build(toggles: toggles);

        foreach (var key in ToggleKeys)
            Assert.Equal(key == onKey, facts[key]!.GetValue<bool>());
    }

    // --- the whole shape ------------------------------------------------------------------------

    // One known input and the exact text it becomes. The customize keys stay PascalCase — the
    // shared policy camel-cases property names and dictionary keys, but a hand-built JsonObject is
    // written exactly as built.
    //
    // `"""…"""` is a raw string literal: everything between the delimiters is taken literally, so
    // the JSON's own quotes need no escaping. Each piece is split just before a comma because a
    // one-line raw string cannot begin or end with a quote character.
    private const string KnownJson =
        """{"version":1,"customize":{"Race":1,"Gender":1,"ModelType":1,"Height":50,"Tribe":2""" +
        ""","FaceType":3,"HairStyle":4,"HasHighlights":128,"SkinColor":5,"EyeColor":6""" +
        ""","HairColor":7,"HairColor2":8,"FaceFeatures":129,"FaceFeaturesColor":9,"Eyebrows":10""" +
        ""","EyeColor2":11,"EyeShape":130,"NoseShape":12,"JawShape":13,"LipStyle":131""" +
        ""","LipColor":14,"RaceFeatureSize":50,"RaceFeatureType":15,"BustSize":60""" +
        ""","Facepaint":132,"FacepaintColor":16},"glasses":[3,0]""" +
        ""","fcCrest":{"head":false,"body":true""" +
        ""","offHand":false},"weaponHidden":false,"hatHidden":false,"visorToggled":true""" +
        ""","vieraEarsHidden":false}""";

    private static JsonObject KnownFacts() => Build(
        customize: new byte[]
        {
            1, 1, 1, 50, 2, 3, 4, 0x80, 5, 6, 7, 8, 0x81, 9, 10, 11, 0x82, 12, 13, 0x83, 14, 50, 15,
            60, 0x84, 16,
        },
        toggles: new AppearanceToggles(
            WeaponHidden: false, HatHidden: false, VisorToggled: true, VieraEarsHidden: false),
        crestBits: 0x04,
        glasses: new ushort[] { 3, 0 });

    [Fact]
    public void A_known_appearance_serializes_to_the_contract_json()
    {
        Assert.Equal(KnownJson, KnownFacts().ToJsonString(ApiJson.Options));
    }

    // The same text, reached the way the upload really reaches it: as one value in a SyncRequest's
    // collections, serialized under the shared policy. This is the check that the policy's
    // camel-casing stops at the dictionary key and never reaches the customize names.
    [Fact]
    public void The_sync_upload_carries_the_appearance_exactly_as_built()
    {
        var request = new SyncRequest
        {
            CharacterContentIdHash = new string('a', 64),
            CharacterName = "Some Name",
            HomeWorld = "Excalibur",
            PluginVersion = "1.0.0",
            Trigger = SyncTrigger.Manual,
            Collections = new Dictionary<string, JsonNode> { ["appearance"] = KnownFacts() },
        };

        var json = JsonSerializer.Serialize(request, ApiJson.Options);

        Assert.Contains("\"appearance\":" + KnownJson, json);
    }
}
