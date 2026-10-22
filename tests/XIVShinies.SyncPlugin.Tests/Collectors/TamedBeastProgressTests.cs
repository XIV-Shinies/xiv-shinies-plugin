using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The read-status line while the bestiary is part-read. It is the only thing telling a player how to
// finish, so it must always name the action and never name work that does not exist.
public class TamedBeastProgressTests
{
    // Each phrase must complete "Tamed beasts: …" as a sentence.
    [Theory]
    [InlineData(0, null)]
    [InlineData(0, 50)]
    [InlineData(17, 50)]
    [InlineData(50, 50)]
    public void Every_phrase_names_the_bestiary_and_reads_as_a_sentence(int seen, int? total)
    {
        var note = TamedBeastCollector.DescribeProgress(seen, total);

        Assert.Contains("Master's Bestiary", note);
        Assert.EndsWith(".", note);
        Assert.False(char.IsUpper(note[0]));
    }

    // "0 of 50" would read as a collection that lost everything, not one not yet looked at.
    [Fact]
    public void Nothing_read_yet_asks_for_the_bestiary_without_a_count()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 0, total: 50);

        Assert.DoesNotContain("0 of", note);
        Assert.Contains("not read yet", note);
    }

    [Fact]
    public void A_count_with_no_total_asks_for_the_bestiary_without_one()
    {
        Assert.Contains("not read yet", TamedBeastCollector.DescribeProgress(seen: 9, total: null));
    }

    // Both ways to record the beasts: the NPC, met anyway on the way into a run, and the bestiary.
    [Fact]
    public void Nothing_read_yet_names_the_crucible_npc_too()
    {
        Assert.Contains("Crucible", TamedBeastCollector.DescribeProgress(seen: 0, total: null));
    }

    // Beasts recorded at the NPC are already on their way, so "not read yet" would be false. The
    // bestiary confirms the set.
    [Fact]
    public void Beasts_recorded_at_the_npc_are_counted_rather_than_called_unread()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 0, total: null, recordedAtCrucible: 50);

        Assert.DoesNotContain("not read yet", note);
        Assert.Contains("50 recorded", note);
        Assert.Contains("Master's Bestiary", note);
    }

    // Once the window has listed a beast, the Crucible's count is left out, even when the window's
    // tally could not be read.
    [Fact]
    public void Once_the_window_has_listed_beasts_the_note_reports_the_window()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 9, total: null, recordedAtCrucible: 5);

        Assert.DoesNotContain("Crucible —", note);
        Assert.DoesNotContain("recorded at the Crucible", note);
    }

    // The NPC was talked to but added no beast, so naming it again would send the player in a
    // circle. That holds whether the window listed nothing or beasts with an unreadable tally.
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Ranks_read_with_no_beast_added_asks_only_for_the_bestiary(int seen)
    {
        var note = TamedBeastCollector.DescribeProgress(seen: seen, total: null, ranksRead: true);

        Assert.Contains("Master's Bestiary", note);
        Assert.DoesNotContain("talk to the NPC", note);
        Assert.DoesNotContain("not read yet", note);
    }

    [Fact]
    public void The_crucible_line_reads_as_a_sentence()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 0, total: 50, recordedAtCrucible: 12);

        Assert.Contains("Master's Bestiary", note);
        Assert.EndsWith(".", note);
        Assert.False(char.IsUpper(note[0]));
    }

    // Part-way through, the count is the encouraging part: it says turning pages is working.
    [Fact]
    public void Part_way_through_it_shows_how_far_along_the_read_is()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 17, total: 50);

        Assert.Contains("17 of 50 read", note);
        Assert.Contains("page through", note);
    }

    // The bestiary remembers its last filter, and paging through a filtered window never finishes
    // the read. Advice that skips the filter sends the player in a circle.
    [Fact]
    public void Part_way_through_it_says_to_clear_the_filter()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 17, total: 50);

        Assert.Contains("filter", note);
    }

    // Every number seen but still unsettled: there is no rest left to page through.
    [Fact]
    public void A_fully_seen_bestiary_does_not_ask_for_the_rest()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 50, total: 50);

        Assert.Contains("50 of 50 read", note);
        Assert.DoesNotContain("the rest", note);
    }
}
