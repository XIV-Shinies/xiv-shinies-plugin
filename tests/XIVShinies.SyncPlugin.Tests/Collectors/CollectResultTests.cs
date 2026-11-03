using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Every collector's output passes through CollectResult, so the contract's positive-id rule is
// enforced here once.
public class CollectResultTests
{
    private static uint[] IdsOf(CollectResult result) =>
        result.Facts!.AsArray().GetValues<uint>().ToArray();

    // Sheets start with a blank row 0, and one zero on the wire makes the server reject the whole
    // upload.
    [Fact]
    public void Ids_drops_zero_because_the_server_requires_positive_integers()
    {
        var result = CollectResult.Ids(new uint[] { 0, 5, 0, 7 });

        Assert.Equal(new uint[] { 5, 7 }, IdsOf(result));
    }

    [Fact]
    public void Ids_preserves_order_and_duplicates_of_real_ids()
    {
        var result = CollectResult.Ids(new uint[] { 7, 5, 7 });

        Assert.Equal(new uint[] { 7, 5, 7 }, IdsOf(result));
    }

    // "I read the sheet and nothing was unlocked" is a real fact, distinct from a skip.
    [Fact]
    public void An_empty_id_list_is_still_a_collected_fact()
    {
        var result = CollectResult.Ids(Array.Empty<uint>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Empty(result.Facts!.AsArray());
    }

    [Fact]
    public void A_list_of_only_zeroes_becomes_an_empty_array_not_a_skip()
    {
        var result = CollectResult.Ids(new uint[] { 0, 0 });

        Assert.True(result.WasCollected);
        Assert.Empty(result.Facts!.AsArray());
    }

    // A wrong false only withholds evidence; a wrong true makes an id's absence count as evidence on
    // the server. So the claim defaults to false.
    [Fact]
    public void Ids_does_not_claim_a_complete_enumeration_unless_asked()
    {
        Assert.False(CollectResult.Ids(new uint[] { 5 }).CompleteEnumeration);
    }

    [Fact]
    public void Ids_carries_the_complete_enumeration_claim_when_the_collector_makes_it()
    {
        Assert.True(CollectResult.Ids(new uint[] { 5 }, completeEnumeration: true).CompleteEnumeration);
    }

    // "I own nothing" and "the game has not filled this in yet" look the same here, and an empty
    // list declared complete would contradict every hand-made mark at once.
    [Fact]
    public void An_empty_list_never_claims_a_complete_enumeration()
    {
        var result = CollectResult.Ids(Array.Empty<uint>(), completeEnumeration: true);

        Assert.True(result.WasCollected);
        Assert.False(result.CompleteEnumeration);
    }

    // The guard applies after the zero filter.
    [Fact]
    public void A_list_of_only_padding_never_claims_a_complete_enumeration()
    {
        var result = CollectResult.Ids(new uint[] { 0, 0 }, completeEnumeration: true);

        Assert.Empty(result.Facts!.AsArray());
        Assert.False(result.CompleteEnumeration);
    }

    // Manifest-scoped: they cover what the server asked about, never the whole domain, so neither
    // factory takes the claim.
    [Fact]
    public void Item_and_sequence_facts_cannot_claim_a_complete_enumeration()
    {
        var items = CollectResult.Items(Array.Empty<ItemPossession>());
        var sequences = CollectResult.Sequences(new Dictionary<uint, byte>());

        Assert.False(items.CompleteEnumeration);
        Assert.False(sequences.CompleteEnumeration);
    }

    [Fact]
    public void A_skip_never_claims_a_complete_enumeration()
    {
        Assert.False(CollectResult.Skipped(CollectSkipReasons.SheetUnavailable).CompleteEnumeration);
    }

    [Fact]
    public void A_skip_carries_its_reason_and_no_facts()
    {
        var result = CollectResult.Skipped(CollectSkipReasons.AchievementListNotLoaded);

        Assert.False(result.WasCollected);
        Assert.Equal("achievement_list_not_loaded", result.SkipReason);
        Assert.Null(result.Facts);
    }

    // A count of zero is legal per the contract, and the collector sends one for an unheld manifest id
    // (see ItemTallies), so Items must not filter the way Ids does.
    [Fact]
    public void Items_does_not_filter_and_preserves_a_zero_count()
    {
        var result = CollectResult.Items(
            new[] { new ItemPossession { Id = 7851, Count = 0, Fresh = false } });

        var item = result.Facts!.AsArray()[0]!.AsObject();
        Assert.Equal(7851u, item["id"]!.GetValue<uint>());
        Assert.Equal(0u, item["count"]!.GetValue<uint>());
    }

    [Fact]
    public void Items_with_source_notes_carries_them_alongside_the_facts()
    {
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
            ["saddlebag"] = new ItemSourceStatus { State = SourceStates.Unscanned },
        };
        var items = new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } };

        var result = CollectResult.Items(items, sourceNotes);

        Assert.True(result.WasCollected);
        Assert.NotNull(result.Facts);
        Assert.NotNull(result.SourceNotes);
        Assert.Equal(2, result.SourceNotes.Count);
        Assert.Equal(SourceStates.Live, result.SourceNotes["inventory"].State);
        Assert.Equal(SourceStates.Unscanned, result.SourceNotes["saddlebag"].State);
    }

    [Fact]
    public void Items_without_source_notes_has_null_source_notes()
    {
        var items = new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } };

        var result = CollectResult.Items(items);

        Assert.True(result.WasCollected);
        Assert.Null(result.SourceNotes);
    }

    [Fact]
    public void Sequences_carries_an_object_keyed_by_quest_id()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte> { [70562] = 3 });

        Assert.True(result.WasCollected);
        Assert.Equal(3, result.Facts!.AsObject()["70562"]!.GetValue<int>());
    }

    // "Every manifested quest was checked and none is in the journal" is a real fact, not a skip.
    [Fact]
    public void An_empty_sequence_map_is_still_a_collected_fact()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Empty(result.Facts!.AsObject());
    }

    [Fact]
    public void Sequences_drops_a_zero_quest_id()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte> { [0] = 2, [70562] = 3 });

        var facts = result.Facts!.AsObject();
        Assert.False(facts.ContainsKey("0"));
        Assert.Equal(3, facts["70562"]!.GetValue<int>());
    }

    // 10 is in the dresser and the Armoire, 21 in an outfit and held. The outfit's id (500) and extra
    // copies are not pieces, so the distinct pieces are 10, 11, 20, 21, 30 and 40: six.
    private static GlamourFacts SampleGlamourFacts() => new()
    {
        Dresser = new[]
        {
            new DresserPiece { Id = 10 },
            new DresserPiece { Id = 11, Hq = true, Stains = new[] { 0, 5 } },
        },
        OutfitGlamours = new[] { new OutfitGlamour { OutfitId = 500, PieceIds = new uint[] { 20, 21 } } },
        Armoire = new uint[] { 10, 30 },
        Held = new[]
        {
            new HeldPiece { Id = 21, Place = HeldPlaces.Bags, Count = 1 },
            new HeldPiece { Id = 40, Place = HeldPlaces.Bags, Count = 3 },
        },
    };

    // The factory is the one place glamour facts become wire JSON, so its output is pinned as the
    // literal text the server receives.
    [Fact]
    public void Glamour_serializes_its_facts_to_the_contract_json()
    {
        var result = CollectResult.Glamour(
            SampleGlamourFacts(), new Dictionary<string, ItemSourceStatus>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Equal(
            """{"version":1,"dresser":""" +
            """[{"id":10},{"id":11,"hq":true,"stains":[0,5]}],"outfitGlamours":""" +
            """[{"outfitId":500,"pieceIds":[20,21]}],"armoire":[10,30],"held":""" +
            """[{"id":21,"place":"bags","count":1},{"id":40,"place":"bags","count":3}]}""",
            result.Facts!.ToJsonString(ApiJson.Options));
    }

    // Glamour reads the storage the items category reports on, so its notes describe those same
    // sources.
    [Fact]
    public void Glamour_carries_its_source_notes_alongside_the_facts()
    {
        var notes = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = new ItemSourceStatus { State = SourceStates.Live },
            [SourceKeys.Retainers] =
                new ItemSourceStatus { State = SourceStates.Cached, Count = 2, Total = 3 },
        };

        var result = CollectResult.Glamour(SampleGlamourFacts(), notes);

        Assert.Same(notes, result.SourceNotes);
    }

    // The facts hold several lists, so the log gets the number a reader understands: distinct pieces.
    [Fact]
    public void Glamour_counts_distinct_pieces_for_the_upload_log()
    {
        var facts = SampleGlamourFacts();

        var result = CollectResult.Glamour(facts, new Dictionary<string, ItemSourceStatus>());

        Assert.Equal(GlamourSnapshot.CountPieces(facts), result.FactCount);
        Assert.Equal(6, result.FactCount);
    }

    // Glamour is a snapshot of current holdings, not an id list, so the factory offers no way to claim
    // completeness.
    [Fact]
    public void Glamour_never_claims_a_complete_enumeration()
    {
        var result = CollectResult.Glamour(SampleGlamourFacts(), new Dictionary<string, ItemSourceStatus>());

        Assert.False(result.CompleteEnumeration);
    }

    // The factory never looks inside the record, so any recognizable shape will do.
    private static JsonObject SampleAppearanceFacts() => new()
    {
        ["version"] = 1,
        ["weaponHidden"] = true,
    };

    [Fact]
    public void Appearance_is_a_collected_fact()
    {
        var result = CollectResult.Appearance(SampleAppearanceFacts());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
    }

    // AppearanceSnapshot already wrote the exact wire keys, so the factory hands on the same object.
    [Fact]
    public void Appearance_passes_its_facts_through_unchanged()
    {
        var facts = SampleAppearanceFacts();

        var result = CollectResult.Appearance(facts);

        Assert.Same(facts, result.Facts);
    }

    // One record about the character is not an id list either.
    [Fact]
    public void Appearance_never_claims_a_complete_enumeration()
    {
        Assert.False(CollectResult.Appearance(SampleAppearanceFacts()).CompleteEnumeration);
    }

    // The category declares itself one record, so the upload log shows no number and needs no count.
    [Fact]
    public void Appearance_supplies_no_count_for_the_upload_log()
    {
        Assert.Null(CollectResult.Appearance(SampleAppearanceFacts()).FactCount);
    }

    // A record that arrived was read, so the "nothing read" mark would be false.
    [Fact]
    public void Appearance_is_never_marked_as_nothing_read()
    {
        Assert.False(CollectResult.Appearance(SampleAppearanceFacts()).NothingReadThisPass);
    }
}
