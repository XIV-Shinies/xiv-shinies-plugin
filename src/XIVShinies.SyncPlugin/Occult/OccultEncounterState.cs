using System;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// One encounter's wire-ready state: what the upload payload will say about it.
/// </summary>
public sealed record OccultEncounterState
{
    /// <summary>
    /// True when <see cref="Id"/> is a <c>Fate</c> sheet row id, false when it is a
    /// <c>DynamicEvent</c> sheet row id. The payload serializes them under different JSON
    /// keys (<c>fateId</c> / <c>dynamicEventId</c>), so the distinction must survive here.
    /// </summary>
    public required bool IsFate { get; init; }

    /// <summary>The Excel sheet row id (see <see cref="IsFate"/> for which sheet).</summary>
    public required ushort Id { get; init; }

    /// <summary>The three-word wire status.</summary>
    public required OccultEncounterStatus Status { get; init; }

    /// <summary>
    /// The moment this status began, in whole seconds — the contract's <c>sinceUtc</c>.
    /// Server-assigned epochs where the game exposes them (these fingerprint the instance);
    /// the plugin's own observation time only for transitions the game zeroes; null when
    /// nothing is known (an idle CE never observed up).
    /// </summary>
    public DateTimeOffset? SinceUtc { get; init; }
}
