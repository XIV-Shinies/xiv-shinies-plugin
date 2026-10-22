using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Pins the DTOs to the wire format in docs/api-contract.md, which the server owns. A renamed field,
// a casing slip, or an absent category serializing as an empty array fails here.
public class DtoSerializationTests
{
    // Reparsed so a test can ask whether a key is present at all: the omitted-versus-empty
    // distinction that monotonic writes depend on.
    private static JsonObject Serialize<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, ApiJson.Options))!.AsObject();

    private static SyncRequest MinimalRequest() => new()
    {
        CharacterContentIdHash = new string('a', 64),
        CharacterName = "Some Name",
        HomeWorld = "Excalibur",
        PluginVersion = "1.0.0",
        Trigger = SyncTrigger.Login,
        Collections = new Dictionary<string, JsonNode>(),
    };

    [Fact]
    public void SyncRequest_uses_the_contract_field_names_in_camelCase()
    {
        var json = Serialize(MinimalRequest());

        Assert.True(json.ContainsKey("characterContentIdHash"));
        Assert.True(json.ContainsKey("characterName"));
        Assert.True(json.ContainsKey("homeWorld"));
        Assert.True(json.ContainsKey("pluginVersion"));
        Assert.True(json.ContainsKey("trigger"));
        Assert.True(json.ContainsKey("collections"));
    }

    [Fact]
    public void SyncRequest_serializes_the_trigger_as_a_lowercase_string()
    {
        var json = Serialize(MinimalRequest() with { Trigger = SyncTrigger.Unlock });
        Assert.Equal("unlock", json["trigger"]!.GetValue<string>());
    }

    [Fact]
    public void SyncRequest_omits_manifestVersion_when_it_is_null()
    {
        var json = Serialize(MinimalRequest());
        Assert.False(json.ContainsKey("manifestVersion"));
    }

    // The contract accepts only "full" or "partial", answering anything else with a 400, and an
    // absent object means partial.
    [Fact]
    public void SyncRequest_omits_collectionScopes_when_it_is_null()
    {
        var json = Serialize(MinimalRequest());
        Assert.False(json.ContainsKey("collectionScopes"));
    }

    [Fact]
    public void SyncRequest_serializes_collection_scopes_under_the_contract_key()
    {
        var request = MinimalRequest() with
        {
            CollectionScopes = new Dictionary<string, string>
            {
                ["tripleTriadCards"] = CollectionScopeValues.Full,
            },
        };

        var scopes = Serialize(request)["collectionScopes"]!.AsObject();

        Assert.Equal("full", scopes["tripleTriadCards"]!.GetValue<string>());
    }

    [Fact]
    public void SyncRequest_includes_manifestVersion_when_present()
    {
        var json = Serialize(MinimalRequest() with { ManifestVersion = "a1b2c3d4e5f6" });
        Assert.Equal("a1b2c3d4e5f6", json["manifestVersion"]!.GetValue<string>());
    }

    // Monotonic writes: absence means "not read this time", while [] means "read, and empty".
    [Fact]
    public void Unread_collection_categories_are_omitted_entirely()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["quests"] = SyncFacts.Ids(new uint[] { 65575, 66216 }),
            },
        };

        var collections = Serialize(request)["collections"]!.AsObject();

        Assert.True(collections.ContainsKey("quests"));
        Assert.False(collections.ContainsKey("achievements"));
        Assert.False(collections.ContainsKey("mounts"));
        Assert.False(collections.ContainsKey("minions"));
        Assert.False(collections.ContainsKey("items"));
    }

    [Fact]
    public void An_explicitly_empty_category_still_serializes_as_an_empty_array()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["mounts"] = SyncFacts.Ids(new uint[0]),
            },
        };

        var collections = Serialize(request)["collections"]!.AsObject();

        Assert.True(collections.ContainsKey("mounts"));
        Assert.Empty(collections["mounts"]!.AsArray());
    }

    [Fact]
    public void Items_serialize_with_id_count_and_fresh()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["items"] = SyncFacts.Items(
                    new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } }),
            },
        };

        var item = Serialize(request)["collections"]!["items"]!.AsArray()[0]!.AsObject();

        Assert.Equal(7851u, item["id"]!.GetValue<uint>());
        Assert.Equal(1u, item["count"]!.GetValue<uint>());
        Assert.True(item["fresh"]!.GetValue<bool>());
    }

    // The server strips keys it does not recognize, which lets both sides ship independently.
    [Fact]
    public void An_unknown_category_key_serializes_without_any_special_handling()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["facewear"] = SyncFacts.Ids(new uint[] { 42 }),
            },
        };

        var collections = Serialize(request)["collections"]!.AsObject();

        Assert.True(collections.ContainsKey("facewear"));
        Assert.Equal(42u, collections["facewear"]!.AsArray()[0]!.GetValue<uint>());
    }

    [Fact]
    public void SyncRequest_round_trips_through_json()
    {
        var original = MinimalRequest() with
        {
            ManifestVersion = "a1b2c3d4e5f6",
            Trigger = SyncTrigger.Interval,
            Collections = new Dictionary<string, JsonNode>
            {
                ["achievements"] = SyncFacts.Ids(new uint[] { 1, 2 }),
            },
        };

        var json = JsonSerializer.Serialize(original, ApiJson.Options);
        var back = JsonSerializer.Deserialize<SyncRequest>(json, ApiJson.Options)!;

        Assert.Equal(original.CharacterContentIdHash, back.CharacterContentIdHash);
        Assert.Equal(original.ManifestVersion, back.ManifestVersion);
        Assert.Equal(SyncTrigger.Interval, back.Trigger);
        Assert.Equal(new uint[] { 1, 2 }, back.Collections["achievements"].AsArray().GetValues<uint>());
        Assert.False(back.Collections.ContainsKey("quests"));
    }

    // The exact 200 body from the contract's GET /me example.
    [Fact]
    public void MeResponse_deserializes_the_contract_sample()
    {
        const string body = """
        {
          "characters": [
            {"id": "12345678", "name": "Some Name", "pluginLinked": true,
             "verified": true, "world": "Excalibur"}
          ],
          "user": {"id": "abc-uuid"}
        }
        """;

        var me = JsonSerializer.Deserialize<MeResponse>(body, ApiJson.Options)!;

        Assert.Equal("abc-uuid", me.User.Id);
        var character = Assert.Single(me.Characters);
        Assert.Equal("12345678", character.Id); // a BigInt on the server, so it travels as a string
        Assert.Equal("Some Name", character.Name);
        Assert.True(character.PluginLinked);
        Assert.True(character.Verified);
        Assert.Equal("Excalibur", character.World);
    }

    [Fact]
    public void ConfigResponse_deserializes_the_contract_sample()
    {
        const string body = """
        {
          "categories": {"achievements": true, "items": true, "minions": false,
                         "mounts": true, "quests": true},
          "enabled": true,
          "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
          "itemManifest": [7851, 7852],
          "manifestVersion": "a1b2c3d4e5f6"
        }
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.True(config.Enabled);
        Assert.False(config.IsCategoryEnabled("minions"));
        Assert.True(config.IsCategoryEnabled("achievements"));
        Assert.Equal(30, config.Intervals.FullSyncMinutes);
        Assert.Equal(5, config.Intervals.UnlockDebounceSeconds);
        Assert.Equal(new uint[] { 7851, 7852 }, config.ItemManifest);
        Assert.Equal("a1b2c3d4e5f6", config.ManifestVersion);

        // An optional key an older server never sends stays null. For this set, null means omit
        // nothing.
        Assert.Null(config.ItemOmitWhenUnseenIds);
    }

    [Fact]
    public void ConfigResponse_deserializes_the_omit_when_unseen_ids()
    {
        const string json = """
            {"categories":{"items":true},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[45043,45044,7851],"manifestVersion":"abc123",
             "itemOmitWhenUnseenIds":[45043,45044]}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.Equal(new uint[] { 45043, 45044 }, config.ItemOmitWhenUnseenIds);
    }

    // Defaulting to enabled lets a new collector work before the server grows its toggle.
    [Fact]
    public void An_unknown_category_switch_defaults_to_enabled()
    {
        const string body = """
        {"categories": {"quests": true},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.True(config.IsCategoryEnabled("quests"));
        Assert.True(config.IsCategoryEnabled("facewear")); // never mentioned by the server
    }

    // --- Category notes ------------------------------------------------------------------------

    // The server explains a switched-off category in its own words, keyed like the switches. The
    // plugin prints the sentence and never parses it, so it can be reworded without a plugin release.
    [Fact]
    public void ConfigResponse_deserializes_a_category_note()
    {
        const string body = """
        {"categories": {"orchestrionRolls": false, "quests": true},
         "categoryNotes": {"orchestrionRolls": "In testing — it switches on once it is ready."},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Equal(
            "In testing — it switches on once it is ready.",
            config.CategoryNote("orchestrionRolls"));

        Assert.Null(config.CategoryNote("quests"));
        Assert.Null(config.CategoryNote("facewear"));
    }

    // The field is additive: a server without it gives no notes, not a deserialization failure.
    [Fact]
    public void A_config_without_category_notes_reports_none()
    {
        const string body = """
        {"categories": {"orchestrionRolls": false},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Null(config.CategoryNotes);
        Assert.Null(config.CategoryNote("orchestrionRolls"));
    }

    // The backend is user-overridable, so untrusted, and this text renders in ImGui. The marker tells
    // a cut sentence from one the server wrote that way.
    [Fact]
    public void An_overlong_category_note_is_clamped()
    {
        var body = $$"""
        {"categories": {"quests": false},
         "categoryNotes": {"quests": "{{new string('n', ServerText.MaxAdoptedLength + 250)}}"},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        var note = config.CategoryNote("quests")!;
        Assert.Equal(new string('n', ServerText.MaxAdoptedLength) + "...", note);
    }

    // The marker makes an off-by-one visible: a sentence that fits would show as truncated.
    [Fact]
    public void A_category_note_exactly_at_the_ceiling_is_untouched()
    {
        var body = $$"""
        {"categories": {"quests": false},
         "categoryNotes": {"quests": "{{new string('n', ServerText.MaxAdoptedLength)}}"},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Equal(new string('n', ServerText.MaxAdoptedLength), config.CategoryNote("quests"));
    }

    // The contract says a note is a non-empty string, but the deserializer does not enforce the
    // non-nullable declaration, so a JSON null lands in the map. Reading it must not throw on the
    // draw path.
    [Fact]
    public void A_null_category_note_reports_none()
    {
        const string body = """
        {"categories": {"quests": false},
         "categoryNotes": {"quests": null},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Null(config.CategoryNote("quests"));
    }

    [Fact]
    public void A_padded_category_note_is_trimmed()
    {
        const string body = """
        {"categories": {"quests": false},
         "categoryNotes": {"quests": "   In testing.   "},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Equal("In testing.", config.CategoryNote("quests"));
    }

    // A note within the length cap can still be hundreds of blank lines, burying the rest of the
    // consent list. One line keeps the server from laying out copy in a panel it does not own.
    [Fact]
    public void A_multi_line_category_note_is_folded_onto_one_line()
    {
        const string body = """
        {"categories": {"quests": false},
         "categoryNotes": {"quests": "In testing.\n\n\n\n\nSwitching on soon."},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Equal("In testing. Switching on soon.", config.CategoryNote("quests"));
    }

    // ServerText.Clamp explains why half a surrogate pair must never survive.
    [Fact]
    public void A_category_note_cut_mid_character_drops_the_whole_character()
    {
        // One char short of the ceiling, so the emoji's two code units straddle the cut.
        var note = new string('n', ServerText.MaxAdoptedLength - 1) + "😀tail";
        var body = $$"""
        {"categories": {"quests": false},
         "categoryNotes": {"quests": "{{note}}"},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        var clamped = config.CategoryNote("quests")!;
        Assert.Equal(new string('n', ServerText.MaxAdoptedLength - 1) + "...", clamped);
        Assert.DoesNotContain(clamped, char.IsSurrogate);
    }

    // Reading a blank note as none makes the panel fall back to its own line rather than draw an
    // empty gap where a reason belongs.
    [Fact]
    public void A_blank_category_note_reports_none()
    {
        const string body = """
        {"categories": {"quests": false},
         "categoryNotes": {"quests": "   "},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.Null(config.CategoryNote("quests"));
    }

    // The server omits optional keys rather than sending null, so the plugin can feature-detect them.
    [Fact]
    public void SyncResponse_leaves_omitted_optional_keys_null()
    {
        const string body = """
        {"ok": true, "bound": false,
         "written": {"achievements": 0, "minions": 2, "mounts": 1, "quests": 12}}
        """;

        var response = JsonSerializer.Deserialize<SyncResponse>(body, ApiJson.Options)!;

        Assert.True(response.Ok);
        Assert.False(response.Bound);
        Assert.Equal(2, response.Written["minions"]);
        Assert.Equal(12, response.Written["quests"]);
        Assert.Null(response.AchievementsSkipped);
        Assert.Null(response.ProvenSteps);
        Assert.Null(response.SkippedCategories);

        // An unsent category is an absent key, not an error.
        Assert.False(response.Written.ContainsKey("tripleTriadCards"));
    }

    // Written counts are a map, so an unknown category arrives intact and the server can add a
    // collection without waiting for the plugin.
    [Fact]
    public void SyncResponse_reads_written_counts_for_an_unknown_category()
    {
        const string body = """
        {"ok": true, "bound": false, "written": {"quests": 3, "facewear": 7}}
        """;

        var response = JsonSerializer.Deserialize<SyncResponse>(body, ApiJson.Options)!;

        Assert.Equal(3, response.Written["quests"]);
        Assert.Equal(7, response.Written["facewear"]);
    }

    // The field is informational; refusing the response would report a successful upload as failed.
    [Fact]
    public void SyncResponse_tolerates_a_response_with_no_written_object()
    {
        var response = JsonSerializer.Deserialize<SyncResponse>(
            """{"ok": true, "bound": false}""", ApiJson.Options)!;

        Assert.NotNull(response.Written);
        Assert.Empty(response.Written);
    }

    [Fact]
    public void SyncResponse_reads_optional_keys_when_the_server_sends_them()
    {
        const string body = """
        {"ok": true, "bound": true,
         "written": {"achievements": 0, "minions": 0, "mounts": 0, "quests": 0,
                     "tripleTriadCards": 5, "tripleTriadNpcs": 1},
         "achievementsSkipped": "not_sent",
         "provenSteps": 3,
         "skippedCategories": ["minions"]}
        """;

        var response = JsonSerializer.Deserialize<SyncResponse>(body, ApiJson.Options)!;

        Assert.True(response.Bound);
        Assert.Equal("not_sent", response.AchievementsSkipped);
        Assert.Equal(3, response.ProvenSteps);
        Assert.Equal(new[] { "minions" }, response.SkippedCategories);
        Assert.Equal(5, response.Written["tripleTriadCards"]);
        Assert.Equal(1, response.Written["tripleTriadNpcs"]);
    }

    [Fact]
    public void ErrorResponse_reads_the_opaque_401_body()
    {
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            """{"error": "invalid_token"}""", ApiJson.Options)!;

        Assert.Equal("invalid_token", error.Error);
        Assert.Null(error.Name);
        Assert.Null(error.World);
    }

    // The 403 echoes the upload's name and world back beside its refusal code.
    [Fact]
    public void ErrorResponse_reads_the_403_character_not_claimed_body()
    {
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            """{"error": "character_not_claimed", "name": "Some Name", "world": "Excalibur"}""",
            ApiJson.Options)!;

        Assert.Equal("character_not_claimed", error.Error);
        Assert.Equal("Some Name", error.Name);
        Assert.Equal("Excalibur", error.World);
    }

    // A 400 carries the flattened validation issues, for the log. A non-JSON body arrives in the same
    // shape, explained under formErrors.
    [Fact]
    public void ErrorResponse_reads_the_400_validation_issues()
    {
        const string body = """
        {"error": "invalid_payload",
         "issues": {"fieldErrors": {"trigger": ["Invalid enum value."]},
                    "formErrors": ["Request body is not valid JSON"]}}
        """;

        var error = JsonSerializer.Deserialize<ErrorResponse>(body, ApiJson.Options)!;

        Assert.Equal("invalid_payload", error.Error);
        Assert.Equal("Request body is not valid JSON", Assert.Single(error.Issues!.FormErrors!));
        Assert.Equal("Invalid enum value.", Assert.Single(error.Issues.FieldErrors!["trigger"]));
    }

    [Fact]
    public void ItemPossession_serializes_quality_counts_in_camelCase_and_round_trips()
    {
        var possession = new ItemPossession
        {
            Id = 7851,
            Count = 5,
            HqCount = 2,
            CollectableCount = 1,
            Fresh = true,
        };

        var json = JsonSerializer.Serialize(possession, ApiJson.Options);

        Assert.Equal("""{"id":7851,"count":5,"hqCount":2,"collectableCount":1,"fresh":true}""", json);

        var deserialized = JsonSerializer.Deserialize<ItemPossession>(json, ApiJson.Options)!;
        Assert.Equal(possession, deserialized);
    }

    [Fact]
    public void ItemPossession_omits_quality_count_keys_when_null()
    {
        // Most relic materials exist only in normal quality. An absent key costs no bytes, and an
        // older server cannot misread it as a fact the way it could a zero.
        var possession = new ItemPossession { Id = 7851, Count = 0, Fresh = true };

        var json = JsonSerializer.Serialize(possession, ApiJson.Options);

        Assert.Equal("""{"id":7851,"count":0,"fresh":true}""", json);
    }

    [Fact]
    public void ConfigResponse_deserializes_item_manifest_groups()
    {
        const string json = """
            {"categories":{"items":true},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[7851],"manifestVersion":"abc123",
             "itemManifestGroups":[
               {"key":"relic-proofs","label":"Relic weapons & tools you own","ids":[7851],"legacy":true},
               {"key":"relic-materials","label":"Relic materials","ids":[27798,24248]}]}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.NotNull(config.ItemManifestGroups);
        Assert.Equal(2, config.ItemManifestGroups!.Count);
        Assert.Equal("relic-proofs", config.ItemManifestGroups[0].Key);
        Assert.Equal("Relic weapons & tools you own", config.ItemManifestGroups[0].Label);
        Assert.True(config.ItemManifestGroups[0].Legacy);
        Assert.Equal("relic-materials", config.ItemManifestGroups[1].Key);
        Assert.False(config.ItemManifestGroups[1].Legacy);   // legacy defaults false when absent
        Assert.Equal(new uint[] { 27798, 24248 }, config.ItemManifestGroups[1].Ids);
    }

    [Fact]
    public void ConfigResponse_without_groups_leaves_them_null()
    {
        // An older server sends no groups, and null means fall back to the flat manifest.
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[7851],"manifestVersion":"abc123"}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.Null(config.ItemManifestGroups);
    }

    [Fact]
    public void ConfigResponse_deserializes_the_occult_tracker_block()
    {
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123",
             "occultTracker":{"enabled":true,"heartbeatSeconds":60}}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.NotNull(config.OccultTracker);
        Assert.True(config.OccultTracker!.Enabled);
        Assert.Equal(60, config.OccultTracker.HeartbeatSeconds);
    }

    [Fact]
    public void ConfigResponse_without_an_occult_tracker_block_leaves_it_null()
    {
        // Null means this server has no tracker endpoint, so the feature stays off; see OccultGate.
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123"}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.Null(config.OccultTracker);
    }

    // The kill switch is required: it gates uploads, so it is never guessed. The cadence is not,
    // since the scheduler clamps whatever arrives, and failing the whole config would strand /sync
    // on its last-known manifest.
    [Fact]
    public void An_occult_tracker_block_without_a_cadence_still_parses()
    {
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123",
             "occultTracker":{"enabled":true}}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.True(config.OccultTracker!.Enabled);
        Assert.Equal(60, config.OccultTracker.HeartbeatSeconds);
    }

    [Fact]
    public void ConfigResponse_deserializes_the_crucible_runs_block()
    {
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123",
             "crucibleRuns":{"enabled":false,"heartbeatSeconds":90,"note":"In testing"}}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.NotNull(config.CrucibleRuns);
        Assert.False(config.CrucibleRuns!.Enabled);
        Assert.Equal(90, config.CrucibleRuns.HeartbeatSeconds);
        Assert.Equal("In testing", config.CrucibleRuns.Note);
    }

    // A server without the Crucible endpoint never sends the block.
    [Fact]
    public void ConfigResponse_without_a_crucible_runs_block_leaves_it_null()
    {
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123"}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.Null(config.CrucibleRuns);
    }

    // The switch is required; the cadence and the note are not.
    [Fact]
    public void A_crucible_runs_block_with_only_its_switch_still_parses()
    {
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123",
             "crucibleRuns":{"enabled":true}}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.True(config.CrucibleRuns!.Enabled);
        Assert.Equal(60, config.CrucibleRuns.HeartbeatSeconds);
        Assert.Null(config.CrucibleRuns.Note);
    }

    [Fact]
    public void ConfigResponse_deserializes_the_quest_sequence_manifest()
    {
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123",
             "questSequenceManifest":[70562,69208]}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.Equal(new uint[] { 70562, 69208 }, config.QuestSequenceManifest);
    }

    [Fact]
    public void ConfigResponse_without_a_quest_sequence_manifest_leaves_it_null()
    {
        // An older server never sends the field. Null means the server asks about no quest sequence.
        const string json = """
            {"categories":{},"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc123"}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.Null(config.QuestSequenceManifest);
    }

    // An object, not an array, so each journal step is unambiguously paired with its quest id. JSON
    // keys are strings, so the ids travel as decimal strings.
    [Fact]
    public void Quest_sequences_serialize_as_an_object_keyed_by_quest_id()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["questSequences"] = SyncFacts.Sequences(
                    new Dictionary<uint, byte> { [70562] = 3, [69208] = 255 }),
            },
        };

        var sequences = Serialize(request)["collections"]!["questSequences"]!.AsObject();

        Assert.Equal(3, sequences["70562"]!.GetValue<int>());
        Assert.Equal(255, sequences["69208"]!.GetValue<int>());
    }

    // The one nested-object category: a "jobs" map of per-job records and an optional knowledge
    // record. OccultProgressionFactsTests pins the tree; this pins that ApiJson.Options keeps its
    // keys verbatim and drops nothing.
    [Fact]
    public void Occult_progression_serializes_as_nested_objects_with_verbatim_keys()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["occultProgression"] = SyncFacts.Progression(
                    new Dictionary<byte, OccultJobProgress>
                    {
                        [0] = new() { Exp = 1840, Level = 2 },
                    },
                    new KnowledgeObservation
                    {
                        Level = 40,
                        ObservedAt = new DateTimeOffset(2026, 8, 12, 20, 0, 0, TimeSpan.Zero),
                    }),
            },
        };

        var progression = Serialize(request)["collections"]!["occultProgression"]!.AsObject();

        Assert.Equal(1840, progression["jobs"]!["0"]!["exp"]!.GetValue<int>());
        Assert.Equal(2, progression["jobs"]!["0"]!["level"]!.GetValue<int>());
        Assert.Equal(40, progression["knowledge"]!["level"]!.GetValue<int>());
        Assert.Equal(
            "2026-08-12T20:00:00Z", progression["knowledge"]!["observedAt"]!.GetValue<string>());
    }

    [Fact]
    public void ItemSourceStatus_serializes_state_and_optional_counts()
    {
        var live = new ItemSourceStatus { State = SourceStates.Live };
        var retainers = new ItemSourceStatus { State = SourceStates.Cached, Count = 3 };
        var partial = new ItemSourceStatus { State = SourceStates.Cached, Count = 3, Total = 5 };

        Assert.Equal("""{"state":"live"}""", JsonSerializer.Serialize(live, ApiJson.Options));
        Assert.Equal("""{"state":"cached","count":3}""", JsonSerializer.Serialize(retainers, ApiJson.Options));
        Assert.Equal(
            """{"state":"cached","count":3,"total":5}""",
            JsonSerializer.Serialize(partial, ApiJson.Options));
    }

    [Fact]
    public void SyncRequest_with_item_sources_carries_them_under_itemSources()
    {
        // glamourDresser shows a mid-word capital in a source key reaching the wire verbatim. The
        // states follow the contract's examples; armoire is the one source that uses "loaded".
        var request = MinimalRequest() with
        {
            ItemSources = new Dictionary<string, ItemSourceStatus>
            {
                ["glamourDresser"] = new ItemSourceStatus { State = SourceStates.Cached },
                ["armoire"] = new ItemSourceStatus { State = SourceStates.Loaded },
                ["retainers"] = new ItemSourceStatus { State = SourceStates.Cached, Count = 2 },
            },
        };

        var json = Serialize(request);

        Assert.True(json.ContainsKey("itemSources"));
        var sources = json["itemSources"]!.AsObject();
        Assert.Equal("cached", sources["glamourDresser"]!["state"]!.GetValue<string>());
        Assert.Equal("loaded", sources["armoire"]!["state"]!.GetValue<string>());
        Assert.Equal("cached", sources["retainers"]!["state"]!.GetValue<string>());
        Assert.Equal(2, sources["retainers"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void SyncRequest_omits_itemSources_when_it_is_null()
    {
        var json = Serialize(MinimalRequest());

        Assert.False(json.ContainsKey("itemSources"));
    }

    // An explicit JSON null gets past `required` (see ApiJson), and this lookup runs per row per
    // frame, so a null map must read as one that names nothing rather than throw.
    [Fact]
    public void A_null_categories_map_reads_as_every_category_enabled()
    {
        const string json = """
            {"categories":null,"enabled":true,
             "intervals":{"fullSyncMinutes":30,"unlockDebounceSeconds":5},
             "itemManifest":[],"manifestVersion":"abc"}
            """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(json, ApiJson.Options)!;

        Assert.True(config.IsCategoryEnabled("quests"));
    }
}
