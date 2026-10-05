using System;
using System.Globalization;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Every request body writes its moments through WireTime, and the server matches occult uploads
// from different players to one instance by exact `(encounter, sinceUtc)` pairs, so the format
// must not vary by time zone, fraction or culture.
public class WireTimeTests
{
    [Fact]
    public void A_utc_moment_is_written_with_a_trailing_Z()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 12, 0, 5, TimeSpan.Zero);

        Assert.Equal("2026-09-28T12:00:05Z", WireTime.Format(moment));
    }

    // A moment taken in another time zone is the same moment, so it writes the same UTC text.
    [Fact]
    public void A_moment_with_an_offset_is_written_in_utc()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-09-28T12:00:00Z", WireTime.Format(moment));
    }

    // The fraction is dropped, never rounded up, so a moment is never written later than it was.
    [Fact]
    public void A_fraction_of_a_second_is_dropped_never_rounded_up()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(999);

        Assert.Equal("2026-09-28T12:00:00Z", WireTime.Format(moment));
    }

    // A system whose regional format is Thai counts years in the Buddhist era (2569 for 2026); the
    // wire text keeps the Gregorian year whatever the system's culture.
    [Fact]
    public void The_text_is_the_same_whatever_the_systems_culture()
    {
        var moment = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var original = CultureInfo.CurrentCulture;

        // `try`/`finally` works as in TypeScript: the `finally` block puts the culture back even if
        // the assertion fails.
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
