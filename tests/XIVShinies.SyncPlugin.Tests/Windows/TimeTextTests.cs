using System;
using Xunit;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The "last synced …" wording, with its boundaries pinned so each one is a decision.
public class TimeTextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59)]
    public void Under_a_minute_is_just_now(int seconds)
    {
        Assert.Equal("just now", TimeText.Ago(TimeSpan.FromSeconds(seconds)));
    }

    // Clocks jump backwards, and "just now" is the honest reading of an elapsed time we cannot compute.
    [Fact]
    public void A_negative_duration_reads_as_just_now()
    {
        Assert.Equal("just now", TimeText.Ago(TimeSpan.FromSeconds(-45)));
    }

    [Fact]
    public void Exactly_one_minute_is_singular()
    {
        Assert.Equal("1 minute ago", TimeText.Ago(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(2, "2 minutes ago")]
    [InlineData(30, "30 minutes ago")]
    [InlineData(59, "59 minutes ago")]
    public void Minutes_are_plural_and_truncated(int minutes, string expected)
    {
        Assert.Equal(expected, TimeText.Ago(TimeSpan.FromMinutes(minutes)));
    }

    // Never round up: the sync did not happen further back than it did.
    [Fact]
    public void Partial_minutes_truncate_down()
    {
        Assert.Equal("1 minute ago", TimeText.Ago(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void Exactly_one_hour_is_singular()
    {
        Assert.Equal("1 hour ago", TimeText.Ago(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Partial_hours_truncate_down()
    {
        Assert.Equal("1 hour ago", TimeText.Ago(TimeSpan.FromMinutes(90)));
    }

    [Theory]
    [InlineData(2, "2 hours ago")]
    [InlineData(26, "26 hours ago")]
    public void Hours_are_plural_and_unbounded(int hours, string expected)
    {
        Assert.Equal(expected, TimeText.Ago(TimeSpan.FromHours(hours)));
    }

    // The settings' cadence wording. The server tunes the interval at runtime, so the wording must
    // survive any value the clamp allows.
    [Theory]
    [InlineData(5, "5 minutes")]
    [InlineData(30, "30 minutes")]
    [InlineData(90, "90 minutes")]
    public void Intervals_in_minutes_read_as_minutes(int minutes, string expected)
    {
        Assert.Equal(expected, TimeText.Interval(TimeSpan.FromMinutes(minutes)));
    }

    // Nobody says "every 120 minutes".
    [Theory]
    [InlineData(1, "1 hour")]
    [InlineData(2, "2 hours")]
    [InlineData(24, "24 hours")]
    public void Whole_hour_intervals_read_as_hours(int hours, string expected)
    {
        Assert.Equal(expected, TimeText.Interval(TimeSpan.FromHours(hours)));
    }

    [Fact]
    public void A_one_minute_interval_is_singular()
    {
        Assert.Equal("1 minute", TimeText.Interval(TimeSpan.FromMinutes(1)));
    }

    // The scheduler clamps at five minutes, so this never reaches the UI; the formatter still avoids
    // "0 minutes".
    [Fact]
    public void Intervals_below_a_minute_read_as_one_minute()
    {
        Assert.Equal("1 minute", TimeText.Interval(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Negative_intervals_also_floor_to_one_minute()
    {
        Assert.Equal("1 minute", TimeText.Interval(TimeSpan.FromMinutes(-5)));
    }
}
