using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// The ledger is what turns pages of the bestiary window into something a collection pass can read.
// The window shows part of itself at a time; a sync happens later and needs the whole set.
public class TamedBeastLedgerTests
{
    private static BestiaryPageReading Page(int[] seen, int[] held) => new(seen, held, held.Length, 50);

    [Fact]
    public void A_held_beast_shows_up_in_the_snapshot()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));

        Assert.Equal(new[] { 30 }, ledger.Snapshot());
    }

    // A beast the page listed but did not mark as held is not the character's, and must not appear
    // in what ships — but it still counts as seen, which is what proves they do not hold it.
    [Fact]
    public void A_beast_listed_but_not_held_is_seen_rather_than_reported()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(Page(new[] { 30, 31 }, new[] { 30 }));

        Assert.Equal(new[] { 30 }, ledger.Snapshot());
        Assert.Equal(2, ledger.SeenCount);
    }

    // Ascending order is for the reader, not the server — the upload log and a pasted diagnostic
    // are easier to scan when the numbers are not in the order the pages happened to arrive.
    [Fact]
    public void The_snapshot_is_sorted_ascending()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));
        ledger.RecordPage(Page(new[] { 2, 11 }, new[] { 2, 11 }));

        Assert.Equal(new[] { 2, 11, 30 }, ledger.Snapshot());
    }

    // Pages overlap when a player scrolls back, and the same beast must not be counted twice.
    [Fact]
    public void Seeing_the_same_beast_twice_reports_it_once()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));
        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));

        Assert.Equal(new[] { 30 }, ledger.Snapshot());
        Assert.Equal(1, ledger.Count);
    }

    // Every pass resends everything gathered rather than only what is new, which is what makes a
    // failed upload self-healing: the next sync carries the beast the failed one dropped, and the
    // server's insert-only write makes the repeat free.
    [Fact]
    public void The_snapshot_keeps_reporting_earlier_beasts_so_a_failed_upload_retries_itself()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(Page(new[] { 2 }, new[] { 2 }));
        var first = ledger.Snapshot();
        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));

        Assert.Equal(new[] { 2 }, first);
        Assert.Equal(new[] { 2, 30 }, ledger.Snapshot());
    }

    // The snapshot is a copy, so a page read after it was taken cannot mutate a list the caller is
    // still reading.
    [Fact]
    public void A_snapshot_is_a_copy_rather_than_a_live_view()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordPage(Page(new[] { 2 }, new[] { 2 }));

        var snapshot = ledger.Snapshot();
        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));

        Assert.Equal(new[] { 2 }, snapshot);
    }

    // The ledger belongs to whoever is logged in, so both session edges empty it. A beast carried
    // across a character switch would attribute one character's collection to another.
    [Fact]
    public void Clearing_empties_the_ledger()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));

        ledger.Clear();

        Assert.Empty(ledger.Snapshot());
        Assert.Equal(0, ledger.Count);
        Assert.Equal(0, ledger.SeenCount);
    }

    // "Nothing read yet" is the ordinary state, and it is a fact rather than a failure — the
    // collector still reports the category as read.
    [Fact]
    public void A_fresh_ledger_is_empty_rather_than_absent()
    {
        Assert.Empty(new TamedBeastLedger().Snapshot());
    }

    // Zero is the sheets' blank padding row and fails the server's positive-integer validation for
    // the entire upload, so it never gets in. Reading should never produce one; this is the floor
    // that means no future caller can reintroduce it.
    [Fact]
    public void A_non_positive_number_is_refused_at_the_door()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(new BestiaryPageReading(new[] { 0, -1 }, new[] { 0, -1 }, 0, 50));

        Assert.Empty(ledger.Snapshot());
        Assert.Equal(0, ledger.SeenCount);
    }

    [Fact]
    public void A_page_with_nothing_on_it_records_nothing()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordPage(new BestiaryPageReading(Array.Empty<int>(), Array.Empty<int>(), null, null));

        Assert.Empty(ledger.Snapshot());
        Assert.Null(ledger.BeastTotal);
    }

    // --- Ranks -----------------------------------------------------------------------------------------

    /// <summary>A reading with these ranks, and the given beasts leveled (none by default).</summary>
    // `params int[] leveled` lets a caller list the leveled numbers one by one, like `...leveled`.
    private static BeastRankReading Reading(Dictionary<int, int> ranks, params int[] leveled) =>
        new(ranks, new HashSet<int>(leveled));

    [Fact]
    public void A_rank_reading_is_kept()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [2] = 6 }));

        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = 6 }, ledger.Ranks());
    }

    // A reading is the whole record set at one moment, so a later one replaces it rather than
    // adding to it.
    [Fact]
    public void A_later_rank_reading_replaces_the_earlier_one()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [2] = 6 }));
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 10 }));

        Assert.Equal(new Dictionary<int, int> { [1] = 10 }, ledger.Ranks());
    }

    // Only news is worth an upload: the record set is looked at once a second while the player
    // stands at the entrance, and it rarely changes.
    [Fact]
    public void Only_a_changed_rank_reading_counts_as_news()
    {
        var ledger = new TamedBeastLedger();

        Assert.True(ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9 })));
        Assert.False(ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9 })));
        Assert.True(ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 10 })));
        Assert.True(ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 10, [2] = 3 })));
    }

    [Fact]
    public void A_rank_past_the_end_of_the_bestiary_is_refused()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(50);

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [51] = 4 }));

        Assert.Equal(new Dictionary<int, int> { [1] = 9 }, ledger.Ranks());
    }

    // Ranks belong to whoever is logged in, like the held beasts.
    [Fact]
    public void Clearing_forgets_the_ranks_and_the_leveled_beasts()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9 }, 1));

        ledger.Clear();

        Assert.Empty(ledger.Ranks());
        Assert.Empty(ledger.Pacts());
    }

    // A leveled beast the window has not listed is held on the record set's word, so it is reported
    // beside the window's.
    [Fact]
    public void The_pacts_are_the_windows_beasts_and_the_leveled_ones_together()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordPage(Page(new[] { 2, 30 }, new[] { 2, 30 }));

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [2] = 3, [30] = 1 }, 1, 2));

        Assert.Equal(new[] { 1, 2, 30 }, ledger.Pacts());
    }

    // A beast at rank 1 with no EXP could be how the record set stores a beast without a pact, so
    // it is reported only once the window has shown it held.
    [Fact]
    public void A_beast_that_is_ranked_but_not_leveled_is_not_a_pact_on_its_own()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [4] = 1 }, 1));

        Assert.Equal(new[] { 1 }, ledger.Pacts());
    }

    // The completeness test counts the window's beasts against the window's own tally, so the
    // record set's beasts must not leak into what it counts.
    [Fact]
    public void Leveled_beasts_stay_out_of_the_windows_snapshot()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9 }, 1));

        Assert.Empty(ledger.Snapshot());
        Assert.Equal(0, ledger.Count);
    }

    // A beast newly raised past a fresh pact is news even when no rank changed number.
    [Fact]
    public void A_newly_leveled_beast_counts_as_news()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [4] = 1 }));

        Assert.True(ledger.RecordRanks(Reading(new Dictionary<int, int> { [4] = 1 }, 4)));
    }

    // A beast dropping out of the leveled set changes what is reported, so it is news too.
    [Fact]
    public void A_beast_leaving_the_leveled_set_counts_as_news()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [4] = 2 }, 4));

        Assert.True(ledger.RecordRanks(Reading(new Dictionary<int, int> { [4] = 2 })));
    }

    // The same number of ranks on different beasts is a different reading.
    [Fact]
    public void The_same_rank_on_a_different_beast_counts_as_news()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9 }));

        Assert.True(ledger.RecordRanks(Reading(new Dictionary<int, int> { [2] = 9 })));
    }

    // The window wins where the two disagree: a beast it listed without marking held is not
    // reported, whatever the record set says, because a reported pact is never taken back.
    [Fact]
    public void A_leveled_beast_the_window_listed_as_not_held_is_not_a_pact()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordPage(Page(new[] { 1, 2 }, new[] { 1 }));

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [2] = 4 }, 1, 2));

        Assert.Equal(new[] { 1 }, ledger.Pacts());
    }

    // The order of the two reads does not matter: a window read after the record set still wins.
    [Fact]
    public void A_window_read_after_the_record_set_still_wins()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [2] = 4 }, 1, 2));

        ledger.RecordPage(Page(new[] { 1, 2 }, new[] { 1 }));

        Assert.Equal(new[] { 1 }, ledger.Pacts());
    }

    // A refused look at the record set drops what an earlier reading said, so a reading the window
    // has since contradicted stops being reported.
    [Fact]
    public void Forgetting_the_ranks_drops_them_and_the_leveled_beasts_but_not_the_windows()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordPage(Page(new[] { 30 }, new[] { 30 }));
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9, [30] = 4 }, 1, 30));

        ledger.ForgetRanks();

        Assert.Empty(ledger.Ranks());
        Assert.Equal(new[] { 30 }, ledger.Pacts());
    }

    // Non-positive numbers in either half of a reading are not bestiary numbers.
    [Fact]
    public void A_reading_drops_non_positive_numbers()
    {
        var ledger = new TamedBeastLedger();

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [0] = 9, [-1] = 4, [1] = 3 }, 0, -1, 1));

        Assert.Equal(new Dictionary<int, int> { [1] = 3 }, ledger.Ranks());
        Assert.Equal(new[] { 1 }, ledger.Pacts());
    }

    [Fact]
    public void A_leveled_beast_past_the_end_of_the_bestiary_is_refused()
    {
        var ledger = new TamedBeastLedger();
        ledger.NoteDomainSize(50);

        ledger.RecordRanks(Reading(new Dictionary<int, int> { [51] = 4 }, 51));

        Assert.Empty(ledger.Pacts());
    }

    // A pass holds its own copy, so a later reading cannot change ranks it is already serializing.
    [Fact]
    public void The_ranks_are_a_copy_rather_than_a_live_view()
    {
        var ledger = new TamedBeastLedger();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 9 }));

        var ranks = ledger.Ranks();
        ledger.RecordRanks(Reading(new Dictionary<int, int> { [1] = 10 }));

        Assert.Equal(9, ranks[1]);
    }
}
