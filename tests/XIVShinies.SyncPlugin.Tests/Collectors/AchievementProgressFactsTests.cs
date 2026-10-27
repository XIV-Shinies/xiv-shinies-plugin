using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// achievementProgress carries one object per reading. The schema is strict: a stray key, or a value
// outside its bounds, fails the whole upload.
public class AchievementProgressFactsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static AchievementProgressReading Reading(
        uint id, uint current = 750, uint max = 1000, DateTimeOffset? at = null) =>
        new() { Id = id, Current = current, Max = max, ObservedAt = at ?? T0 };

    private static JsonArray ArrayOf(CollectResult result) => result.Facts!.AsArray();

    [Fact]
    public void A_reading_travels_as_exactly_the_contracts_four_keys()
    {
        var result = CollectResult.AchievementProgress(new[] { Reading(3) });

        Assert.Equal(
            "[{\"id\":3,\"current\":750,\"max\":1000,\"observedAt\":\"2026-10-27T12:00:00Z\"}]",
            result.Facts!.ToJsonString());
    }

    // The wire carries second-exact UTC with a trailing Z (see WireTime).
    [Fact]
    public void The_observation_time_is_second_exact_utc()
    {
        var at = new DateTimeOffset(2026, 10, 27, 14, 30, 5, 600, TimeSpan.FromHours(2));

        var result = CollectResult.AchievementProgress(new[] { Reading(3, at: at) });

        Assert.Equal("2026-10-27T12:30:05Z", ArrayOf(result).Single()!["observedAt"]!.GetValue<string>());
    }

    // The ledger already skips these; the factory is the last guard, so no collector can forget.
    [Fact]
    public void A_reading_outside_the_contract_never_reaches_the_wire()
    {
        var result = CollectResult.AchievementProgress(new[] { Reading(0), Reading(3), Reading(4, max: 0) });

        Assert.Equal(3u, ArrayOf(result).Single()!["id"]!.GetValue<uint>());
    }

    // The contract takes at most 1,000 entries. The newest go, since an older reading has had more
    // chances to ride an earlier upload.
    [Fact]
    public void Past_the_contracts_cap_the_newest_readings_go()
    {
        var readings = Enumerable.Range(1, AchievementProgressReading.MaxPerUpload + 1)
            .Select(id => Reading((uint)id, at: T0.AddSeconds(id)))
            .ToArray();

        var result = CollectResult.AchievementProgress(readings);

        var ids = ArrayOf(result).Select(node => node!["id"]!.GetValue<uint>()).ToArray();
        Assert.Equal(AchievementProgressReading.MaxPerUpload, ids.Length);
        Assert.DoesNotContain(1u, ids);
    }

    // A steady order keeps the upload log's fingerprint, and so its "(changed)" mark, honest.
    [Fact]
    public void Readings_go_in_achievement_order_whatever_order_they_were_seen()
    {
        var readings = new[] { Reading(40, at: T0), Reading(3, at: T0.AddMinutes(1)) };

        var result = CollectResult.AchievementProgress(readings);

        Assert.Equal(new uint[] { 3, 40 }, ArrayOf(result).Select(node => node!["id"]!.GetValue<uint>()));
    }

    // Nothing opened means nothing to say. An empty array would spend bytes saying it.
    [Fact]
    public void No_readings_is_a_skip_rather_than_an_empty_list()
    {
        var result = CollectResult.AchievementProgress(Array.Empty<AchievementProgressReading>());

        Assert.False(result.WasCollected);
        Assert.Equal(CollectSkipReasons.NothingSeenYet, result.SkipReason);
    }

    [Fact]
    public void Only_invalid_readings_is_a_skip_too()
    {
        var result = CollectResult.AchievementProgress(new[] { Reading(0) });

        Assert.Equal(CollectSkipReasons.NothingSeenYet, result.SkipReason);
    }

    // Readings are not ids, so they never claim a complete list.
    [Fact]
    public void Readings_never_claim_completeness()
    {
        Assert.False(CollectResult.AchievementProgress(new[] { Reading(3) }).CompleteEnumeration);
    }

    [Fact]
    public void The_collected_detail_is_carried_through_for_the_read_status_chip()
    {
        var result = CollectResult.AchievementProgress(
            new[] { Reading(3) }, collectedDetail: "Read on open.");

        Assert.Equal("Read on open.", result.CollectedDetail);
    }
}
