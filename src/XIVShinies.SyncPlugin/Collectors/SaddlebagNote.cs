using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns what the game says about the saddlebag into the saddlebag source note.
/// </summary>
/// <remarks>
/// Cached when the item finder's copy was refreshed this session, or when the character has no
/// saddlebag because it has not unlocked the chocobo companion. That empty source counts as current,
/// so a source that can never be read does not hold back every clear. Anything else is unscanned.
/// </remarks>
public static class SaddlebagNote
{
    /// <summary>Builds the saddlebag source note.</summary>
    /// <param name="isCached">
    /// True when the item finder's copy of the saddlebag was refreshed this session.
    /// </param>
    /// <param name="playerStateLoaded">
    /// True when the game has loaded the logged-in character's own state.
    /// </param>
    /// <param name="companionQuestComplete">
    /// What the game reports for the quest that unlocks the chocobo companion, and with it the
    /// saddlebag.
    /// </param>
    /// <remarks>
    /// The quest answer counts only once the player's state has loaded. Before that, after a
    /// character switch, it is the previous character's progress, and a stale "not complete" would
    /// report an empty saddlebag as current and make the pieces in it read as gone.
    /// </remarks>
    public static ItemSourceStatus Build(
        bool isCached, bool playerStateLoaded, bool companionQuestComplete)
    {
        var hasNoSaddlebag = playerStateLoaded && !companionQuestComplete;

        return new ItemSourceStatus
        {
            State = (isCached || hasNoSaddlebag) ? SourceStates.Cached : SourceStates.Unscanned,
        };
    }
}
