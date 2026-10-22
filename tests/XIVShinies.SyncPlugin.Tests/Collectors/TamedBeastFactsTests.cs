using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// tamedBeasts carries objects, not bare ids: the contract also accepts a rank and a battlehorn slot
// per beast. The plugin sends the number always, the rank once read, the battlehorn never.
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

    // A stray key would be a claim the plugin cannot support.
    [Fact]
    public void A_beast_with_no_rank_read_carries_only_its_number()
    {
        var result = CollectResult.TamedBeasts(new[] { 30 });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(new[] { "number" }, entry.Select(pair => pair.Key).ToArray());
    }

    [Fact]
    public void A_read_rank_travels_beside_its_beast()
    {
        var result = CollectResult.TamedBeasts(new[] { 2, 30 }, ranks: new Dictionary<int, int> { [30] = 9 });

        var entries = ArrayOf(result).Select(node => node!.AsObject()).ToArray();
        Assert.Equal(new[] { "number" }, entries[0].Select(pair => pair.Key).ToArray());
        Assert.Equal(9, entries[1]["rank"]!.GetValue<int>());
    }

    // The list is what says a beast is held, so a rank for an unlisted beast would claim a pact
    // nothing has shown.
    [Fact]
    public void A_rank_for_a_beast_not_listed_is_not_sent()
    {
        var result = CollectResult.TamedBeasts(new[] { 30 }, ranks: new Dictionary<int, int> { [31] = 4 });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(30u, entry["number"]!.GetValue<uint>());
        Assert.Null(entry["rank"]);
    }

    // The contract accepts 1 to 25, and a rank outside it would make the server reject the whole
    // upload. The beast goes up without one.
    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(-1)]
    public void A_rank_outside_the_contracts_range_is_dropped(int rank)
    {
        var result = CollectResult.TamedBeasts(new[] { 30 }, ranks: new Dictionary<int, int> { [30] = rank });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(new[] { "number" }, entry.Select(pair => pair.Key).ToArray());
    }

    [Fact]
    public void Every_recorded_beast_reaches_the_wire()
    {
        var result = CollectResult.TamedBeasts(new[] { 2, 11, 30 });

        Assert.Equal(3, ArrayOf(result).Count);
    }

    // One invalid id makes the server reject the whole upload, every category with it.
    [Fact]
    public void A_non_positive_number_never_reaches_the_wire()
    {
        var result = CollectResult.TamedBeasts(new[] { 0, 30, -5 });

        var entry = ArrayOf(result).Single()!.AsObject();
        Assert.Equal(30u, entry["number"]!.GetValue<uint>());
    }

    // "The bestiary has not been read yet" is a real answer. A skip would claim the source could not
    // be read.
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

    [Fact]
    public void A_list_of_only_invalid_numbers_reports_nothing_read()
    {
        var result = CollectResult.TamedBeasts(new[] { 0, -1 });

        Assert.True(result.WasCollected);
        Assert.True(result.NothingReadThisPass);
        Assert.Empty(ArrayOf(result));
    }

    // The collector asks for the claim only once the whole bestiary has been read.
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

    // The serializer's camelCase policy makes the contract's key, so this pins the policy, not the
    // property name.
    [Fact]
    public void The_wire_key_is_camel_cased_by_the_shared_serializer_policy()
    {
        var json = JsonSerializer.Serialize(new[] { new TamedBeast { Number = 30 } }, ApiJson.Options);

        Assert.Equal("[{\"number\":30}]", json);
    }

    [Fact]
    public void A_rank_reaches_the_wire_under_the_contracts_key()
    {
        var json = JsonSerializer.Serialize(
            new[] { new TamedBeast { Number = 30, Rank = 9 } }, ApiJson.Options);

        Assert.Equal("[{\"number\":30,\"rank\":9}]", json);
    }

    [Theory]
    [InlineData(TamedBeast.MinRank)]
    [InlineData(TamedBeast.MaxRank)]
    public void The_ends_of_the_contracts_range_reach_the_wire(int rank)
    {
        var result = CollectResult.TamedBeasts(new[] { 30 }, ranks: new Dictionary<int, int> { [30] = rank });

        Assert.Equal(rank, ArrayOf(result).Single()!["rank"]!.GetValue<int>());
    }
}
