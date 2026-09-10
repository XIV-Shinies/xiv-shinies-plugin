using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// Declaring a collection complete is what lets the site say "you marked this by hand but you do not
// hold it". Getting it wrong contradicts the player about their own collection, so these are about
// when the ledger may make that claim — and, mostly, when it may not.
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

    // The filter trap, and the reason the game's own data has a vote. The bestiary's filter panel
    // narrows which beasts are listed; if the window's tally narrows with it, the window supplies
    // both the numerator and the denominator and a subset agrees with itself perfectly.
    [Fact]
    public void A_filtered_view_that_agrees_with_itself_is_not_complete()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        var listed = Enumerable.Range(1, 12).ToArray();
        var held = Enumerable.Range(1, 9).ToArray();
        ledger.RecordPage(PageOf(listed, held, capturedTotal: 9, beastTotal: 12));

        Assert.False(ledger.IsComplete);
    }

    // ...and a filter applied after a full read must not revoke a claim that is still true, which
    // is why the largest total ever seen wins rather than the latest.
    [Fact]
    public void A_filter_applied_after_a_full_read_does_not_revoke_the_claim()
    {
        var ledger = LedgerOverBothPages();

        ledger.RecordPage(PageOf(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, capturedTotal: 3, beastTotal: 3));

        Assert.True(ledger.IsComplete);
    }

    // The completeness check leans on every held beast being one of the seen ones — that is what
    // lets "as many seen as the bestiary has" mean the whole domain was covered. A page claiming a
    // beast it did not list would break that quietly, so the ledger refuses it rather than trusting
    // the caller to be consistent.
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

    // Every number seen and the right count of records found, but one held flag misread. The third
    // agreement is what catches it: the beasts recorded as held no longer match the window's own
    // count of them.
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

    // One page is a fragment, and a fragment presented as the whole would have the site tell the
    // player they do not own beasts that are simply on the page they did not look at.
    [Fact]
    public void One_page_of_the_bestiary_is_not_complete()
    {
        var ledger = new TamedBeastLedger();
        var seen = Enumerable.Range(1, 25).ToArray();

        ledger.RecordPage(PageOf(seen, seen, 40, 50));

        Assert.False(ledger.IsComplete);
    }

    // The agreements check each other. Seeing every number while holding a different count than
    // the window reports means something was misread, and the claim is withheld.
    [Fact]
    public void Seeing_every_number_is_not_enough_when_the_held_count_disagrees()
    {
        var ledger = new TamedBeastLedger();
        var seen = Enumerable.Range(1, 50).ToArray();

        ledger.RecordPage(PageOf(seen, new[] { 1, 2, 3 }, 40, 50));

        Assert.Equal(50, ledger.SeenCount);
        Assert.False(ledger.IsComplete);
    }

    // ...and the same in the other direction: holding the right number of beasts while having seen
    // only part of the bestiary proves nothing about the part left unseen.
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

    // One page of held beasts is a real fact, and a lonely one: it says nothing about the beasts on
    // the pages nobody looked at.
    [Fact]
    public void One_page_of_held_beasts_never_makes_the_ledger_complete()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(RealBestiarySize);

        ledger.RecordPage(PageOf(new[] { 30, 31 }, new[] { 30, 31 }, capturedTotal: 2, beastTotal: 50));

        Assert.False(ledger.IsComplete);
        Assert.Equal(new[] { 30, 31 }, ledger.Snapshot());
    }

    // Pages overlap when a player scrolls back, and the same beast must not be counted twice —
    // double counting would make the held total overshoot and silently withhold completeness.
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

    // Taming a beast and looking at the bestiary again is the ordinary way a beastmaster's
    // collection grows. The new beast is recorded and the claim survives, because the window's own
    // tally moved with it.
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

    // Session edges empty the character's data, the window's tally included — a leftover
    // denominator would judge the next character's bestiary. The bestiary's size is game data
    // rather than character data, so that survives.
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

    // Recording a page answers whether it brought news, so the caller acts on the answer rather
    // than diffing a count around the write.
    [Fact]
    public void Recording_a_page_reports_whether_it_held_anything_new()
    {
        var ledger = new TamedBeastLedger();

        Assert.True(ledger.RecordPage(PageOf(new[] { 1, 2 }, new[] { 1 }, 1, 50)));
        Assert.False(ledger.RecordPage(PageOf(new[] { 1, 2 }, new[] { 1 }, 1, 50)));
        Assert.True(ledger.RecordPage(PageOf(new[] { 1, 2 }, new[] { 1, 2 }, 2, 50)));
    }

    // A page listing beasts the player holds none of is still worth recording — it is what proves
    // they hold none of them — but it is not news that should prompt an upload.
    [Fact]
    public void A_page_of_beasts_none_of_which_are_held_is_not_news()
    {
        var ledger = new TamedBeastLedger();

        Assert.False(ledger.RecordPage(PageOf(new[] { 1, 2, 3 }, System.Array.Empty<int>(), 0, 50)));
        Assert.Equal(3, ledger.SeenCount);
    }

    // The emptiness floor the other collections have: an empty list never carries the claim,
    // whatever the ledger thinks, because "owns nothing" and "read nothing" look identical.
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
