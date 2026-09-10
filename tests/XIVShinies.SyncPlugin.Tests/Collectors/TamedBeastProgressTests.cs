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

    // Part-way through, the count is the encouraging part: it says turning pages is working.
    [Fact]
    public void Part_way_through_it_shows_how_far_along_the_read_is()
    {
        var note = TamedBeastCollector.DescribeProgress(seen: 17, total: 50);

        Assert.Contains("17 of 50 read", note);
        Assert.Contains("page through", note);
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
