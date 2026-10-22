using System;
using System.Globalization;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Every request body writes its moments through WireTime. The server matches occult uploads from
// different players to one instance by exact `(encounter, sinceUtc)` pairs, so the text must not vary
// by time zone, fraction or culture.
public class WireTimeTests
{
    [Fact]
    public void A_utc_moment_is_written_with_a_trailing_Z()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 12, 0, 5, TimeSpan.Zero);

        Assert.Equal("2026-09-28T12:00:05Z", WireTime.Format(moment));
    }

    [Fact]
    public void A_moment_with_an_offset_is_written_in_utc()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-09-28T12:00:00Z", WireTime.Format(moment));
    }

    // Dropping the fraction means a moment is never written later than it happened.
    [Fact]
    public void A_fraction_of_a_second_is_dropped_never_rounded_up()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(999);

        Assert.Equal("2026-09-28T12:00:00Z", WireTime.Format(moment));
    }

    // Thai culture counts years in the Buddhist era (2569 for 2026); the wire keeps the Gregorian year.
    [Fact]
    public void The_text_is_the_same_whatever_the_systems_culture()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            Assert.Equal("2026-09-28T12:00:00Z", WireTime.Format(moment));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
