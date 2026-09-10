using System;
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
}
