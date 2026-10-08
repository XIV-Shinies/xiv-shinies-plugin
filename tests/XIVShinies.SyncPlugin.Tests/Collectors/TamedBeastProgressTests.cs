using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The line the read-status panel shows while the bestiary is only part-read. It is the only thing
// telling a player how to finish, so it has to name the action every time — and never name work
// that does not exist.
public class TamedBeastProgressTests
{
    // Drawn after the category's display name, so each phrase has to complete
    // "Tamed beasts: …" and end as a sentence does.
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

    // Before anything has been read there is no count worth showing — "0 of 50" would read as a
    // collection that lost everything rather than one not yet looked at.
    [Fact]
    public void Nothing_read_yet_asks_for_the_bestiary_without_a_count()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 0, total: 50);

        Assert.DoesNotContain("0 of", note);
        Assert.Contains("not read yet", note);
    }

    // A count with no total behind it cannot be shown either.
    [Fact]
    public void A_count_with_no_total_asks_for_the_bestiary_without_one()
    {
        Assert.Contains("not read yet", TamedBeastCollector.DescribeProgress(seen: 9, total: null));
    }

    // Nothing read names both ways to record the beasts: the NPC, which a player meets anyway on
    // the way into a run, and the bestiary.
    [Fact]
    public void Nothing_read_yet_names_the_crucible_npc_too()
    {
        Assert.Contains("Crucible", TamedBeastCollector.DescribeProgress(seen: 0, total: null));
    }

    // Beasts recorded at the NPC are on their way already, so "not read yet" would be false. The
    // bestiary is what confirms the set.
    [Fact]
    public void Beasts_recorded_at_the_npc_are_counted_rather_than_called_unread()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 0, total: null, recordedAtCrucible: 50);

        Assert.DoesNotContain("not read yet", note);
        Assert.Contains("50 recorded", note);
        Assert.Contains("Master's Bestiary", note);
    }

    // Once the window has listed anything, the Crucible's count is left out of the note, even when
    // the window's tally could not be read.
    [Fact]
    public void Once_the_window_has_listed_beasts_the_note_reports_the_window()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 9, total: null, recordedAtCrucible: 5);

        Assert.DoesNotContain("Crucible —", note);
        Assert.DoesNotContain("recorded at the Crucible", note);
    }

    // The NPC was talked to but the record set added no beast, so the bestiary is the one step left;
    // naming the NPC again would send the player in a circle. That holds whether the window has
    // listed nothing or listed beasts whose tally could not be read.
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

    // The Crucible's line reads as a sentence completing "Tamed beasts: …" like every other.
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

    // The bestiary remembers the last filter the player set, so a part-way read is as likely to be
    // a filter left on as a page not yet turned. Paging through a filtered window records only what
    // the filter shows and never finishes the read, so the advice has to name the filter or it
    // sends the player in a circle.
    [Fact]
    public void Part_way_through_it_says_to_clear_the_filter()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 17, total: 50);

        Assert.Contains("filter", note);
    }

    // Every number seen, yet still unsettled. There is no "rest" left to page through, so asking
    // for it would send the player after work that does not exist.
    [Fact]
    public void A_fully_seen_bestiary_does_not_ask_for_the_rest()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 50, total: 50);

        Assert.Contains("50 of 50 read", note);
        Assert.DoesNotContain("the rest", note);
    }
}
