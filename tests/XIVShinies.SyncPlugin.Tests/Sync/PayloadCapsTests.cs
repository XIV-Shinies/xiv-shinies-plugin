using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Glamour;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Enforces the contract's payload caps client-side, so an over-cap category is truncated rather
// than costing the whole upload a 400.
public class PayloadCapsTests
{
    // The shape SyncFacts.Ids produces.
    private static JsonArray IdArray(int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
            array.Add(JsonValue.Create((uint)(i + 1)));

        return array;
    }

    // The shape SyncFacts.Items produces.
    private static JsonArray EntryArray(int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            array.Add(new JsonObject
            {
                ["id"] = i + 1,
                ["count"] = 1,
                ["fresh"] = true,
            });
        }

        return array;
    }

    [Fact]
    public void An_id_array_over_the_cap_is_truncated_to_exactly_the_cap()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(PayloadCaps.MaxIdsPerCategory + 1),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(PayloadCaps.MaxIdsPerCategory, ((JsonArray)bounded["quests"]).Count);
        Assert.Single(dropped);
    }

    [Fact]
    public void An_id_array_over_the_cap_names_the_category_and_the_number_dropped()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(PayloadCaps.MaxIdsPerCategory + 1),
        };

        var (_, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(
            $"quests: dropped 1 entry over the contract cap of {PayloadCaps.MaxIdsPerCategory}",
            Assert.Single(dropped).Line);
    }

    [Fact]
    public void An_entry_array_over_the_cap_is_truncated_to_exactly_the_cap()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["items"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 1),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(PayloadCaps.MaxEntriesPerCategory, ((JsonArray)bounded["items"]).Count);
        Assert.Single(dropped);
    }

    [Fact]
    public void An_entry_array_over_the_cap_reports_the_drop()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["items"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 1),
        };

        var (_, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(
            $"items: dropped 1 entry over the contract cap of {PayloadCaps.MaxEntriesPerCategory}",
            Assert.Single(dropped).Line);
    }

    [Fact]
    public void A_compliant_payload_comes_back_with_no_report_entries()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(5),
            ["items"] = EntryArray(5),
        };

        var (_, dropped) = PayloadCaps.Bound(collections);

        Assert.Empty(dropped);
    }

    [Fact]
    public void A_compliant_payload_is_returned_by_the_same_dictionary_instance()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(5),
        };

        var (bounded, _) = PayloadCaps.Bound(collections);

        Assert.Same(collections, bounded);
        Assert.Same(collections["quests"], bounded["quests"]);
    }

    [Fact]
    public void An_empty_array_passes_through_untouched()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = new JsonArray(),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Empty(dropped);
        Assert.Same(collections["quests"], bounded["quests"]);
    }

    [Fact]
    public void A_non_array_category_value_passes_through_untouched()
    {
        // The caps limit array counts. Any other shape, such as the questSequences object, rides
        // through.
        var collections = new Dictionary<string, JsonNode>
        {
            ["future"] = new JsonObject { ["someFlag"] = true },
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Empty(dropped);
        Assert.Same(collections, bounded);
        Assert.Same(collections["future"], bounded["future"]);
    }

    // Glamour is read as current holdings, so a cut list would report every piece left off as gone;
    // GlamourSnapshot withholds the whole category instead. These caps must cut only top-level arrays.
    [Fact]
    public void A_glamour_object_passes_through_untouched_whatever_its_inner_lists_hold()
    {
        var held = Enumerable.Range(1, PayloadCaps.MaxIdsPerCategory + 1)
            .Select(i => new HeldPiece { Id = (uint)i, Place = HeldPlaces.Bags, Count = 1 })
            .ToArray();
        var dresser = Enumerable.Range(1, PayloadCaps.MaxEntriesPerCategory + 1)
            .Select(i => new DresserPiece { Id = (uint)i })
            .ToArray();
        var glamour = SyncFacts.Glamour(new GlamourFacts { Dresser = dresser, Held = held });

        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode> { [CategoryKeys.Glamour] = glamour },
            Skipped = new Dictionary<string, string>(),
            CompleteKeys = new HashSet<string>(),
        };

        var (bounded, dropped) = PayloadCaps.Bound(snapshot);

        Assert.Empty(dropped);
        Assert.Same(snapshot, bounded);
        Assert.Same(glamour, bounded.Collections[CategoryKeys.Glamour]);
        Assert.Equal(held.Length, glamour["held"]!.AsArray().Count);
        Assert.Equal(dresser.Length, glamour["dresser"]!.AsArray().Count);
    }

    // A truncated list is not the character's complete set (see PayloadCaps.Bound).
    [Fact]
    public void Capping_a_category_retracts_its_completeness_claim()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["facewear"] = IdArray(PayloadCaps.MaxIdsPerCategory + 1),
            },
            Skipped = new Dictionary<string, string>(),
            CompleteKeys = new HashSet<string> { "facewear" },
        };

        var (bounded, dropped) = PayloadCaps.Bound(snapshot);

        Assert.Single(dropped);
        Assert.DoesNotContain("facewear", bounded.CompleteKeys);
    }

    [Fact]
    public void Capping_a_category_withdraws_its_store_confirmation()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["facewear"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 1),
                ["ornaments"] = EntryArray(3),
            },
            Skipped = new Dictionary<string, string>(),
            StoreConfirmations = new Dictionary<string, StoreConfirmation>
            {
                ["facewear"] = new("storedFacewear", () => { }),
                ["ornaments"] = new("storedOrnaments", () => { }),
            },
        };

        var (bounded, _) = PayloadCaps.Bound(snapshot);

        Assert.Equal(new[] { "ornaments" }, bounded.StoreConfirmations.Keys);
    }

    // The copy a cut makes must keep everything it does not cut: the glamour object ships as read,
    // and the container notes and single-record keys still reach the settings window and the log.
    [Fact]
    public void Capping_one_category_leaves_a_glamour_object_and_the_snapshots_other_keys_alone()
    {
        var glamour = SyncFacts.Glamour(new GlamourFacts
        {
            Dresser = new[] { new DresserPiece { Id = 2642 } },
            Held = new[] { new HeldPiece { Id = 2965, Place = HeldPlaces.Bags, Count = 1 } },
        });
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = new ItemSourceStatus { State = SourceStates.Live },
        };
        var singleRecordKeys = new HashSet<string> { CategoryKeys.Appearance };

        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>
            {
                [CategoryKeys.Glamour] = glamour,
                ["facewear"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 1),
            },
            Skipped = new Dictionary<string, string>(),
            StoreConfirmations = new Dictionary<string, StoreConfirmation>
            {
                ["facewear"] = new("storedFacewear", () => { }),
            },
            SourceNotes = sourceNotes,
            SingleRecordKeys = singleRecordKeys,
        };

        var (bounded, dropped) = PayloadCaps.Bound(snapshot);

        Assert.Equal("facewear", Assert.Single(dropped).CategoryKey);
        Assert.Empty(bounded.StoreConfirmations);
        Assert.Same(glamour, bounded.Collections[CategoryKeys.Glamour]);
        Assert.Same(sourceNotes, bounded.SourceNotes);
        Assert.Equal(SourceStates.Live, Assert.Single(bounded.SourceNotes).Value.State);
        Assert.Same(singleRecordKeys, bounded.SingleRecordKeys);
        Assert.Equal(new[] { CategoryKeys.Appearance }, bounded.SingleRecordKeys);
    }

    // One over-cap category must not disarm the upload's other declarations.
    [Fact]
    public void Capping_one_category_leaves_another_categorys_claim_intact()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["facewear"] = IdArray(PayloadCaps.MaxIdsPerCategory + 1),
                ["ornaments"] = IdArray(3),
            },
            Skipped = new Dictionary<string, string>(),
            CompleteKeys = new HashSet<string> { "facewear", "ornaments" },
        };

        var (bounded, _) = PayloadCaps.Bound(snapshot);

        Assert.DoesNotContain("facewear", bounded.CompleteKeys);
        Assert.Contains("ornaments", bounded.CompleteKeys);
    }

    [Fact]
    public void A_compliant_snapshot_is_returned_as_the_same_instance()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode> { ["facewear"] = IdArray(5) },
            Skipped = new Dictionary<string, string>(),
            CompleteKeys = new HashSet<string> { "facewear" },
        };

        var (bounded, dropped) = PayloadCaps.Bound(snapshot);

        Assert.Empty(dropped);
        Assert.Same(snapshot, bounded);
        Assert.Contains("facewear", bounded.CompleteKeys);
    }

    [Fact]
    public void Multiple_over_cap_categories_are_each_truncated_with_their_own_report_line()
    {
        var collections = new Dictionary<string, JsonNode>
        {
            ["quests"] = IdArray(PayloadCaps.MaxIdsPerCategory + 2),
            ["items"] = EntryArray(PayloadCaps.MaxEntriesPerCategory + 3),
        };

        var (bounded, dropped) = PayloadCaps.Bound(collections);

        Assert.Equal(PayloadCaps.MaxIdsPerCategory, ((JsonArray)bounded["quests"]).Count);
        Assert.Equal(PayloadCaps.MaxEntriesPerCategory, ((JsonArray)bounded["items"]).Count);
        Assert.Equal(2, dropped.Count);
        Assert.Contains(
            dropped,
            drop => drop.Line ==
                $"quests: dropped 2 entries over the contract cap of {PayloadCaps.MaxIdsPerCategory}");
        Assert.Contains(
            dropped,
            drop => drop.Line ==
                $"items: dropped 3 entries over the contract cap of {PayloadCaps.MaxEntriesPerCategory}");
    }
}
