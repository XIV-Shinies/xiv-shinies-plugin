using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The retainer source note is the one storage note with more than a state in it, so its rules live
// in a pure helper and are pinned here. Reading the two counts out of the game is verified in game.
public class RetainerNoteTests
{
    // Any remembered retainer means the source was read from the item finder's copy, and the note
    // says how many of how many: "2 of 3 scanned".
    [Fact]
    public void Remembered_retainers_make_the_source_cached_with_their_count_and_the_total()
    {
        var note = RetainerNote.Build(rememberedCount: 2, managerTotal: 3);

        Assert.Equal(SourceStates.Cached, note.State);
        Assert.Equal(2, note.Count);
        Assert.Equal(3, note.Total);
    }

    // Nothing remembered means nothing could be read. The total still travels, so a reader can tell
    // "has retainers, none scanned" from "has no retainers we know of".
    [Fact]
    public void No_remembered_retainers_make_the_source_unscanned_with_the_total_only()
    {
        var note = RetainerNote.Build(rememberedCount: 0, managerTotal: 3);

        Assert.Equal(SourceStates.Unscanned, note.State);
        Assert.Null(note.Count);
        Assert.Equal(3, note.Total);
    }

    // The game reports 0 retainers until it has filled in its retainer list, which cannot be told
    // apart from "has none" — so a zero total is unknown and is left off rather than sent as 0.
    [Fact]
    public void A_zero_total_is_unknown_and_left_off_a_cached_note()
    {
        var note = RetainerNote.Build(rememberedCount: 2, managerTotal: 0);

        Assert.Equal(SourceStates.Cached, note.State);
        Assert.Equal(2, note.Count);
        Assert.Null(note.Total);
    }

    [Fact]
    public void A_zero_total_is_unknown_and_left_off_an_unscanned_note()
    {
        var note = RetainerNote.Build(rememberedCount: 0, managerTotal: 0);

        Assert.Equal(SourceStates.Unscanned, note.State);
        Assert.Null(note.Count);
        Assert.Null(note.Total);
    }

    // The read gate and the note share this one rule, so a collector can never read the retainers
    // while reporting them unscanned, or report them cached without reading them.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    public void Any_remembered_retainer_counts_as_remembered(int rememberedCount, bool expected)
    {
        Assert.Equal(expected, RetainerNote.HasRemembered(rememberedCount));
    }
}
