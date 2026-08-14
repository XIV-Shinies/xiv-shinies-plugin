using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Pins the occultProgression payload to the contract: the jobs map's key and value shape,
// the exp ceiling's omission gate, and the optional knowledge block's level and timestamp
// format. SyncFacts.Progression documents why each rule is what it is.
public class OccultProgressionFactsTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    private static Dictionary<byte, OccultJobProgress> Jobs(params (byte Id, uint Exp, byte Level)[] jobs)
    {
        var map = new Dictionary<byte, OccultJobProgress>();
        foreach (var (id, exp, level) in jobs)
            map[id] = new OccultJobProgress { Exp = exp, Level = level };
        return map;
    }

    [Fact]
    public void Jobs_serialize_keyed_by_digit_string_with_exp_and_level()
    {
        var result = CollectResult.Progression(Jobs((11, 1840, 2)), knowledge: null);

        var jobs = result.Facts!["jobs"]!.AsObject();
        Assert.Equal(1840, jobs["11"]!["exp"]!.GetValue<int>());
        Assert.Equal(2, jobs["11"]!["level"]!.GetValue<int>());
    }

    // Freelancer is MKDSupportJob row 0 and a real job, so the map must carry key "0".
    [Fact]
    public void Job_zero_is_real_and_survives()
    {
        var result = CollectResult.Progression(Jobs((0, 500, 13)), knowledge: null);

        var jobs = result.Facts!["jobs"]!.AsObject();
        Assert.Equal(13, jobs["0"]!["level"]!.GetValue<int>());
    }

    // The schema's exp ceiling is 100,000,000, and a value beyond it takes the job off the wire
    // entirely (SyncFacts.Progression explains why omission beats clamping). Other jobs in the
    // same pass are unaffected.
    [Fact]
    public void A_job_whose_exp_exceeds_the_ceiling_is_omitted_not_clamped()
    {
        var result = CollectResult.Progression(
            Jobs((3, uint.MaxValue, 5), (4, 1840, 2)), knowledge: null);

        var jobs = result.Facts!["jobs"]!.AsObject();
        Assert.False(jobs.ContainsKey("3"));
        Assert.Equal(1840, jobs["4"]!["exp"]!.GetValue<int>());
    }

    // The ceiling itself is a legal value — the omission gate is strictly "beyond", never "at".
    [Fact]
    public void An_exp_exactly_at_the_ceiling_survives_unchanged()
    {
        var result = CollectResult.Progression(Jobs((7, 100_000_000, 9)), knowledge: null);

        Assert.Equal(100_000_000, result.Facts!["jobs"]!["7"]!["exp"]!.GetValue<int>());
    }

    // The contract wants the key PRESENT only when an observation exists — the serializer's
    // null-omission is not in play here because the node is built by hand.
    [Fact]
    public void Knowledge_is_omitted_when_never_observed()
    {
        var result = CollectResult.Progression(Jobs((0, 0, 1)), knowledge: null);

        Assert.False(result.Facts!.AsObject().ContainsKey("knowledge"));
    }

    [Fact]
    public void Knowledge_carries_level_and_a_second_exact_Z_suffixed_stamp()
    {
        var observation = new KnowledgeObservation { Level = 40, ObservedAt = ObservedAt };
        var result = CollectResult.Progression(Jobs((0, 0, 1)), observation);

        var knowledge = result.Facts!["knowledge"]!.AsObject();
        Assert.Equal(40, knowledge["level"]!.GetValue<int>());
        Assert.Equal("2026-08-12T20:00:00Z", knowledge["observedAt"]!.GetValue<string>());
    }

    // The server rejects numeric-offset timestamp forms, so an observation carrying a local
    // offset must be CONVERTED to UTC on the wire, not stamped with its offset digits.
    [Fact]
    public void A_non_UTC_observation_time_is_converted_to_UTC_on_the_wire()
    {
        var observation = new KnowledgeObservation
        {
            Level = 40,
            // 05:00 at +09:00 is 20:00 UTC the previous day.
            ObservedAt = new DateTimeOffset(2026, 8, 13, 5, 0, 0, TimeSpan.FromHours(9)),
        };
        var result = CollectResult.Progression(Jobs((0, 0, 1)), observation);

        Assert.Equal(
            "2026-08-12T20:00:00Z",
            result.Facts!["knowledge"]!["observedAt"]!.GetValue<string>());
    }

    // A fractional observation time must not leak sub-second precision into the stamp.
    [Fact]
    public void The_knowledge_stamp_truncates_to_whole_seconds()
    {
        var observation = new KnowledgeObservation
        {
            Level = 40,
            ObservedAt = ObservedAt + TimeSpan.FromMilliseconds(789),
        };
        var result = CollectResult.Progression(Jobs((0, 0, 1)), observation);

        Assert.Equal(
            "2026-08-12T20:00:00Z",
            result.Facts!["knowledge"]!["observedAt"]!.GetValue<string>());
    }

    [Fact]
    public void A_progression_result_is_a_collected_fact_not_a_skip()
    {
        var result = CollectResult.Progression(Jobs((0, 0, 1)), knowledge: null);

        Assert.True(result.WasCollected);
        Assert.Null(result.SkipReason);
        // The category is a map, not an id list; the scopes vocabulary does not apply to it.
        Assert.False(result.CompleteEnumeration);
    }

    // The partial phrase is for the settings panel alone — it rides the result, never the wire.
    [Fact]
    public void A_partial_note_rides_the_result_and_never_reaches_the_facts()
    {
        var result = CollectResult.Progression(
            Jobs((0, 0, 1)), knowledge: null, partialNote: "half read.");

        Assert.Equal("half read.", result.PartialNote);
        Assert.False(result.Facts!.AsObject().ContainsKey("partialNote"));
    }

    // As is the chip's hover copy.
    [Fact]
    public void A_chip_detail_rides_the_result_and_never_reaches_the_facts()
    {
        var result = CollectResult.Progression(
            Jobs((0, 0, 1)), knowledge: null, collectedDetail: "Optional hover copy.");

        Assert.Equal("Optional hover copy.", result.CollectedDetail);
        Assert.False(result.Facts!.AsObject().ContainsKey("collectedDetail"));
    }

    // A knowledge sighting captured outside an instance travels alone: the jobs key is still
    // present — the contract requires it — but empty, and the server's empty map writes nothing.
    [Fact]
    public void A_knowledge_only_payload_carries_an_empty_jobs_map()
    {
        var observation = new KnowledgeObservation { Level = 40, ObservedAt = ObservedAt };
        var result = CollectResult.Progression(
            new Dictionary<byte, OccultJobProgress>(), observation);

        Assert.Empty(result.Facts!["jobs"]!.AsObject());
        Assert.Equal(40, result.Facts!["knowledge"]!["level"]!.GetValue<int>());
    }

    [Fact]
    public void All_jobs_appear_in_the_map()
    {
        var all = new Dictionary<byte, OccultJobProgress>();
        for (byte i = 0; i < 24; i++)
            all[i] = new OccultJobProgress { Exp = i, Level = 1 };

        var result = CollectResult.Progression(all, knowledge: null);

        Assert.Equal(24, result.Facts!["jobs"]!.AsObject().Count);
    }
}
