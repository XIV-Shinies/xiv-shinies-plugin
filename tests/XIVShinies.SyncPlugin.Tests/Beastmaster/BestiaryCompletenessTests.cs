using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// A completeness claim lets the site say "you marked this by hand but you do not hold it". A wrong
// claim contradicts the player about their own collection, so these pin when the ledger may make
// it, and mostly when it may not.
public class BestiaryCompletenessTests
{
    private static BestiaryPageReading PageOf(int[] seen, int[] held, int? capturedTotal, int? beastTotal) =>
        new(seen, held, capturedTotal, beastTotal);

    /// <summary>The bestiary's real size, as the game's own data sheet reports it.</summary>
    private const int RealBestiarySize = 50;

    private static TamedBeastLedger LedgerOverBothPages()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        var firstSeen = Enumerable.Range(1, 25).ToArray();
        ledger.RecordPage(PageOf(firstSeen, firstSeen, 40, 50));

        var secondSeen = Enumerable.Range(26, 25).ToArray();
        var secondHeld = secondSeen.Where(n => n <= 36 || n is 39 or 40 or 41 or 42).ToArray();
        ledger.RecordPage(PageOf(secondSeen, secondHeld, 40, 50));

        return ledger;
    }

    // Only the game's own bestiary size can refuse a short window total: every other figure in the
    // check is the window's own, so a window naming twelve as the whole agrees with itself. A
    // filtered view names the real size, but a still-loading list comes back short, and the claim
    // cannot be taken back.
    [Fact]
    public void A_window_total_that_disagrees_with_the_games_own_size_is_not_complete()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        var listed = Enumerable.Range(1, 12).ToArray();
        var held = Enumerable.Range(1, 9).ToArray();
        ledger.RecordPage(PageOf(listed, held, capturedTotal: 9, beastTotal: 12));

        Assert.False(ledger.IsComplete);
    }

    // The largest size ever seen wins, not the latest, so a still-loading list cannot revoke an
    // earned claim.
    [Fact]
    public void A_window_total_that_comes_back_short_does_not_revoke_the_claim()
    {
        var ledger = LedgerOverBothPages();

        ledger.RecordPage(PageOf(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, capturedTotal: 3, beastTotal: 3));

        Assert.True(ledger.IsComplete);
    }

    // A filter narrows the tally's held count but leaves its size alone, so an unchanged size is no
    // evidence the view is unfiltered. The largest held count ever seen wins: pacts are never broken,
    // so within a session a smaller count can only be a narrower view.
    [Fact]
    public void A_filter_that_narrows_only_the_held_count_does_not_revoke_the_claim()
    {
        var ledger = LedgerOverBothPages();

        var listed = new[] { 4, 9, 17, 22, 31 };
        ledger.RecordPage(PageOf(listed, listed, capturedTotal: 5, beastTotal: RealBestiarySize));

        Assert.True(ledger.IsComplete);
    }

    // In play the tally can fail to read while the records beside it read fine, so a refresh mid-load
    // lists beasts with no counts. An unreadable figure is no evidence against a finished read.
    [Fact]
    public void A_page_with_no_readable_tally_does_not_revoke_the_claim()
    {
        var ledger = LedgerOverBothPages();

        var listed = Enumerable.Range(1, 25).ToArray();
        ledger.RecordPage(PageOf(listed, listed, capturedTotal: null, beastTotal: null));

        Assert.True(ledger.IsComplete);
    }

    // The held count only rises within a session, so a leftover from a character holding more
    // beasts would withhold the next character's claim until they log out.
    [Fact]
    public void Clearing_forgets_the_held_count_so_the_next_character_can_be_complete()
    {
        var ledger = LedgerOverBothPages();
        Assert.True(ledger.IsComplete);

        ledger.Clear();

        // A second character who holds three beasts, reading the same fifty-beast bestiary.
        var everyNumber = Enumerable.Range(1, RealBestiarySize).ToArray();
        var held = new[] { 7, 12, 30 };
        ledger.RecordPage(PageOf(everyNumber, held, capturedTotal: 3, beastTotal: RealBestiarySize));

        Assert.True(ledger.IsComplete);
        Assert.Equal(3, ledger.Count);
    }

    // The held-count floor sits below zero so a real "0 of 50" is recorded; at zero it would read as
    // a tally never seen, and a player who tamed nothing could never settle. CollectResult.TamedBeasts,
    // not this class, keeps the empty claim off the wire.
    [Fact]
    public void A_bestiary_read_with_nothing_held_is_still_complete()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        var everyNumber = Enumerable.Range(1, RealBestiarySize).ToArray();
        ledger.RecordPage(PageOf(everyNumber, [], capturedTotal: 0, beastTotal: RealBestiarySize));

        Assert.True(ledger.IsComplete);
        Assert.Equal(0, ledger.Count);
    }

    // The completeness test reads "as many seen as the bestiary has" as "the whole bestiary was
    // covered", which holds only while every remembered number is real. One bogus number standing in
    // for a missing real one would make the count add up over the wrong set.
    [Fact]
    public void A_number_past_the_end_of_the_bestiary_is_not_counted_as_seen()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        // Forty-nine real numbers, plus one that cannot exist standing in for the fiftieth.
        var listed = Enumerable.Range(1, 49).Append(RealBestiarySize + 7).ToArray();
        ledger.RecordPage(PageOf(listed, listed, capturedTotal: 50, beastTotal: RealBestiarySize));

        Assert.Equal(49, ledger.SeenCount);
        Assert.False(ledger.IsComplete);
    }

    // The completeness check relies on every held beast being a seen one. A page claiming a beast it
    // did not list would break that quietly, so the ledger refuses it rather than trust the caller.
    [Fact]
    public void A_held_beast_the_page_never_listed_is_refused()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        var seen = Enumerable.Range(1, 50).ToArray();
        var held = Enumerable.Range(1, 39).Append(51).ToArray();
        ledger.RecordPage(PageOf(seen, held, capturedTotal: 40, beastTotal: 50));

        Assert.DoesNotContain(51, ledger.Snapshot());
        Assert.Equal(39, ledger.Count);
        Assert.False(ledger.IsComplete);
    }

    // Without the game's own size there is nothing to check the window against, so the claim waits.
    [Fact]
    public void A_ledger_that_was_never_told_the_bestiarys_real_size_is_never_complete()
    {
        var ledger = new TamedBeastLedger();

        var seen = Enumerable.Range(1, 50).ToArray();
        ledger.RecordPage(PageOf(seen, seen, 50, 50));

        Assert.False(ledger.IsComplete);
    }

    // Every number seen, but one held flag misread. The third agreement catches it: the beasts
    // recorded as held no longer match the window's own count.
    [Fact]
    public void A_page_whose_held_flags_were_misread_withholds_the_claim()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        var seen = Enumerable.Range(1, 50).ToArray();
        var held = seen.Where(n => n != 23).ToArray();
        ledger.RecordPage(PageOf(seen, held, capturedTotal: 50, beastTotal: 50));

        Assert.Equal(50, ledger.SeenCount);
        Assert.Equal(49, ledger.Count);
        Assert.False(ledger.IsComplete);
    }

    [Fact]
    public void A_bestiary_read_end_to_end_is_complete()
    {
        var ledger = LedgerOverBothPages();

        Assert.Equal(50, ledger.SeenCount);
        Assert.Equal(40, ledger.Count);
        Assert.True(ledger.IsComplete);
    }

    // Presented as the whole, one page would have the site tell the player they lack beasts that are
    // on the page they did not look at.
    [Fact]
    public void One_page_of_the_bestiary_is_not_complete()
    {
        var ledger = new TamedBeastLedger();
        var seen = Enumerable.Range(1, 25).ToArray();

        ledger.RecordPage(PageOf(seen, seen, 40, 50));

        Assert.False(ledger.IsComplete);
    }

    // Holding a different count than the window reports means something was misread.
    [Fact]
    public void Seeing_every_number_is_not_enough_when_the_held_count_disagrees()
    {
        var ledger = new TamedBeastLedger();
        var seen = Enumerable.Range(1, 50).ToArray();

        ledger.RecordPage(PageOf(seen, new[] { 1, 2, 3 }, 40, 50));

        Assert.Equal(50, ledger.SeenCount);
        Assert.False(ledger.IsComplete);
    }

    // The right held count proves nothing about the part of the bestiary left unseen.
    [Fact]
    public void Holding_the_reported_count_is_not_enough_without_seeing_every_number()
    {
        var ledger = new TamedBeastLedger();
        var seen = Enumerable.Range(1, 40).ToArray();

        ledger.RecordPage(PageOf(seen, seen, 40, 50));

        Assert.Equal(40, ledger.Count);
        Assert.False(ledger.IsComplete);
    }

    // Without the window's tally there is no denominator, so nothing can say the read is finished.
    [Fact]
    public void A_ledger_that_never_read_the_tally_is_never_complete()
    {
        var ledger = new TamedBeastLedger();
        var seen = Enumerable.Range(1, 50).ToArray();

        ledger.RecordPage(PageOf(seen, seen, null, null));

        Assert.False(ledger.IsComplete);
    }

    // One page of held beasts is still a real fact, but says nothing about the pages nobody looked
    // at.
    [Fact]
    public void One_page_of_held_beasts_never_makes_the_ledger_complete()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        ledger.RecordPage(PageOf(new[] { 30, 31 }, new[] { 30, 31 }, capturedTotal: 2, beastTotal: 50));

        Assert.False(ledger.IsComplete);
        Assert.Equal(new[] { 30, 31 }, ledger.Snapshot());
    }

    // Pages overlap when a player scrolls back. Counting a beast twice would overshoot the held total
    // and silently withhold completeness.
    [Fact]
    public void Reading_the_same_page_twice_changes_nothing()
    {
        var ledger = LedgerOverBothPages();
        var before = ledger.Snapshot();

        var firstSeen = Enumerable.Range(1, 25).ToArray();
        ledger.RecordPage(PageOf(firstSeen, firstSeen, 40, 50));

        Assert.Equal(before, ledger.Snapshot());
        Assert.True(ledger.IsComplete);
    }

    // Taming a beast is the ordinary way the collection grows. The claim survives because the
    // window's tally moved with the new beast.
    [Fact]
    public void A_beast_tamed_after_a_complete_read_is_picked_up_on_the_next_look()
    {
        var ledger = LedgerOverBothPages();

        var secondSeen = Enumerable.Range(26, 25).ToArray();
        var secondHeld = secondSeen.Where(n => n <= 36 || n is 37 or 39 or 40 or 41 or 42).ToArray();
        ledger.RecordPage(PageOf(secondSeen, secondHeld, capturedTotal: 41, beastTotal: 50));

        Assert.Equal(41, ledger.Count);
        Assert.Contains(37, ledger.Snapshot());
        Assert.True(ledger.IsComplete);
    }

    // A leftover tally would judge the next character's bestiary. The bestiary's size is game data,
    // not character data, so it survives.
    [Fact]
    public void Clearing_forgets_the_tally_as_well_as_the_beasts()
    {
        var ledger = LedgerOverBothPages();

        ledger.Clear();

        Assert.False(ledger.IsComplete);
        Assert.Equal(0, ledger.SeenCount);
        Assert.Null(ledger.BeastTotal);
        Assert.Empty(ledger.Snapshot());
    }

    // The caller acts on this answer rather than diffing a count around the write.
    [Fact]
    public void Recording_a_page_reports_whether_it_held_anything_new()
    {
        var ledger = new TamedBeastLedger();

        Assert.True(ledger.RecordPage(PageOf(new[] { 1, 2 }, new[] { 1 }, 1, 50)));
        Assert.False(ledger.RecordPage(PageOf(new[] { 1, 2 }, new[] { 1 }, 1, 50)));
        Assert.True(ledger.RecordPage(PageOf(new[] { 1, 2 }, new[] { 1, 2 }, 2, 50)));
    }

    // A page of beasts the player holds none of is still recorded, since it proves they hold none,
    // but it should not prompt an upload.
    [Fact]
    public void A_page_of_beasts_none_of_which_are_held_is_not_news()
    {
        var ledger = new TamedBeastLedger();

        Assert.False(ledger.RecordPage(PageOf(new[] { 1, 2, 3 }, System.Array.Empty<int>(), 0, 50)));
        Assert.Equal(3, ledger.SeenCount);
    }

    // "Owns nothing" and "read nothing" look the same, so an empty list never carries the claim,
    // whatever the ledger thinks.
    [Fact]
    public void An_empty_list_never_ships_a_completeness_claim()
    {
        var result = XIVShinies.SyncPlugin.Collectors.CollectResult.TamedBeasts(
            Array.Empty<int>(), completeEnumeration: true);

        Assert.False(result.CompleteEnumeration);
    }

    [Fact]
    public void A_complete_read_ships_the_claim()
    {
        var result = XIVShinies.SyncPlugin.Collectors.CollectResult.TamedBeasts(
            new[] { 1, 2, 3 }, completeEnumeration: true);

        Assert.True(result.CompleteEnumeration);
    }

    [Fact]
    public void An_incomplete_read_ships_no_claim()
    {
        var result = XIVShinies.SyncPlugin.Collectors.CollectResult.TamedBeasts(
            new[] { 1, 2, 3 }, completeEnumeration: false);

        Assert.False(result.CompleteEnumeration);
    }
}
