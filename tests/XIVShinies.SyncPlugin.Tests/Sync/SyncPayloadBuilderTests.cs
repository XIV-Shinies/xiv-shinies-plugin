using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Assembles the upload body from a collection pass.
public class SyncPayloadBuilderTests
{
    private static readonly CharacterIdentity Identity = new()
    {
        ContentIdHash = new string('a', 64),
        Name = "Some Name",
        HomeWorld = "Excalibur",
    };

    private static CollectionSnapshot Snapshot(params string[] categoryKeys)
    {
        var collections = new Dictionary<string, JsonNode>();
        foreach (var key in categoryKeys)
            collections[key] = SyncFacts.Ids(new uint[] { 1, 2 });

        return new CollectionSnapshot
        {
            Collections = collections,
            Skipped = new Dictionary<string, string>(),
        };
    }

    [Fact]
    public void A_snapshot_built_without_source_notes_yields_no_item_sources()
    {
        // The Snapshot helper leaves SourceNotes at its default, an empty dictionary.
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("quests"), manifestVersion: null);

        Assert.Null(request.ItemSources);
    }

    // The unreadable state is for the settings panel: it marks a source the game never exposes
    // (mannequins) and carries no counts. Data minimization keeps a fact that never changes off the
    // wire.
    [Fact]
    public void An_unreadable_source_note_stays_off_the_wire()
    {
        var snapshot = Snapshot("items") with
        {
            SourceNotes = new Dictionary<string, ItemSourceStatus>
            {
                [SourceKeys.Inventory] = new ItemSourceStatus { State = SourceStates.Live },
                [SourceKeys.Mannequins] = new ItemSourceStatus { State = SourceStates.Unreadable },
            },
        };

        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, snapshot, manifestVersion: null);

        Assert.NotNull(request.ItemSources);
        Assert.True(request.ItemSources!.ContainsKey(SourceKeys.Inventory));
        Assert.False(request.ItemSources.ContainsKey(SourceKeys.Mannequins));
    }

    [Fact]
    public void A_snapshot_whose_only_source_note_is_unreadable_yields_no_item_sources()
    {
        // An empty itemSources object is the noise the omitted-key rule avoids.
        var snapshot = Snapshot("items") with
        {
            SourceNotes = new Dictionary<string, ItemSourceStatus>
            {
                [SourceKeys.Mannequins] = new ItemSourceStatus { State = SourceStates.Unreadable },
            },
        };

        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, snapshot, manifestVersion: null);

        Assert.Null(request.ItemSources);
    }

    [Fact]
    public void Carries_the_identity_version_and_trigger()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.2.3", SyncTrigger.Login, Snapshot("quests"), manifestVersion: null);

        Assert.Equal(Identity.ContentIdHash, request.CharacterContentIdHash);
        Assert.Equal("Some Name", request.CharacterName);
        Assert.Equal("Excalibur", request.HomeWorld);
        Assert.Equal("1.2.3", request.PluginVersion);
        Assert.Equal(SyncTrigger.Login, request.Trigger);
    }

    [Fact]
    public void Carries_exactly_the_categories_the_snapshot_read()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Interval, Snapshot("quests", "mounts"), null);

        Assert.True(request.Collections.ContainsKey("quests"));
        Assert.True(request.Collections.ContainsKey("mounts"));
        Assert.Equal(2, request.Collections.Count);
    }

    // The contract stamps an unlock upload's moment as the acquisition time for every category in
    // it, and a later upload never revises that date, so a sweep would misdate unchanged rows.
    // Naming one category also spares a burst of unlocks a re-scan of every inventory container.
    [Fact]
    public void An_unlock_upload_carries_only_the_category_that_was_unlocked()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Unlock, Snapshot("mounts"), null);

        Assert.Equal(SyncTrigger.Unlock, request.Trigger);
        Assert.Equal(new[] { "mounts" }, request.Collections.Keys);
    }

    [Fact]
    public void Omits_the_manifest_version_when_there_is_none()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("quests"), manifestVersion: null);

        Assert.Null(request.ManifestVersion);
    }

    // Echoing it back records which manifest the item list was built against.
    [Fact]
    public void Echoes_the_manifest_version_when_the_server_supplied_one()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("items"), "a1b2c3d4e5f6");

        Assert.Equal("a1b2c3d4e5f6", request.ManifestVersion);
    }

    // `collections` is an empty object, never null.
    [Fact]
    public void An_empty_snapshot_produces_an_empty_collections_object()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Interval, Snapshot(), null);

        Assert.Empty(request.Collections);
    }

    // The contract trims the name but length-checks the world as sent, so a padded world could fail
    // validation and reject the whole upload.
    [Fact]
    public void Trims_the_character_name_and_world()
    {
        var padded = Identity with { Name = "  Some Name  ", HomeWorld = " Excalibur " };

        var request = SyncPayloadBuilder.Build(
            padded, "1.0.0", SyncTrigger.Login, Snapshot("quests"), null);

        Assert.Equal("Some Name", request.CharacterName);
        Assert.Equal("Excalibur", request.HomeWorld);
    }

    // The builder never invents facts; whether an empty pass is worth sending is the caller's call.
    [Fact]
    public void Never_adds_a_category_the_snapshot_did_not_contain()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Login, Snapshot("quests"), null);

        Assert.False(request.Collections.ContainsKey("achievements"));
    }

    [Fact]
    public void Build_puts_source_notes_on_the_request_when_present()
    {
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
            ["saddlebag"] = new ItemSourceStatus { State = SourceStates.Cached, Count = 3 },
        };
        var snapshot = Snapshot("items") with { SourceNotes = sourceNotes };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.NotNull(request.ItemSources);
        Assert.Equal(2, request.ItemSources.Count);
        Assert.Equal(SourceStates.Live, request.ItemSources["inventory"].State);
        Assert.Equal(SourceStates.Cached, request.ItemSources["saddlebag"].State);
        Assert.Equal(3, request.ItemSources["saddlebag"].Count);
    }

    [Fact]
    public void Build_omits_item_sources_when_none_are_present()
    {
        var snapshot = Snapshot("items") with { SourceNotes = new Dictionary<string, ItemSourceStatus>() };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.Null(request.ItemSources);
    }

    // "full" says the list is the character's complete set, the one declaration that makes an absent
    // id meaningful (the site's disputed manual marks). The key is one the plugin does not know: a
    // scope is generic collector self-description, like the facts.
    [Fact]
    public void A_complete_enumeration_is_declared_full_on_a_snapshot_upload()
    {
        var snapshot = Snapshot("facewear") with { CompleteKeys = new HashSet<string> { "facewear" } };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.NotNull(request.CollectionScopes);
        Assert.Equal(CollectionScopeValues.Full, request.CollectionScopes!["facewear"]);
    }

    // A delta upload must not claim completeness. Keyed on the trigger alone, so it holds for every
    // collection.
    [Fact]
    public void An_unlock_upload_never_declares_full_scope()
    {
        var snapshot = Snapshot("facewear") with { CompleteKeys = new HashSet<string> { "facewear" } };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Unlock, snapshot, null);

        Assert.Null(request.CollectionScopes);
    }

    // The server reads a missing object as "partial", so an empty one would be noise.
    [Fact]
    public void Omits_collection_scopes_when_nothing_was_complete()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("facewear"), null);

        Assert.Null(request.CollectionScopes);
    }

    // "full" for an absent category would claim completeness of facts the server never received.
    [Fact]
    public void A_scope_is_never_declared_for_a_category_the_upload_does_not_carry()
    {
        var snapshot = Snapshot("facewear") with { CompleteKeys = new HashSet<string> { "ornaments" } };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.Null(request.CollectionScopes);
    }

    // The uncarried key costs only itself. Bailing out of the whole object would pass every other
    // test here while dropping real declarations.
    [Fact]
    public void An_uncarried_complete_key_does_not_suppress_the_categories_that_are_carried()
    {
        var snapshot = Snapshot("facewear", "ornaments") with
        {
            CompleteKeys = new HashSet<string> { "facewear", "gone" },
        };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.NotNull(request.CollectionScopes);
        Assert.Equal(CollectionScopeValues.Full, request.CollectionScopes!["facewear"]);
        Assert.False(request.CollectionScopes.ContainsKey("gone"));

        // Carried but never claimed complete, so it says nothing rather than saying "partial".
        Assert.False(request.CollectionScopes.ContainsKey("ornaments"));
    }

    // "partial" is the server's default for an absent key, so the plugin sends "full" or nothing.
    [Fact]
    public void The_literal_partial_value_is_never_sent()
    {
        var snapshot = Snapshot("facewear", "ornaments") with
        {
            CompleteKeys = new HashSet<string> { "facewear" },
        };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.DoesNotContain(CollectionScopeValues.Partial, request.CollectionScopes!.Values);
    }

    // The contract caps manifestVersion at 100 characters, so an over-long echo would fail every
    // upload with a 400. Dropped, not shortened: the field is optional, and a shortened version is a
    // different wrong value.
    [Fact]
    public void An_over_long_manifest_echo_is_dropped_rather_than_sent()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("quests"),
            new string('v', SyncPayloadBuilder.MaxManifestVersionLength + 1));

        Assert.Null(request.ManifestVersion);
    }

    [Fact]
    public void A_manifest_echo_at_the_ceiling_is_kept()
    {
        var version = new string('v', SyncPayloadBuilder.MaxManifestVersionLength);

        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("quests"), version);

        Assert.Equal(version, request.ManifestVersion);
    }
}
