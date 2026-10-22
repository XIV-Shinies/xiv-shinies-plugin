using System;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>One encounter's wire-ready state: what the upload will say about it.</summary>
public sealed record OccultEncounterState
{
    /// <summary>
    /// True when <see cref="Id"/> is a <c>Fate</c> row id, false for a <c>DynamicEvent</c> row id. The
    /// payload sends them under different keys, <c>fateId</c> and <c>dynamicEventId</c>.
    /// </summary>
    public required bool IsFate { get; init; }

    /// <summary>The sheet row id; <see cref="IsFate"/> says which sheet.</summary>
    public required ushort Id { get; init; }

    /// <summary>The wire status.</summary>
    public required OccultEncounterStatus Status { get; init; }

    /// <summary>When this status began, in whole seconds: the contract's <c>sinceUtc</c>.</summary>
    /// <remarks>
    /// A server-assigned epoch where the game exposes one, which fingerprints the instance; the
    /// plugin's own observation time for a transition the game zeroes; null when nothing is known,
    /// such as an idle CE never seen up.
    /// </remarks>
    public DateTimeOffset? SinceUtc { get; init; }
}
