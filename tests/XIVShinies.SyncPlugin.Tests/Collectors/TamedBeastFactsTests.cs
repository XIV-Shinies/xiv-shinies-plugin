using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The tamedBeasts category carries objects rather than bare ids, because a pact is more than its
// existence — the contract also accepts a rank and a battlehorn slot per beast. These pin the shape
// that leaves the plugin.
public class TamedBeastFactsTests
{
    private static JsonArray ArrayOf(CollectResult result) => result.Facts!.AsArray();

    [Fact]
    public void A_beast_travels_as_an_object_keyed_by_number()
    {
        var result = CollectResult.TamedBeasts(new[] { 30 });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(30u, entry["number"]!.GetValue<uint>());
    }

    // Whether a beast is held is all the plugin can read; a beast's rank and battlehorn slot are
    // not among the slots the bestiary window's records carry. A stray key here would be a claim it
    // cannot support.
    [Fact]
    public void A_beast_carries_no_key_the_plugin_cannot_read()
    {
        var result = CollectResult.TamedBeasts(new[] { 30 });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(new[] { "number" }, entry.Select(pair => pair.Key).ToArray());
    }

    [Fact]
    public void Every_recorded_beast_reaches_the_wire()
    {
        var result = CollectResult.TamedBeasts(new[] { 2, 11, 30 });

        Assert.Equal(3, ArrayOf(result).Count);
    }

    // The same floor the id-list factory enforces: one invalid id makes the server reject the whole
    // upload, every category with it, so a non-positive number can never leave this method.
    [Fact]
    public void A_non_positive_number_never_reaches_the_wire()
    {
        var result = CollectResult.TamedBeasts(new[] { 0, 30, -5 });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(30u, entry["number"]!.GetValue<uint>());
    }

    // "The bestiary has not been read yet" is a real answer, and distinct from a skip: a skip would
    // claim the source could not be read.
    [Fact]
    public void An_empty_session_is_still_a_collected_fact()
    {
        var result = CollectResult.TamedBeasts(Array.Empty<int>());

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        Assert.Empty(ArrayOf(result));
    }

    // Zero is a claim, and for a collection that cannot shrink it reads as a loss. The upload log
    // names the category without a count instead.
    [Fact]
    public void An_empty_session_reports_nothing_read_rather_than_a_count_of_zero()
    {
        Assert.True(CollectResult.TamedBeasts(Array.Empty<int>()).NothingReadThisPass);
    }

    [Fact]
    public void A_session_with_a_pact_does_not_report_nothing_read()
    {
        Assert.False(CollectResult.TamedBeasts(new[] { 30 }).NothingReadThisPass);
    }

    // A list that was ONLY padding is the same story as an empty one, not a different one.
    [Fact]
    public void A_list_of_only_invalid_numbers_reports_nothing_read()
    {
        var result = CollectResult.TamedBeasts(new[] { 0, -1 });

        Assert.True(result.WasCollected);
        Assert.True(result.NothingReadThisPass);
        Assert.Empty(ArrayOf(result));
    }

    // The claim is the collector's to make, and it only makes it once the whole bestiary has been
    // read. A caller that does not ask gets the safe answer.
    [Fact]
    public void The_claim_is_withheld_unless_the_collector_asks_for_it()
    {
        Assert.False(CollectResult.TamedBeasts(new[] { 2, 11, 30 }).CompleteEnumeration);
        Assert.False(CollectResult.TamedBeasts(Array.Empty<int>()).CompleteEnumeration);
    }

    [Fact]
    public void The_collected_detail_is_carried_through_for_the_read_status_chip()
    {
        var result = CollectResult.TamedBeasts(new[] { 30 }, collectedDetail: "Counts what it saw.");

        Assert.Equal("Counts what it saw.", result.CollectedDetail);
    }

    // The serializer's camelCase policy is what turns the C# property into the contract's key, so
    // this pins the policy rather than the property name.
    [Fact]
    public void The_wire_key_is_camel_cased_by_the_shared_serializer_policy()
    {
        var json = JsonSerializer.Serialize(new[] { new TamedBeast { Number = 30 } }, ApiJson.Options);

        Assert.Equal("[{\"number\":30}]", json);
    }
}
