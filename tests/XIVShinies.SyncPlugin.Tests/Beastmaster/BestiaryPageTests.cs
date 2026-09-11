using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// Reads which beasts the Master's Bestiary says the character holds. The layout is positional, so
// most of these are really about one question: does a page that is not shaped the way this expects
// get refused, rather than read as beasts from the wrong slots?
public class BestiaryPageTests
{
    // Builds a page shaped like the real window: a tally at slot 7, then fixed-size records from
    // slot 24 — the bestiary number, three flags with the held flag second of them, an icon, the
    // caption, and two more slots this reader has no use for.
    private static List<AddonValue> Page(
        string? tally,
        params (int Number, bool Held)[] beasts)
    {
        var values = new List<AddonValue>();
        for (var i = 0; i < 24; i++)
            values.Add(i == 7 && tally is not null ? AddonValue.FromText(tally) : AddonValue.Unreadable);

        foreach (var (number, held) in beasts)
        {
            values.Add(AddonValue.FromInteger((uint)number));
            values.Add(AddonValue.FromBoolean(true));
            values.Add(AddonValue.FromBoolean(held));
            values.Add(AddonValue.FromBoolean(false));
            values.Add(AddonValue.FromInteger(held ? 242000u + (uint)number : 242051u));
            values.Add(AddonValue.FromText($"No. {number}"));
            values.Add(AddonValue.FromBoolean(false));
            values.Add(AddonValue.FromInteger(3));
        }

        return values;
    }

    [Fact]
    public void A_held_beast_is_read_from_its_record()
    {
        var page = BestiaryPage.Read(Page("2/50", (1, true), (2, false), (3, true)));

        Assert.Equal(new[] { 1, 3 }, page.Captured);
    }

    // Every number on the page is "seen" whether held or not, because seeing a beast you do not
    // hold is exactly what proves you do not hold it.
    [Fact]
    public void Every_listed_beast_is_seen_whether_held_or_not()
    {
        var page = BestiaryPage.Read(Page("2/50", (1, true), (2, false), (3, true)));

        Assert.Equal(new[] { 1, 2, 3 }, page.Seen);
    }

    [Fact]
    public void The_windows_own_tally_is_read()
    {
        var page = BestiaryPage.Read(Page("40/50", (1, true)));

        Assert.Equal(40, page.CapturedTotal);
        Assert.Equal(50, page.BeastTotal);
    }

    [Theory]
    [InlineData("40")]
    [InlineData("40/")]
    [InlineData("")]
    [InlineData("some/words")]
    [InlineData("60/50")]
    [InlineData("40/0")]
    [InlineData("-1/50")]
    public void A_tally_that_does_not_read_as_one_is_refused(string tally)
    {
        var page = BestiaryPage.Read(Page(tally, (1, true)));

        Assert.Null(page.CapturedTotal);
        Assert.Null(page.BeastTotal);
    }

    // A missing tally costs the completeness claim but not the beasts: what the page listed is
    // still true about the character.
    [Fact]
    public void A_page_with_no_tally_still_reports_its_beasts()
    {
        var page = BestiaryPage.Read(Page(tally: null, (7, true)));

        Assert.Equal(new[] { 7 }, page.Captured);
        Assert.Null(page.BeastTotal);
    }

    // The run of records ends where the window's other data begins, and that shows up as a slot
    // not holding what a record holds. Everything past that point belongs to something else.
    [Fact]
    public void Reading_stops_where_the_records_stop()
    {
        var values = Page("2/50", (1, true), (2, true));

        // The window's detail pane for the selected beast, which follows the list in the real
        // window and must not be read as more beasts.
        values.Add(AddonValue.Unreadable);
        values.Add(AddonValue.FromBoolean(true));
        values.Add(AddonValue.FromBoolean(true));
        values.Add(AddonValue.FromInteger(0));
        values.Add(AddonValue.FromBoolean(true));
        values.Add(AddonValue.FromText("No. 1"));
        values.Add(AddonValue.FromText("Cu Sith"));
        values.Add(AddonValue.FromInteger(242001));

        var page = BestiaryPage.Read(values);

        Assert.Equal(new[] { 1, 2 }, page.Seen);
    }

    // The caption names the beast's number, so it proves the record is aligned. A run of slots that
    // merely looks like a record will not also caption itself correctly — and a misaligned read is
    // the one failure that could permanently mark beasts the character does not hold.
    [Fact]
    public void A_record_whose_caption_names_a_different_beast_is_refused()
    {
        var values = Page("1/50", (1, true));
        values[24 + 5] = AddonValue.FromText("No. 9");

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    // The caption is compared by digits alone, so a client that words it differently still reads.
    [Theory]
    [InlineData("No. 7")]
    [InlineData("No.7")]
    [InlineData("Nr. 7")]
    [InlineData("7")]
    public void A_caption_is_matched_by_its_digits_rather_than_its_words(string caption)
    {
        var values = Page("1/50", (7, true));
        values[24 + 5] = AddonValue.FromText(caption);

        Assert.Equal(new[] { 7 }, BestiaryPage.Read(values).Seen);
    }

    [Fact]
    public void A_caption_with_no_digits_is_refused()
    {
        var values = Page("1/50", (7, true));
        values[24 + 5] = AddonValue.FromText("Cu Sith");

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    // The held flag has to be a flag. A slot holding something else means the layout moved, and
    // guessing which way it moved is exactly what this must not do.
    [Fact]
    public void A_record_whose_held_slot_is_not_a_flag_is_refused()
    {
        var values = Page("1/50", (1, true));
        values[24 + 2] = AddonValue.FromInteger(1);

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    // The caption slot holding something that is not text at all, as distinct from text that names
    // the wrong beast. Both mean the layout moved; this is the arm that catches it first.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_record_whose_caption_slot_is_not_text_is_refused(bool unreadable)
    {
        var values = Page("1/50", (1, true));
        values[24 + 5] = unreadable ? AddonValue.Unreadable : AddonValue.FromInteger(1);

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    // With no tally there is no bestiary-sized bound, so a generous ceiling stands in — its job is
    // to refuse an absurd number, not to encode how many beasts the game has.
    [Fact]
    public void An_absurd_number_is_refused_even_with_no_tally_to_bound_it()
    {
        var values = Page(tally: null, (5000, true));

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    [Fact]
    public void A_record_whose_number_slot_is_not_a_number_is_refused()
    {
        var values = Page("1/50", (1, true));
        values[24] = AddonValue.FromText("1");

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    // The tally bounds the numbers, so a record claiming a beast beyond the bestiary's own size is
    // a misread rather than a discovery.
    [Fact]
    public void A_number_beyond_the_bestiarys_own_size_is_refused()
    {
        var values = Page("1/50", (99, true));

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    [Fact]
    public void Beast_zero_is_refused()
    {
        var values = Page("1/50", (0, true));

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    [Fact]
    public void A_window_with_no_values_reads_as_nothing()
    {
        var page = BestiaryPage.Read(new List<AddonValue>());

        Assert.Empty(page.Seen);
        Assert.Empty(page.Captured);
        Assert.Null(page.BeastTotal);
    }

    // A record cut short by the end of the values is not a record. Reading it would take the
    // remaining slots from beyond the array the window actually handed over.
    [Fact]
    public void A_record_running_past_the_end_of_the_values_is_refused()
    {
        var values = Page("1/50", (1, true));
        values.RemoveAt(values.Count - 1);

        Assert.Empty(BestiaryPage.Read(values).Seen);
    }

    // The real window, as captured from the game: 25 records a page, and the ten the character does
    // not hold are the ones whose held flag is false.
    [Fact]
    public void A_full_page_shaped_like_the_real_window_reads_end_to_end()
    {
        var missing = new[] { 37, 38, 43, 44, 45, 46, 47, 48, 49, 50 };
        var beasts = Enumerable.Range(26, 25)
            .Select(number => (Number: number, Held: !missing.Contains(number)))
            .ToArray();

        var page = BestiaryPage.Read(Page("40/50", beasts));

        Assert.Equal(25, page.Seen.Count);
        Assert.Equal(15, page.Captured.Count);
        Assert.DoesNotContain(37, page.Captured);
        Assert.Contains(36, page.Captured);
        Assert.Equal(50, page.BeastTotal);
    }
}
