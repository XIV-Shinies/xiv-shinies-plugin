using System;
using Xunit;
using XIVShinies.SyncPlugin.Achievements;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Achievements;

// The ledger holds the newest reading per achievement and skips one the contract refuses.
public class AchievementProgressLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static AchievementProgressReading Reading(
        uint id, uint current, uint max, DateTimeOffset? at = null) =>
        new() { Id = id, Current = current, Max = max, ObservedAt = at ?? T0 };

    [Fact]
    public void A_recorded_reading_is_held()
    {
        var ledger = new AchievementProgressLedger();

        Assert.True(ledger.Record(Reading(3, 750, 1000)));

        Assert.Equal(new[] { Reading(3, 750, 1000) }, ledger.Snapshot());
    }

    [Fact]
    public void A_newer_reading_of_the_same_achievement_replaces_the_older()
    {
        var ledger = new AchievementProgressLedger();
        ledger.Record(Reading(3, 750, 1000, T0));

        ledger.Record(Reading(3, 800, 1000, T0.AddMinutes(1)));

        Assert.Equal(new[] { Reading(3, 800, 1000, T0.AddMinutes(1)) }, ledger.Snapshot());
    }

    [Fact]
    public void A_reading_the_website_stored_is_dropped()
    {
        var ledger = new AchievementProgressLedger();
        ledger.Record(Reading(3, 750, 1000));
        ledger.Record(Reading(4, 2, 5));

        ledger.DropStored(new[] { Reading(3, 750, 1000) });

        Assert.Equal(new[] { Reading(4, 2, 5) }, ledger.Snapshot());
    }

    // A reading taken while the upload was in flight was never sent, so it stays.
    [Fact]
    public void A_newer_reading_outlives_the_store_of_the_one_it_replaced()
    {
        var ledger = new AchievementProgressLedger();
        ledger.Record(Reading(3, 750, 1000, T0));
        ledger.Record(Reading(3, 800, 1000, T0.AddMinutes(1)));

        ledger.DropStored(new[] { Reading(3, 750, 1000, T0) });

        Assert.Equal(new[] { Reading(3, 800, 1000, T0.AddMinutes(1)) }, ledger.Snapshot());
    }

    [Fact]
    public void Clearing_drops_every_reading()
    {
        var ledger = new AchievementProgressLedger();
        ledger.Record(Reading(3, 750, 1000));

        ledger.Clear();

        Assert.Empty(ledger.Snapshot());
    }

    // The contract rejects a whole upload for one entry outside its bounds. A reading is skipped, never
    // clamped: a clamped value would be one the game never showed.
    [Theory]
    [InlineData(0u, 1u, 5u)]
    [InlineData(1_000_001u, 1u, 5u)]
    [InlineData(3u, 1u, 0u)]
    [InlineData(3u, 2_147_483_648u, 2_147_483_648u)]
    [InlineData(3u, 1u, 2_147_483_648u)]
    public void A_reading_outside_the_contract_is_skipped(uint id, uint current, uint max)
    {
        var ledger = new AchievementProgressLedger();

        Assert.False(ledger.Record(Reading(id, current, max)));

        Assert.Empty(ledger.Snapshot());
    }

    // A bad reading must not cost a good one already held for the same achievement.
    [Fact]
    public void A_skipped_reading_leaves_the_held_one_in_place()
    {
        var ledger = new AchievementProgressLedger();
        ledger.Record(Reading(3, 750, 1000));

        ledger.Record(Reading(3, 750, 0));

        Assert.Equal(new[] { Reading(3, 750, 1000) }, ledger.Snapshot());
    }

    // The server decides what a count past its target means, so it goes as the game showed it.
    [Theory]
    [InlineData(1u, 0u, 1u)]
    [InlineData(1_000_000u, 2_147_483_647u, 2_147_483_647u)]
    [InlineData(3u, 1200u, 1000u)]
    public void A_reading_inside_the_contract_is_kept_as_shown(uint id, uint current, uint max)
    {
        var ledger = new AchievementProgressLedger();

        Assert.True(ledger.Record(Reading(id, current, max)));

        Assert.Equal(new[] { Reading(id, current, max) }, ledger.Snapshot());
    }
}
