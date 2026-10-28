using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The progress an achievement's details panel showed when the player opened it, as the game server's
/// answer gave it.
/// </summary>
/// <remarks>
/// Sent as the game showed it: a count past its target, or a target of 1 for a count the game keeps as
/// a flag, is the server's to interpret. A reading outside the contract's bounds is skipped, never
/// clamped, because a clamped value is one the game never showed.
/// </remarks>
public sealed record AchievementProgressReading
{
    /// <summary>The most readings the contract accepts in one upload.</summary>
    public const int MaxPerUpload = 1000;

    /// <summary>The highest achievement id the contract accepts.</summary>
    public const uint MaxId = 1_000_000;

    /// <summary>The highest count or target the contract accepts.</summary>
    public const uint MaxValue = int.MaxValue;

    /// <summary>The <c>Achievement</c> sheet row id.</summary>
    public required uint Id { get; init; }

    /// <summary>The progress the details panel showed.</summary>
    public required uint Current { get; init; }

    /// <summary>The target shown beside it.</summary>
    public required uint Max { get; init; }

    /// <summary>When the answer was seen, within a frame of the client receiving it.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    /// True when the contract accepts every value; one that does not fails the whole upload.
    /// </summary>
    public bool IsWithinContract =>
        Id is >= 1 and <= MaxId && Max is >= 1 and <= MaxValue && Current <= MaxValue;
}
