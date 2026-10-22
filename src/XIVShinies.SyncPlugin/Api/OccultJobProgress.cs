namespace XIVShinies.SyncPlugin.Api;

/// <summary>One phantom job's progress, as read from the occult instance director.</summary>
/// <remarks>
/// A wire-input record: collectors produce it, and <see cref="SyncFacts.Progression"/> turns
/// it into JSON.
/// </remarks>
public sealed record OccultJobProgress
{
    /// <summary>
    /// Experience toward the next level, exactly as the game reports it.
    /// <see cref="SyncFacts.Progression"/> enforces the range at the wire boundary.
    /// </summary>
    public required uint Exp { get; init; }

    /// <summary>The job's current level.</summary>
    public required byte Level { get; init; }
}
