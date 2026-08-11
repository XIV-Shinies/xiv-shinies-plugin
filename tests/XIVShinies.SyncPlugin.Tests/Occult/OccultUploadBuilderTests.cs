using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// Pins the occult upload to the exact wire format in docs/api-contract.md § occult/instance-state.
// The subtle parts are deliberate departures from the /sync serializer defaults: sinceUtc must
// appear EXPLICITLY as null (the contract's zod schema is .nullable(), not .nullish()), and
// timestamps must be second-exact UTC strings ending in "Z" (a "+00:00" offset is rejected).
public class OccultUploadBuilderTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.FromUnixTimeSeconds(1786465621);

    // Serializes with the shared wire policy and reparses so tests can ask "is this key present
    // at all?" — the omitted-vs-null distinction both matter here.
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
            encounters: encounters);

    private static OccultEncounterState Ce(
        ushort id, OccultEncounterStatus status, DateTimeOffset? since = null) =>
        new() { IsFate = false, Id = id, Status = status, SinceUtc = since };

    private static OccultEncounterState Fate(
        ushort id, OccultEncounterStatus status, DateTimeOffset? since = null) =>
        new() { IsFate = true, Id = id, Status = status, SinceUtc = since };

    // Values, not just key presence: Build takes four consecutive strings, so a swapped
    // argument pair would keep every key intact while binding the wrong character.
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

    // All four contract words, not a sample — the server rejects anything else.
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

    // The instance identity is the territory alone — the tracker is resolved by fingerprint, so
    // sending anything else would imply an identity that does not exist.
    [Fact]
    public void The_instance_object_carries_only_the_territory()
    {
        var instance = Serialize(Build())["instance"]!.AsObject();

        Assert.Equal(1252, instance["territoryTypeId"]!.GetValue<int>());
        Assert.Single(instance);
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

    // All three contract words — the vocabulary is closed.
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

    // The contract's example is "2026-08-11T16:02:15Z": whole seconds, UTC, trailing Z — never
    // a "+00:00" numeric offset.
    [Fact]
    public void SinceUtc_serializes_as_a_second_exact_utc_string_with_a_trailing_Z()
    {
        var row = Serialize(Build(encounters: Ce(46, OccultEncounterStatus.Active, Since)))
            ["encounters"]!.AsArray()[0]!.AsObject();

        Assert.Equal("2026-08-11T16:27:01Z", row["sinceUtc"]!.GetValue<string>());
    }

    // "null entries carry state but never identity" — the key must be PRESENT with a JSON null,
    // not omitted the way the /sync serializer treats null properties.
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

        // Order and identity both: the rows come out exactly as the tracker reported them.
        Assert.Equal(48, rows[0]!["dynamicEventId"]!.GetValue<int>());
        Assert.Equal(46, rows[1]!["dynamicEventId"]!.GetValue<int>());
        Assert.Equal(1972, rows[2]!["fateId"]!.GetValue<int>());
    }

    // A leave from an instance with nothing tracked (or reset moments earlier) still carries
    // the key: an ABSENT encounters array is a 400, an empty one is a valid snapshot.
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
