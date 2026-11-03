using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The saddlebag note can say "read" for two reasons, one of them without any read at all.
public class SaddlebagNoteTests
{
    // A saddlebag opened and closed this session has a current copy, whatever the companion quest
    // reads.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_copy_refreshed_this_session_is_cached(bool playerStateLoaded, bool companionQuestComplete)
    {
        var note = SaddlebagNote.Build(
            isCached: true,
            playerStateLoaded: playerStateLoaded,
            companionQuestComplete: companionQuestComplete);

        Assert.Equal(SourceStates.Cached, note.State);
    }

    // A loaded character without its chocobo companion has no saddlebag, so "it holds nothing" is
    // known without a read, and the empty source counts as current.
    [Fact]
    public void A_loaded_character_without_a_companion_has_an_empty_saddlebag_that_counts_as_read()
    {
        var note = SaddlebagNote.Build(
            isCached: false, playerStateLoaded: true, companionQuestComplete: false);

        Assert.Equal(SourceStates.Cached, note.State);
    }

    [Fact]
    public void A_loaded_character_with_a_companion_and_no_read_is_unscanned()
    {
        var note = SaddlebagNote.Build(
            isCached: false, playerStateLoaded: true, companionQuestComplete: true);

        Assert.Equal(SourceStates.Unscanned, note.State);
    }

    // Before the player's state loads, the quest check may still answer for the previous character.
    // A stale "not complete" read as "has none" sends an empty saddlebag as current, clearing pieces.
    [Fact]
    public void A_quest_answer_read_before_the_player_state_loads_is_ignored()
    {
        var note = SaddlebagNote.Build(
            isCached: false, playerStateLoaded: false, companionQuestComplete: false);

        Assert.Equal(SourceStates.Unscanned, note.State);
    }

    [Fact]
    public void The_note_carries_no_counts()
    {
        var note = SaddlebagNote.Build(
            isCached: false, playerStateLoaded: true, companionQuestComplete: false);

        Assert.Null(note.Count);
        Assert.Null(note.Total);
    }
}
