using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns what the game says about the saddlebag into the saddlebag source note.
/// </summary>
/// <remarks>
/// <para>
/// The note is <see cref="SourceStates.Cached"/> in two cases. The ordinary one: the item finder's
/// copy was refreshed this session, so what was read is current. The other: the character has no
/// saddlebag at all, because the saddlebag comes with the chocobo companion and the character has
/// not unlocked it. Then "it holds nothing" is known without a read, and the empty source counts
/// as current, so a character without a saddlebag is not held back from every clear by a source
/// that can never be read. Anything else is <see cref="SourceStates.Unscanned"/>.
/// </para>
/// <para>
/// Reading the three flags out of game memory is <see cref="StorageSources.Saddlebag"/>' job;
/// deciding what they mean is this class's, so the rules are unit-tested, the same split as
/// <see cref="RetainerNote"/>.
/// </para>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports plain functions. It holds no game types, which is what lets the tests call it.
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
    /// The quest answer counts only once the player's state has loaded. Before that, the game
    /// answers from whatever it last held, which after a character switch is the previous
    /// character's progress, so a stale "not complete" would report an empty saddlebag as current
    /// for a character whose saddlebag holds pieces, and those pieces would read as gone.
    /// </remarks>
    public static ItemSourceStatus Build(
        bool isCached, bool playerStateLoaded, bool companionQuestComplete)
    {
        // Named rather than inlined, so the two reasons for "read" each have a name.
        var hasNoSaddlebag = playerStateLoaded && !companionQuestComplete;

        return new ItemSourceStatus
        {
            State = (isCached || hasNoSaddlebag) ? SourceStates.Cached : SourceStates.Unscanned,
        };
    }
}
