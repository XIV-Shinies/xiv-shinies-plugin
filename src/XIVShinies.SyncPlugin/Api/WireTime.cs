using System;
using System.Globalization;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>The one way a moment is written in a request body.</summary>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class WireTime
{
    /// <summary>
    /// The moment as second-exact UTC with a trailing <c>Z</c>, such as
    /// <c>2026-08-11T16:02:15Z</c>. A fraction of a second is dropped.
    /// </summary>
    /// <param name="moment">The moment to write.</param>
    /// <remarks>
    /// The serializer's own <c>DateTimeOffset</c> format would write the moment's offset
    /// (<c>+00:00</c> for UTC, never <c>Z</c>) and any fraction, which is why request bodies carry
    /// this text instead. The quoted parts of the pattern are literal characters, so no culture can
    /// swap a separator, and <c>InvariantCulture</c> keeps the Gregorian calendar, so the year is
    /// the same whatever regional format (culture) the player's system uses.
    /// </remarks>
    // A `DateTimeOffset` is a moment plus the offset from UTC it was taken in; `.UtcDateTime` is
    // the same moment moved to UTC, so a moment from any time zone writes the same text. `=>`
    // makes the expression after it the whole method body, like an arrow function.
    public static string Format(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
