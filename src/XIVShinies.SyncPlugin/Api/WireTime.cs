using System;
using System.Globalization;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>The one way a moment is written in a request body.</summary>
public static class WireTime
{
    /// <summary>
    /// The moment as second-exact UTC with a trailing <c>Z</c>, such as
    /// <c>2026-08-11T16:02:15Z</c>. A fraction of a second is dropped.
    /// </summary>
    /// <param name="moment">The moment to write.</param>
    /// <remarks>
    /// The serializer's own <c>DateTimeOffset</c> format would write <c>+00:00</c>, never <c>Z</c>, and
    /// any fraction. The quoted pattern parts are literal, so no culture can swap a separator, and
    /// <c>InvariantCulture</c> keeps the Gregorian year whatever the player's regional format.
    /// </remarks>
    public static string Format(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
