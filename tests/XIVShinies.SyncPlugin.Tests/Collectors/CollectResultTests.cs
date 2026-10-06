using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// CollectResult is the boundary every collector's output passes through, so the contract's
// "IDs are positive integers" rule is enforced here once rather than in each game-touching class.
public class CollectResultTests
{
    private static uint[] IdsOf(CollectResult result) =>
        result.Facts!.AsArray().GetValues<uint>().ToArray();

    // The game's sheets start with a blank row 0. A single zero on the wire makes the server reject
    // the ENTIRE upload, so it must never leave this method.
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

    // ...and a list that was ONLY padding collapses to an empty array, not to a skip.
    [Fact]
    public void A_list_of_only_zeroes_becomes_an_empty_array_not_a_skip()
    {
        var result = CollectResult.Ids(new uint[] { 0, 0 });

        Assert.True(result.WasCollected);
        Assert.Empty(result.Facts!.AsArray());
    }

    // Getting this claim wrong is asymmetric: false only withholds evidence, while true makes an
    // id's absence count as evidence server-side. So it defaults to false and a collector has to
    // ask for it.
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

    // "I own nothing" and "the game had not filled this in yet" read identically from here, and an
    // empty list declared complete would contradict every entry the user marked by hand at once.
    [Fact]
    public void An_empty_list_never_claims_a_complete_enumeration()
    {
        var result = CollectResult.Ids(Array.Empty<uint>(), completeEnumeration: true);

        Assert.True(result.WasCollected);
        Assert.False(result.CompleteEnumeration);
    }

    // The same guard applies after the zero filter: a list of nothing but sheet padding collapses to
    // empty, and an empty list is exactly what must not be declared complete.
    [Fact]
    public void A_list_of_only_padding_never_claims_a_complete_enumeration()
    {
        var result = CollectResult.Ids(new uint[] { 0, 0 }, completeEnumeration: true);

        Assert.Empty(result.Facts!.AsArray());
        Assert.False(result.CompleteEnumeration);
    }

    // Both of these belong to manifest-scoped collections: their scope is whatever the server asked
    // about, never the whole domain, so there is deliberately no way for them to claim completeness.
    [Fact]
    public void Item_and_sequence_facts_cannot_claim_a_complete_enumeration()
    {
        var items = CollectResult.Items(Array.Empty<ItemPossession>());
        var sequences = CollectResult.Sequences(new Dictionary<uint, byte>());

        Assert.False(items.CompleteEnumeration);
        Assert.False(sequences.CompleteEnumeration);
    }

    // A skip read nothing, so there is nothing for it to have read completely.
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

    // An item COUNT of zero is legitimate per the contract, so Items must not filter the way Ids
    // does. (In practice the collector only reports positives, but the boundary must not lie.)
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

    // "Every manifested quest was checked and none is in the journal" is a real fact, distinct
    // from a skip — the same collected-vs-skipped line the Ids and Items factories draw.
    [Fact]
    public void An_empty_sequence_map_is_still_a_collected_fact()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Empty(result.Facts!.AsObject());
    }

    // The same positive-integer defense Ids applies: the server rejects the whole upload over an
    // invalid id, and this boundary is the one funnel where no collector can forget the rule.
    [Fact]
    public void Sequences_drops_a_zero_quest_id()
    {
        var result = CollectResult.Sequences(new Dictionary<uint, byte> { [0] = 2, [70562] = 3 });

        var facts = result.Facts!.AsObject();
        Assert.False(facts.ContainsKey("0"));
        Assert.Equal(3, facts["70562"]!.GetValue<int>());
    }

    // Glamour facts in which items repeat across containers: 10 is both a loose dresser piece and in
    // the Armoire, and 21 is both inside an outfit and held. The outfit's own id (500) is not a
    // piece, and a held piece's copies are not separate pieces. So the distinct pieces are 10, 11,
    // 20, 21, 30 and 40: six, where a count of the facts' shape would say something else entirely.
    private static GlamourFacts SampleGlamourFacts() => new()
    {
        Dresser = new[]
        {
            new DresserPiece { Id = 10 },
            new DresserPiece { Id = 11, Hq = true, Stains = new[] { 0, 5 } },
        },
        OutfitGlamours = new[] { new OutfitGlamour { OutfitId = 500, PieceIds = new uint[] { 20, 21 } } },
        Armoire = new uint[] { 10, 30 },
        Held = new[] { new HeldPiece { Id = 21, Count = 1 }, new HeldPiece { Id = 40, Count = 3 } },
    };

    // The factory is the one place the glamour facts are turned into wire JSON for a collector, so
    // its output is pinned as the literal text the server receives: every key, in order, with
    // nothing added and nothing reshaped. A raw string literal ("""...""") holds the JSON verbatim,
    // with no escaping of its quotes.
    [Fact]
    public void Glamour_serializes_its_facts_to_the_contract_json()
    {
        var result = CollectResult.Glamour(
            SampleGlamourFacts(), new Dictionary<string, ItemSourceStatus>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Equal(
            """{"version":1,"dresser":[{"id":10},{"id":11,"hq":true,"stains":[0,5]}],"outfitGlamours":[{"outfitId":500,"pieceIds":[20,21]}],"armoire":[10,30],"held":[{"id":21,"count":1},{"id":40,"count":3}]}""",
            result.Facts!.ToJsonString(ApiJson.Options));
    }

    // Glamour reads the same storage the items category reports on, so its notes describe those
    // sources and must reach the runner untouched.
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

    // The facts are a wrapper around several lists, so the log is told the number that means
    // something to a reader: how many distinct pieces they mention.
    [Fact]
    public void Glamour_counts_distinct_pieces_for_the_upload_log()
    {
        var facts = SampleGlamourFacts();

        var result = CollectResult.Glamour(facts, new Dictionary<string, ItemSourceStatus>());

        Assert.Equal(GlamourSnapshot.CountPieces(facts), result.FactCount);
        Assert.Equal(6, result.FactCount);
    }

    // Glamour is a snapshot of current holdings, not an id list, so the completeness vocabulary does
    // not apply to it and the factory offers no way to claim it.
    [Fact]
    public void Glamour_never_claims_a_complete_enumeration()
    {
        var result = CollectResult.Glamour(SampleGlamourFacts(), new Dictionary<string, ItemSourceStatus>());

        Assert.False(result.CompleteEnumeration);
    }

    // A small stand-in for an appearance record. The factory never looks inside the object, so the
    // shape only needs to be recognizable when it comes back out.
    private static JsonObject SampleAppearanceFacts() => new()
    {
        ["version"] = 1,
        ["weaponHidden"] = true,
    };

    // A read appearance is a collected fact like any other: it travels, and it is not a skip.
    [Fact]
    public void Appearance_is_a_collected_fact()
    {
        var result = CollectResult.Appearance(SampleAppearanceFacts());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
    }

    // The builder already wrote the exact wire keys, so the factory must hand the very same object
    // on: nothing added, nothing reshaped, nothing copied.
    [Fact]
    public void Appearance_passes_its_facts_through_unchanged()
    {
        var facts = SampleAppearanceFacts();

        var result = CollectResult.Appearance(facts);

        Assert.Same(facts, result.Facts);
    }

    // One record about the character is not an id list, so the completeness vocabulary does not
    // apply and the factory offers no way to claim it.
    [Fact]
    public void Appearance_never_claims_a_complete_enumeration()
    {
        Assert.False(CollectResult.Appearance(SampleAppearanceFacts()).CompleteEnumeration);
    }

    // The upload log names a single-record category without a number because the category
    // declares itself one record. The factory therefore supplies no count of its own.
    [Fact]
    public void Appearance_supplies_no_count_for_the_upload_log()
    {
        Assert.Null(CollectResult.Appearance(SampleAppearanceFacts()).FactCount);
    }

    // The "nothing read" mark says the pass read none of what the category is about, which is
    // false of a record that arrived, so the factory never sets it.
    [Fact]
    public void Appearance_is_never_marked_as_nothing_read()
    {
        Assert.False(CollectResult.Appearance(SampleAppearanceFacts()).NothingReadThisPass);
    }
}
