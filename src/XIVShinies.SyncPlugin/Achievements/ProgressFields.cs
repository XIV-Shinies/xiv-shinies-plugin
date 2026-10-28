namespace XIVShinies.SyncPlugin.Achievements;

/// <summary>Where the game's one achievement progress request stands.</summary>
/// <remarks>Mirrors <c>Achievement.AchievementState</c>, so the reading logic needs no game types.</remarks>
public enum ProgressRequestState : byte
{
    /// <summary>Nothing asked for, or the last answer was cleared.</summary>
    Invalid = 0,

    /// <summary>Asked for and not answered yet. The values still hold the previous answer.</summary>
    Requested = 1,

    /// <summary>The server's answer is in the values.</summary>
    Loaded = 2,
}

/// <summary>The four fields the game keeps for its one achievement progress reading.</summary>
/// <param name="State">Where the request stands.</param>
/// <param name="AchievementId">The <c>Achievement</c> sheet row the values belong to.</param>
/// <param name="Current">The progress count the answer gave.</param>
/// <param name="Max">The target the answer gave.</param>
public readonly record struct ProgressFields(
    ProgressRequestState State, uint AchievementId, uint Current, uint Max);
