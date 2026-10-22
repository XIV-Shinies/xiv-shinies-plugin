using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// Pins the occult upload to docs/api-contract.md § occult/instance-state. Two rules depart from
// the /sync serializer: sinceUtc is written as an explicit null, because the contract's zod schema
// is .nullable(), not .nullish(); and timestamps are whole-second UTC ending in "Z", since
// "+00:00" is rejected.
public class OccultUploadBuilderTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.FromUnixTimeSeconds(1786465621);

    // Reparsed so a test can tell an omitted key from a null one.
    private static JsonObject Serialize<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, ApiJson.Options))!.AsObject();

    private static OccultInstanceStateRequest Build(
        OccultTrigger trigger = OccultTrigger.Change, params OccultEncounterState[] encounters) =>
        OccultUploadBuilder.Build(
            characterContentIdHash: new string('a', 64),
            characterName: "Some Name",
            homeWorld: "Excalibur",
            pluginVersion: "1.0.0",
            territoryTypeId: 1252,
            trigger: trigger,
            encounters: encounters,
            currentWorldId: null);

    private static OccultEncounterState Ce(
        ushort id, OccultEncounterStatus status, DateTimeOffset? since = null) =>
        new() { IsFate = false, Id = id, Status = status, SinceUtc = since };

    private static OccultEncounterState Fate(
        ushort id, OccultEncounterStatus status, DateTimeOffset? since = null) =>
        new() { IsFate = true, Id = id, Status = status, SinceUtc = since };

    // Values, not just keys: Build takes four strings in a row, so a swapped pair would keep every
    // key while binding the wrong character.
    [Fact]
    public void The_request_carries_the_identity_values_under_the_contract_field_names()
    {
        var json = Serialize(Build());

        Assert.Equal(new string('a', 64), json["characterContentIdHash"]!.GetValue<string>());
        Assert.Equal("Some Name", json["characterName"]!.GetValue<string>());
        Assert.Equal("Excalibur", json["homeWorld"]!.GetValue<string>());
        Assert.Equal("1.0.0", json["pluginVersion"]!.GetValue<string>());
        Assert.True(json.ContainsKey("trigger"));
        Assert.True(json.ContainsKey("instance"));
        Assert.True(json.ContainsKey("encounters"));
    }

    // Every contract word, because the server rejects anything else.
    [Theory]
    [InlineData(OccultTrigger.Enter, "enter")]
    [InlineData(OccultTrigger.Change, "change")]
    [InlineData(OccultTrigger.Heartbeat, "heartbeat")]
    [InlineData(OccultTrigger.Leave, "leave")]
    public void Every_trigger_serializes_as_its_lowercase_contract_word(OccultTrigger trigger, string expected)
    {
        var json = Serialize(Build(trigger: trigger));
        Assert.Equal(expected, json["trigger"]!.GetValue<string>());
    }

    // The server reads an absent worldId as unscoped. Nothing else may ride along: the tracker is
    // resolved by fingerprint, so extra fields would imply an identity that does not exist.
    [Fact]
    public void The_instance_object_carries_only_the_territory_when_no_world_is_known()
    {
        var instance = Serialize(Build())["instance"]!.AsObject();

        Assert.Equal(1252, instance["territoryTypeId"]!.GetValue<int>());
        Assert.Single(instance);
    }

    // worldId is what scopes the tracker to a data center on the server.
    [Fact]
    public void The_instance_object_carries_the_current_world_when_known()
    {
        var request = OccultUploadBuilder.Build(
            characterContentIdHash: new string('a', 64),
            characterName: "Some Name",
            homeWorld: "Excalibur",
            pluginVersion: "1.0.0",
            territoryTypeId: 1252,
            trigger: OccultTrigger.Enter,
            encounters: [],
            currentWorldId: 73);

        var instance = Serialize(request)["instance"]!.AsObject();

        Assert.Equal(73, instance["worldId"]!.GetValue<int>());
        Assert.Equal(2, instance.Count);
    }

    [Fact]
    public void A_ce_row_carries_dynamicEventId_and_no_fateId()
    {
        var row = Serialize(Build(encounters: Ce(46, OccultEncounterStatus.Active, Since)))
            ["encounters"]!.AsArray()[0]!.AsObject();

        Assert.Equal(46, row["dynamicEventId"]!.GetValue<int>());
        Assert.False(row.ContainsKey("fateId"));
    }

    [Fact]
    public void A_fate_row_carries_fateId_and_no_dynamicEventId()
    {
        var row = Serialize(Build(encounters: Fate(1972, OccultEncounterStatus.Active, Since)))
            ["encounters"]!.AsArray()[0]!.AsObject();

        Assert.Equal(1972, row["fateId"]!.GetValue<int>());
        Assert.False(row.ContainsKey("dynamicEventId"));
    }

    // Every contract word, because the vocabulary is closed.
    [Theory]
    [InlineData(OccultEncounterStatus.Preparing, "preparing")]
    [InlineData(OccultEncounterStatus.Active, "active")]
    [InlineData(OccultEncounterStatus.Down, "down")]
    public void Every_status_serializes_as_its_contract_word(OccultEncounterStatus status, string expected)
    {
        var row = Serialize(Build(encounters: Ce(46, status, Since)))
            ["encounters"]!.AsArray()[0]!.AsObject();

        Assert.Equal(expected, row["status"]!.GetValue<string>());
    }

    [Fact]
    public void SinceUtc_serializes_as_a_second_exact_utc_string_with_a_trailing_Z()
    {
        var row = Serialize(Build(encounters: Ce(46, OccultEncounterStatus.Active, Since)))
            ["encounters"]!.AsArray()[0]!.AsObject();

        Assert.Equal("2026-08-11T16:27:01Z", row["sinceUtc"]!.GetValue<string>());
    }

    [Fact]
    public void A_null_sinceUtc_is_written_explicitly_not_omitted()
    {
        var row = Serialize(Build(encounters: Ce(46, OccultEncounterStatus.Down)))
            ["encounters"]!.AsArray()[0]!.AsObject();

        Assert.True(row.ContainsKey("sinceUtc"));
        Assert.Null(row["sinceUtc"]);
    }

    [Fact]
    public void Every_tracked_encounter_becomes_one_row_in_order()
    {
        var rows = Serialize(Build(encounters:
        [
            Ce(48, OccultEncounterStatus.Down),
            Ce(46, OccultEncounterStatus.Active, Since),
            Fate(1972, OccultEncounterStatus.Down, Since),
        ]))["encounters"]!.AsArray();

        Assert.Equal(3, rows.Count);

        Assert.Equal(48, rows[0]!["dynamicEventId"]!.GetValue<int>());
        Assert.Equal(46, rows[1]!["dynamicEventId"]!.GetValue<int>());
        Assert.Equal(1972, rows[2]!["fateId"]!.GetValue<int>());
    }

    // A leave with nothing tracked still carries the key: an absent encounters array is a 400, an
    // empty one a valid snapshot.
    [Fact]
    public void An_empty_snapshot_serializes_as_an_empty_array_not_an_omitted_key()
    {
        var json = Serialize(Build(trigger: OccultTrigger.Leave));

        Assert.True(json.ContainsKey("encounters"));
        Assert.Empty(json["encounters"]!.AsArray());
    }

    // --- Response deserialization ----------------------------------------------------------

    [Theory]
    [InlineData("{\"ok\":true,\"outcome\":\"applied\",\"trackerId\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\",\"created\":false}", OccultOutcomes.Applied)]
    [InlineData("{\"ok\":true,\"outcome\":\"unresolved\",\"trackerId\":null}", OccultOutcomes.Unresolved)]
    [InlineData("{\"ok\":true,\"outcome\":\"left\",\"trackerId\":null}", OccultOutcomes.Left)]
    public void The_response_deserializes_every_contract_outcome(string body, string expected)
    {
        var response = JsonSerializer.Deserialize<OccultInstanceStateResponse>(body, ApiJson.Options);

        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Equal(expected, response.Outcome);
    }

    [Fact]
    public void The_response_carries_the_tracker_id_when_present()
    {
        var response = JsonSerializer.Deserialize<OccultInstanceStateResponse>(
            "{\"ok\":true,\"outcome\":\"applied\",\"trackerId\":\"abc\",\"created\":true}", ApiJson.Options);

        Assert.Equal("abc", response!.TrackerId);
        Assert.True(response.Created);
    }
}
