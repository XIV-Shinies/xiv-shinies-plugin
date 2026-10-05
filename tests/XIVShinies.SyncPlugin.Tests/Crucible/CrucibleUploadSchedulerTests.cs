using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// When the Crucible snapshots go up, in which uploads, and under which trigger word. The scheduler
// is handed the current moment, so every test sets the time explicitly. Each `[Fact]` is one test,
// like `it(...)` in Jest; a `[Theory]` runs once per `[InlineData]` row, like `it.each`.
public class CrucibleUploadSchedulerTests
{
    /// <summary>A board's territory.</summary>
    // `uint` is a whole number that can never be negative (an unsigned integer), the type of a
    // territory id; `const` fixes the value when the code compiles.
    private const uint Board = 1339;

    /// <summary>A second board's territory.</summary>
    private const uint OtherBoard = 1340;

    /// <summary>The entrance's territory.</summary>
    private const uint Entrance = 148;

    // `static readonly` is a value built once and never reassigned; a `DateTimeOffset` is a moment
    // with its offset from UTC.
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A moment this many seconds after <see cref="T0"/>.</summary>
    // `=>` makes the expression after it the whole method body, like an arrow function.
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>A bag snapshot holding this many tokens, read at this moment.</summary>
    private static CrucibleObservation Bag(int tokens, double seconds) =>
        CrucibleUploadBuilder.Bag(new CrucibleBagReading(tokens, [], []), At(seconds));

    /// <summary>A team snapshot in the lineup mode, open or closing.</summary>
    private static CrucibleObservation Team(bool closed, double seconds, int hp = 500) =>
        CrucibleUploadBuilder.Team(
            new CrucibleTeamReading(
                CrucibleTeamMode.Lineup, null, [new CrucibleFamiliar(10, 0, hp, 667, 5, false, [], false)]),
            At(seconds),
            closed);

    /// <summary>The kinds in an upload, in order, as their wire names.</summary>
    // `Select` maps each observation through `KindOf`, like `.map(kindOf)`; `ToArray` collects the
    // result into an array.
    private static string[] Kinds(CrucibleUpload upload) =>
        upload.Observations.Select(CrucibleUploadScheduler.KindOf).ToArray();

    /// <summary>Enters the board fresh and settles the enter, as a run's first moments do.</summary>
    private static CrucibleUploadScheduler EnteredAndSettled()
    {
        var scheduler = new CrucibleUploadScheduler();
        // `freshEntry: true` names the parameter it fills, so the call says what the true means.
        scheduler.NotifyEntered(Board, At(0), freshEntry: true);
        scheduler.MarkAccepted(scheduler.Poll(At(2))!, At(2));
        return scheduler;
    }

    // --- Changes ------------------------------------------------------------------------------

    [Fact]
    public void Nothing_queued_outside_a_board_means_nothing_to_send()
    {
        Assert.Null(new CrucibleUploadScheduler().Poll(At(100)));
    }

    // A snapshot waits out the debounce, so the rest of a burst of window refreshes can join it.
    [Fact]
    public void A_snapshot_goes_up_as_a_change_once_the_debounce_has_passed()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));

        Assert.Null(scheduler.Poll(At(1)));

        var upload = scheduler.Poll(At(2))!;
        Assert.Equal(CrucibleTrigger.Change, upload.Trigger);
        Assert.Equal(Board, upload.TerritoryTypeId);
        Assert.Equal(["bag"], Kinds(upload));
    }

    // The wait slides: each new snapshot pushes the deadline out, so a burst is one upload.
    [Fact]
    public void A_burst_of_snapshots_goes_up_as_one_upload()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.Queue(Team(false, 1.5), Board, At(1.5));

        Assert.Null(scheduler.Poll(At(2)));
        Assert.Equal(["bag", "team"], Kinds(scheduler.Poll(At(3.5))!));
    }

    // A window that keeps changing still goes up: the wait is capped at ten seconds from the first
    // snapshot that started waiting, even though each change pushes the debounce out.
    [Fact]
    public void Snapshots_that_keep_changing_still_go_up_within_ten_seconds()
    {
        var scheduler = new CrucibleUploadScheduler();
        // `(int)` converts the fractional number to a whole one, dropping the fraction.
        for (var second = 0.0; second < 10; second += 1.5)
            scheduler.Queue(Bag((int)(second * 10), second), Board, At(second));

        Assert.Null(scheduler.Poll(At(9.9)));
        Assert.Equal(["bag"], Kinds(scheduler.Poll(At(10))!));
    }

    // The longest wait counts from the snapshot that has waited longest, even when a replacement
    // has moved it behind one that started waiting later.
    [Fact]
    public void The_longest_wait_counts_from_the_snapshot_waiting_longest()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(0, 0), Board, At(0));
        scheduler.Queue(Team(false, 8), Board, At(8));
        scheduler.Queue(Bag(90, 9), Board, At(9));

        Assert.Null(scheduler.Poll(At(9.9)));
        Assert.Equal(["team", "bag"], Kinds(scheduler.Poll(At(10))!));
    }

    // Change detection ignores the moment: the same content read later is not a change.
    [Fact]
    public void A_snapshot_the_same_as_the_last_of_its_kind_is_not_sent_again()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.MarkAccepted(scheduler.Poll(At(2))!, At(2));

        scheduler.Queue(Bag(100, 5), Board, At(5));

        Assert.Null(scheduler.Poll(At(10)));
    }

    // A window's closing snapshot goes up whether or not its content changed.
    [Fact]
    public void A_closed_snapshot_goes_up_even_when_nothing_else_changed()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(true, 0), Board, At(0));
        scheduler.MarkAccepted(scheduler.Poll(At(2))!, At(2));

        scheduler.Queue(Team(true, 5), Board, At(5));

        Assert.Equal(["team"], Kinds(scheduler.Poll(At(7))!));
    }

    // A newer snapshot of a kind replaces an unsent one: snapshots are facts at a time, and the
    // latest is the one that matters. `is` with a type checks the record's kind and its values, like
    // a TypeScript type guard.
    [Fact]
    public void A_newer_snapshot_replaces_an_unsent_one_of_the_same_kind()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.Queue(Bag(120, 1), Board, At(1));

        var upload = scheduler.Poll(At(3))!;

        Assert.True(upload.Observations.Single() is CrucibleBagObservation { Tokens: 120 });
    }

    // The replacement takes the newest place in its run, so the moments in an upload never decrease.
    [Fact]
    public void A_replacement_keeps_the_upload_in_time_order()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.Queue(Team(false, 1), Board, At(1));
        scheduler.Queue(Bag(120, 3), Board, At(3));

        var upload = scheduler.Poll(At(5))!;

        Assert.Equal(["team", "bag"], Kinds(upload));
        Assert.Equal(
            ["2026-09-28T12:00:01Z", "2026-09-28T12:00:03Z"],
            upload.Observations.Select(observation => observation.ObservedAtUtc));
    }

    // A window's closing snapshot replaces an unsent open one: it is the same window, finished.
    [Fact]
    public void A_closing_snapshot_replaces_an_unsent_open_one()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(false, 0), Board, At(0));
        scheduler.Queue(Team(true, 1, hp: 400), Board, At(1));

        var upload = scheduler.Poll(At(3))!;

        Assert.True(upload.Observations.Single() is CrucibleTeamObservation { Closed: true });
    }

    // A window's closing snapshot and a fresh snapshot of the same kind go in two uploads, the
    // closing one first; the fresh one never replaces it.
    [Fact]
    public void A_fresh_snapshot_after_a_closing_one_goes_in_the_next_upload()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(true, 0), Board, At(0));
        scheduler.Queue(Team(false, 1, hp: 400), Board, At(1));

        var first = scheduler.Poll(At(3))!;
        Assert.True(first.Observations.Single() is CrucibleTeamObservation { Closed: true });
        scheduler.MarkAccepted(first, At(3));

        var second = scheduler.Poll(At(3))!;
        Assert.True(second.Observations.Single() is CrucibleTeamObservation { Closed: false });
    }

    // Snapshots read in different territories go in separate uploads, each with its own territory.
    [Fact]
    public void Snapshots_from_different_territories_go_in_separate_uploads()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(false, 0), Entrance, At(0));
        scheduler.Queue(Bag(100, 1), Board, At(1));

        var first = scheduler.Poll(At(3))!;
        Assert.Equal(Entrance, first.TerritoryTypeId);
        Assert.Equal(["team"], Kinds(first));
        scheduler.MarkAccepted(first, At(3));

        var second = scheduler.Poll(At(3))!;
        Assert.Equal(Board, second.TerritoryTypeId);
        Assert.Equal(["bag"], Kinds(second));
    }

    // An upload ends where its moments would go backwards, so the server never sees them decrease.
    [Fact]
    public void An_upload_ends_where_its_moments_would_go_backwards()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 5), Board, At(5));
        scheduler.Queue(Team(true, 3), Board, At(5));

        var first = scheduler.Poll(At(7))!;
        Assert.Equal(["bag"], Kinds(first));
        scheduler.MarkAccepted(first, At(7));

        Assert.Equal(["team"], Kinds(scheduler.Poll(At(7))!));
    }

    // One upload at a time: nothing more is handed out until the one in flight is settled.
    [Fact]
    public void Nothing_else_is_handed_out_while_an_upload_is_in_flight()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(true, 0), Board, At(0));
        scheduler.Queue(Team(false, 1, hp: 400), Board, At(1));

        Assert.NotNull(scheduler.Poll(At(3)));
        Assert.Null(scheduler.Poll(At(4)));
    }

    // --- Entering and leaving -----------------------------------------------------------------

    // Zoning into a board sends a baseline under the enter trigger, and the baseline goes up even
    // when its content matches what was sent before.
    [Fact]
    public void Entering_a_board_sends_the_baseline_as_an_enter()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.MarkAccepted(scheduler.Poll(At(2))!, At(2));

        scheduler.NotifyEntered(Board, At(10), freshEntry: true);
        scheduler.Queue(Bag(100, 10), Board, At(10));

        var upload = scheduler.Poll(At(12))!;
        Assert.Equal(CrucibleTrigger.Enter, upload.Trigger);
        Assert.Equal(["bag"], Kinds(upload));
    }

    // Starting or reloading the plugin inside a board sends the same baseline, as a change.
    [Fact]
    public void Starting_inside_a_board_sends_the_baseline_as_a_change()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: false);
        scheduler.Queue(Bag(100, 0), Board, At(0));

        Assert.Equal(CrucibleTrigger.Change, scheduler.Poll(At(2))!.Trigger);
    }

    // An enter whose baseline turned out empty still says the character arrived.
    [Fact]
    public void An_enter_with_nothing_read_still_goes_up()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: true);

        Assert.Null(scheduler.Poll(At(1)));

        var upload = scheduler.Poll(At(2))!;
        // `(a, b)` is a tuple: two values traveling together, compared by value, like a two-item
        // array checked with `toEqual`.
        Assert.Equal((CrucibleTrigger.Enter, Board), (upload.Trigger, upload.TerritoryTypeId));
        Assert.Empty(upload.Observations);
    }

    // Only the visit's first upload carries the enter trigger.
    [Fact]
    public void Only_the_first_upload_of_a_visit_is_an_enter()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: true);
        scheduler.Queue(Team(true, 0), Board, At(0));
        scheduler.Queue(Team(false, 1, hp: 400), Board, At(1));

        var first = scheduler.Poll(At(3))!;
        Assert.Equal(CrucibleTrigger.Enter, first.Trigger);
        scheduler.MarkAccepted(first, At(3));

        Assert.Equal(CrucibleTrigger.Change, scheduler.Poll(At(3))!.Trigger);
    }

    // The entrance's windows, read before zoning in, go up as a change; the board's baseline is the
    // enter.
    [Fact]
    public void An_entrance_snapshot_read_before_entering_goes_up_as_a_change()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(false, 0), Entrance, At(0));
        scheduler.NotifyEntered(Board, At(0.5), freshEntry: true);
        scheduler.Queue(Bag(100, 0.5), Board, At(0.5));

        var first = scheduler.Poll(At(2.5))!;
        Assert.Equal((CrucibleTrigger.Change, Entrance), (first.Trigger, first.TerritoryTypeId));
        scheduler.MarkAccepted(first, At(2.5));

        var second = scheduler.Poll(At(2.5))!;
        Assert.Equal((CrucibleTrigger.Enter, Board), (second.Trigger, second.TerritoryTypeId));
        Assert.Equal(["bag"], Kinds(second));
    }

    // Leaving sends whatever the board still has queued, without waiting out the debounce, and
    // then the leave itself, carrying the board just left and no observations.
    [Fact]
    public void Leaving_sends_what_is_queued_and_then_the_leave()
    {
        var scheduler = EnteredAndSettled();
        scheduler.Queue(Bag(100, 10), Board, At(10));
        scheduler.NotifyLeft(At(10.5));

        var flush = scheduler.Poll(At(10.5))!;
        Assert.Equal(CrucibleTrigger.Change, flush.Trigger);
        Assert.Equal(["bag"], Kinds(flush));
        scheduler.MarkAccepted(flush, At(11));

        // The leave itself waits two seconds, for a window that closes as the character zones out.
        Assert.Null(scheduler.Poll(At(12.4)));

        var leave = scheduler.Poll(At(12.5))!;
        Assert.Equal((CrucibleTrigger.Leave, Board), (leave.Trigger, leave.TerritoryTypeId));
        Assert.Empty(leave.Observations);
    }

    // A window that closes as the character zones out belongs to the board left: its closing
    // snapshot joins the board's flush, ahead of the leave, in place of the open one.
    [Fact]
    public void A_window_closing_as_the_character_leaves_goes_up_before_the_leave()
    {
        var scheduler = EnteredAndSettled();
        scheduler.Queue(Team(false, 5), Board, At(5));
        scheduler.NotifyLeft(At(6));
        scheduler.Queue(Team(true, 6, hp: 400), Board, At(6));

        var flush = scheduler.Poll(At(6))!;
        Assert.True(flush.Observations.Single() is CrucibleTeamObservation { Closed: true });
        scheduler.MarkAccepted(flush, At(6));

        Assert.Equal(CrucibleTrigger.Leave, scheduler.Poll(At(8))!.Trigger);
    }

    // The closing snapshot is often read a moment after the leave is noticed, with the scheduler
    // polled in between; the leave's short wait still lets it go first.
    [Fact]
    public void A_window_closing_a_moment_after_leaving_still_goes_before_the_leave()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyLeft(At(10));

        Assert.Null(scheduler.Poll(At(10.016)));
        scheduler.Queue(Team(true, 10.033), Board, At(10.033));

        var flush = scheduler.Poll(At(10.05))!;
        Assert.Equal((CrucibleTrigger.Change, Board), (flush.Trigger, flush.TerritoryTypeId));
        scheduler.MarkAccepted(flush, At(10.05));

        Assert.Equal(CrucibleTrigger.Leave, scheduler.Poll(At(12))!.Trigger);
    }

    // The entrance's window, closing just after the character zones into the board, belongs to the
    // entrance: it goes ahead of the enter, and the board's baseline stays in the enter.
    [Fact]
    public void An_entrance_window_closing_just_after_entering_goes_ahead_of_the_enter()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(10), freshEntry: true);
        scheduler.Queue(Team(true, 10.1), Entrance, At(10.1));
        scheduler.Queue(Bag(100, 10.2), Board, At(10.2));

        var first = scheduler.Poll(At(12.2))!;
        Assert.Equal((CrucibleTrigger.Change, Entrance), (first.Trigger, first.TerritoryTypeId));
        scheduler.MarkAccepted(first, At(12.2));

        var enter = scheduler.Poll(At(12.2))!;
        Assert.Equal((CrucibleTrigger.Enter, Board), (enter.Trigger, enter.TerritoryTypeId));
        Assert.Equal(["bag"], Kinds(enter));
    }

    // Moving straight from one board to another, a window of the first board closing on the way
    // goes before that board's leave, and the second board's baseline stays in its enter.
    [Fact]
    public void Moving_between_boards_keeps_each_boards_snapshots_with_it()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyEntered(OtherBoard, At(10), freshEntry: true);
        scheduler.Queue(Team(true, 10.1), Board, At(10.1));
        scheduler.Queue(Bag(100, 10.2), OtherBoard, At(10.2));

        Assert.Equal((CrucibleTrigger.Change, Board), SendNext(scheduler, At(10.2)));
        Assert.Equal((CrucibleTrigger.Leave, Board), SendNext(scheduler, At(12)));

        var enter = scheduler.Poll(At(12.2))!;
        Assert.Equal((CrucibleTrigger.Enter, OtherBoard), (enter.Trigger, enter.TerritoryTypeId));
        Assert.Equal(["bag"], Kinds(enter));
    }

    // A fresh entry while the character is still in the board is a new visit: the old one is left
    // first, so the server sees a leave and then an enter.
    [Fact]
    public void A_fresh_entry_into_the_same_board_starts_a_new_visit()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyEntered(Board, At(10), freshEntry: true);

        Assert.Equal((CrucibleTrigger.Leave, Board), SendNext(scheduler, At(12)));
        Assert.Equal((CrucibleTrigger.Enter, Board), SendNext(scheduler, At(12)));
    }

    // A snapshot read elsewhere after leaving goes after the leave.
    [Fact]
    public void A_snapshot_read_after_leaving_goes_after_the_leave()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyLeft(At(10));
        scheduler.Queue(Team(false, 11), Entrance, At(11));

        var leave = scheduler.Poll(At(12))!;
        Assert.Equal(CrucibleTrigger.Leave, leave.Trigger);
        scheduler.MarkAccepted(leave, At(12));

        var next = scheduler.Poll(At(13))!;
        Assert.Equal((CrucibleTrigger.Change, Entrance), (next.Trigger, next.TerritoryTypeId));
    }

    // Re-entering the same board before its leave has gone keeps the two visits apart: the old
    // visit's last bag goes before its leave, and the new visit's goes in its enter.
    [Fact]
    public void Re_entering_before_the_leave_goes_keeps_the_visits_apart()
    {
        var scheduler = EnteredAndSettled();
        scheduler.Queue(Bag(50, 5), Board, At(5));
        scheduler.NotifyLeft(At(6));
        scheduler.NotifyEntered(Board, At(7), freshEntry: true);
        scheduler.Queue(Bag(60, 7), Board, At(7));

        var flush = scheduler.Poll(At(7))!;
        Assert.True(flush.Observations.Single() is CrucibleBagObservation { Tokens: 50 });
        scheduler.MarkAccepted(flush, At(7));

        var leave = scheduler.Poll(At(8))!;
        Assert.Equal(CrucibleTrigger.Leave, leave.Trigger);
        scheduler.MarkAccepted(leave, At(8));

        var enter = scheduler.Poll(At(9))!;
        Assert.Equal(CrucibleTrigger.Enter, enter.Trigger);
        Assert.True(enter.Observations.Single() is CrucibleBagObservation { Tokens: 60 });
    }

    // Entering a different board without a leave for the first sends the first board's leave.
    [Fact]
    public void Entering_another_board_sends_the_first_boards_leave()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyEntered(OtherBoard, At(10), freshEntry: true);

        var leave = scheduler.Poll(At(12))!;
        Assert.Equal((CrucibleTrigger.Leave, Board), (leave.Trigger, leave.TerritoryTypeId));
        scheduler.MarkAccepted(leave, At(12));

        var enter = scheduler.Poll(At(12))!;
        Assert.Equal((CrucibleTrigger.Enter, OtherBoard), (enter.Trigger, enter.TerritoryTypeId));
    }

    // Every leave goes, in order, even when a long wait holds them all back.
    [Fact]
    public void Two_visits_left_during_a_wait_both_send_their_leaves()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: true);
        var enter = scheduler.Poll(At(2))!;
        scheduler.MarkRetry(enter, At(1000), At(2));

        scheduler.NotifyLeft(At(10));
        scheduler.NotifyEntered(OtherBoard, At(20), freshEntry: true);
        scheduler.NotifyLeft(At(30));

        // `Assert.Same` checks it is the very same object, like `toBe`, not just an equal one.
        Assert.Same(enter, scheduler.Poll(At(1000)));
        scheduler.MarkAccepted(enter, At(1000));

        Assert.Equal((CrucibleTrigger.Leave, Board), SendNext(scheduler, At(1000)));
        Assert.Equal((CrucibleTrigger.Enter, OtherBoard), SendNext(scheduler, At(1000)));
        Assert.Equal((CrucibleTrigger.Leave, OtherBoard), SendNext(scheduler, At(1000)));
    }

    /// <summary>
    /// Polls the next upload, settles it as accepted, and returns its trigger and territory.
    /// </summary>
    private static (CrucibleTrigger, uint) SendNext(CrucibleUploadScheduler scheduler, DateTimeOffset now)
    {
        var upload = scheduler.Poll(now)!;
        scheduler.MarkAccepted(upload, now);
        return (upload.Trigger, upload.TerritoryTypeId);
    }

    [Fact]
    public void Leaving_without_having_entered_sends_nothing()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyLeft(At(0));

        Assert.Null(scheduler.Poll(At(100)));
    }

    // --- The heartbeat ------------------------------------------------------------------------

    // Inside a board, a quiet interval sends a heartbeat with no observations, under the visit's
    // territory.
    [Fact]
    public void A_quiet_interval_inside_a_board_sends_a_heartbeat()
    {
        var scheduler = EnteredAndSettled();

        Assert.Null(scheduler.Poll(At(61)));

        var heartbeat = scheduler.Poll(At(62))!;
        Assert.Equal((CrucibleTrigger.Heartbeat, Board), (heartbeat.Trigger, heartbeat.TerritoryTypeId));
        Assert.Empty(heartbeat.Observations);
    }

    // The interval the server sets is the one the heartbeat keeps.
    [Fact]
    public void The_heartbeat_keeps_the_interval_the_server_sets()
    {
        var scheduler = EnteredAndSettled();
        scheduler.ApplyHeartbeat(90);

        Assert.Null(scheduler.Poll(At(62)));
        Assert.Equal(CrucibleTrigger.Heartbeat, scheduler.Poll(At(92))!.Trigger);
    }

    // Any upload restarts the quiet interval, so a busy board sends no heartbeats at all.
    [Fact]
    public void Any_upload_restarts_the_heartbeat_interval()
    {
        var scheduler = EnteredAndSettled();
        scheduler.Queue(Bag(100, 50), Board, At(50));
        scheduler.MarkAccepted(scheduler.Poll(At(52))!, At(52));

        Assert.Null(scheduler.Poll(At(70)));
        Assert.Equal(CrucibleTrigger.Heartbeat, scheduler.Poll(At(112))!.Trigger);
    }

    // Entering a board after a long quiet spell sends its enter first, never a heartbeat ahead of
    // it.
    [Fact]
    public void Entering_after_a_long_quiet_spell_sends_the_enter_first()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyLeft(At(10));
        scheduler.MarkAccepted(scheduler.Poll(At(12))!, At(12));

        scheduler.NotifyEntered(OtherBoard, At(1000), freshEntry: true);

        Assert.Null(scheduler.Poll(At(1000)));
        Assert.Equal(CrucibleTrigger.Enter, scheduler.Poll(At(1002))!.Trigger);
    }

    // While snapshots are waiting to go, they are the sign of life; no heartbeat goes ahead of them.
    [Fact]
    public void No_heartbeat_goes_up_while_snapshots_are_waiting()
    {
        var scheduler = EnteredAndSettled();
        scheduler.Queue(Bag(100, 61), Board, At(61));

        Assert.Null(scheduler.Poll(At(62)));
        Assert.Equal(CrucibleTrigger.Change, scheduler.Poll(At(63))!.Trigger);
    }

    // Outside a board, and after leaving one, there is no heartbeat.
    [Fact]
    public void No_heartbeat_goes_up_outside_a_board()
    {
        var scheduler = EnteredAndSettled();
        scheduler.NotifyLeft(At(10));
        scheduler.MarkAccepted(scheduler.Poll(At(12))!, At(12));

        Assert.Null(scheduler.Poll(At(1000)));
    }

    // A clock that jumps backwards makes the earlier moment the heartbeat's reference point, so the
    // heartbeat does not stall for however far the clock jumped.
    [Fact]
    public void A_clock_that_jumps_backwards_does_not_stall_the_heartbeat()
    {
        var scheduler = EnteredAndSettled();

        Assert.Null(scheduler.Poll(At(-100)));
        Assert.Equal(CrucibleTrigger.Heartbeat, scheduler.Poll(At(-40))!.Trigger);
    }

    // Starting inside a board with nothing to send yet: the heartbeat counts from the start, not
    // from an earlier upload.
    [Fact]
    public void Starting_inside_a_board_waits_a_full_interval_before_the_heartbeat()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: false);

        Assert.Null(scheduler.Poll(At(59)));
        Assert.Equal(CrucibleTrigger.Heartbeat, scheduler.Poll(At(60))!.Trigger);
    }

    // The cadence comes from /config, held between 30 seconds and five minutes whatever the
    // server sends.
    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(90, 90)]
    [InlineData(100_000, 300)]
    public void The_heartbeat_cadence_is_held_within_its_bounds(int requested, int expected)
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.ApplyHeartbeat(requested);

        Assert.Equal(TimeSpan.FromSeconds(expected), scheduler.Heartbeat);
    }

    // --- The upload budget ----------------------------------------------------------------------

    // Four uploads can go at once; after that one more is allowed every sixteen seconds.
    [Fact]
    public void A_fifth_upload_in_a_burst_waits_for_the_budget()
    {
        var scheduler = new CrucibleUploadScheduler();
        for (uint territory = 1; territory <= 5; territory++)
            scheduler.Queue(Bag(100, 0), territory, At(0));

        for (var upload = 0; upload < 4; upload++)
            scheduler.MarkAccepted(scheduler.Poll(At(2))!, At(2));

        Assert.Null(scheduler.Poll(At(17.9)));

        // The `u` suffix makes the 5 a `uint`, the same type as a territory id.
        Assert.Equal(5u, scheduler.Poll(At(18))!.TerritoryTypeId);
    }

    // --- Retrying -------------------------------------------------------------------------------

    // A retried upload goes again after the wait, as the very same request: same snapshots, same
    // moments.
    [Fact]
    public void A_retried_upload_goes_again_unchanged_after_the_wait()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var upload = scheduler.Poll(At(2))!;

        scheduler.MarkRetry(upload, At(30), At(2));

        Assert.Null(scheduler.Poll(At(29)));
        Assert.Same(upload, scheduler.Poll(At(30)));
    }

    // A wait of nothing, or one already past, still waits fifteen seconds.
    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void A_retry_waits_at_least_fifteen_seconds(double retryAtSeconds)
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var upload = scheduler.Poll(At(2))!;

        scheduler.MarkRetry(upload, At(retryAtSeconds), At(2));

        Assert.Null(scheduler.Poll(At(16.9)));
        Assert.Same(upload, scheduler.Poll(At(17)));
    }

    // Each failure in a row doubles the least wait, so a failure that persists slows down.
    [Fact]
    public void Each_retry_in_a_row_waits_twice_as_long()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var upload = scheduler.Poll(At(2))!;

        scheduler.MarkRetry(upload, At(2), At(2));
        Assert.Same(upload, scheduler.Poll(At(17)));

        scheduler.MarkRetry(upload, At(17), At(17));
        Assert.Null(scheduler.Poll(At(46.9)));
        Assert.Same(upload, scheduler.Poll(At(47)));
    }

    // The least wait stops doubling at five minutes.
    [Fact]
    public void The_least_retry_wait_stops_growing_at_five_minutes()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var upload = scheduler.Poll(At(2))!;

        // 15, 30, 60, 120 and 240 seconds, then capped at 300.
        var now = 2.0;
        foreach (var wait in new[] { 15, 30, 60, 120, 240, 300, 300 })
        {
            scheduler.MarkRetry(upload, At(now), At(now));
            Assert.Null(scheduler.Poll(At(now + wait - 0.1)));
            Assert.Same(upload, scheduler.Poll(At(now + wait)));
            now += wait;
        }
    }

    // A success ends the run of failures, so the next retry waits the shortest wait again.
    [Fact]
    public void A_success_resets_the_retry_wait()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var first = scheduler.Poll(At(2))!;
        scheduler.MarkRetry(first, At(2), At(2));
        scheduler.MarkAccepted(scheduler.Poll(At(17))!, At(17));

        scheduler.Queue(Bag(120, 20), Board, At(20));
        var second = scheduler.Poll(At(22))!;
        scheduler.MarkRetry(second, At(22), At(22));

        Assert.Same(second, scheduler.Poll(At(37)));
    }

    // A clock that jumps backwards does not stretch a wait: it moves back with the clock.
    [Fact]
    public void A_clock_that_jumps_backwards_keeps_a_wait_its_length()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var upload = scheduler.Poll(At(2))!;
        scheduler.MarkRetry(upload, At(2), At(2));

        Assert.Null(scheduler.Poll(At(-3598)));
        Assert.Same(upload, scheduler.Poll(At(-3583)));
    }

    // A retry goes before anything else waiting, the board's leave included.
    [Fact]
    public void A_retry_goes_before_a_waiting_leave()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: true);
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var enter = scheduler.Poll(At(2))!;
        scheduler.NotifyLeft(At(3));
        scheduler.MarkRetry(enter, At(30), At(3));

        Assert.Same(enter, scheduler.Poll(At(30)));
        scheduler.MarkAccepted(enter, At(30));

        Assert.Equal(CrucibleTrigger.Leave, scheduler.Poll(At(30))!.Trigger);
    }

    // A refused upload is dropped, and the queue carries on with what comes after it.
    [Fact]
    public void A_dropped_upload_is_not_sent_again()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(true, 0), Board, At(0));
        scheduler.Queue(Team(false, 1, hp: 400), Board, At(1));

        scheduler.MarkDropped(scheduler.Poll(At(3))!, At(3));

        var next = scheduler.Poll(At(3))!;
        Assert.True(next.Observations.Single() is CrucibleTeamObservation { Closed: false });
    }

    // A refused upload can hold everything back for a while, so a refusal that repeats does not
    // repeat at full speed.
    [Fact]
    public void A_dropped_upload_can_hold_what_follows()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Team(true, 0), Board, At(0));
        scheduler.Queue(Team(false, 1, hp: 400), Board, At(1));

        scheduler.MarkDropped(scheduler.Poll(At(3))!, At(3), holdUntil: At(900));

        Assert.Null(scheduler.Poll(At(899)));
        Assert.NotNull(scheduler.Poll(At(900)));
    }

    // An answer for an earlier upload changes nothing, even when a newer upload is equal to it.
    [Fact]
    public void A_late_answer_for_an_earlier_upload_settles_nothing()
    {
        var scheduler = EnteredAndSettled();
        var early = scheduler.Poll(At(62))!;

        scheduler.Reset();
        scheduler.NotifyEntered(Board, At(70), freshEntry: false);
        scheduler.Queue(Bag(100, 70), Board, At(70));
        scheduler.MarkAccepted(scheduler.Poll(At(72))!, At(72));
        Assert.Equal(CrucibleTrigger.Heartbeat, scheduler.Poll(At(132))!.Trigger);

        scheduler.MarkAccepted(early, At(133));

        Assert.Null(scheduler.Poll(At(300)));
    }

    // A server's request to wait still holds when it answers an earlier upload.
    [Fact]
    public void A_late_request_to_wait_still_holds()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var early = scheduler.Poll(At(2))!;

        scheduler.Reset();
        scheduler.MarkRetry(early, At(100), At(3));
        scheduler.Queue(Bag(120, 3), Board, At(3));

        Assert.Null(scheduler.Poll(At(50)));
        Assert.Equal(["bag"], Kinds(scheduler.Poll(At(100))!));
    }

    // --- Resetting ------------------------------------------------------------------------------

    // A reset, as on logging out, forgets everything queued, the visit, the retry and the content
    // change detection compares against, so the next character starts from a clean baseline; a
    // server-instructed wait survives it.
    [Fact]
    public void A_reset_forgets_the_queue_and_the_visit_but_not_a_wait()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.NotifyEntered(Board, At(0), freshEntry: true);
        scheduler.Queue(Bag(100, 0), Board, At(0));
        var enter = scheduler.Poll(At(2))!;
        scheduler.MarkRetry(enter, At(100), At(2));
        scheduler.Queue(Team(false, 3), Board, At(3));

        scheduler.Reset();
        scheduler.Queue(Bag(100, 10), Board, At(10));

        Assert.Null(scheduler.Poll(At(50)));

        var next = scheduler.Poll(At(100))!;
        Assert.Equal(CrucibleTrigger.Change, next.Trigger);
        Assert.Equal(["bag"], Kinds(next));
        scheduler.MarkAccepted(next, At(100));
        Assert.Null(scheduler.Poll(At(1000)));
    }

    // A snapshot left unsent for half an hour, as through a long hold, is let go.
    [Fact]
    public void A_snapshot_left_unsent_for_half_an_hour_is_let_go()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.Queue(Bag(100, 0), OtherBoard, At(0));
        scheduler.MarkDropped(scheduler.Poll(At(2))!, At(2), holdUntil: At(1801));

        Assert.Null(scheduler.Poll(At(1801)));
    }

    // Short of half an hour, a held snapshot still goes once the hold ends.
    [Fact]
    public void A_snapshot_held_for_less_than_half_an_hour_still_goes()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.Queue(Bag(100, 0), OtherBoard, At(0));
        scheduler.MarkDropped(scheduler.Poll(At(2))!, At(2), holdUntil: At(1799));

        Assert.Equal(OtherBoard, scheduler.Poll(At(1799))!.TerritoryTypeId);
    }

    // A snapshot let go never reached the server, so the same content read again still goes up.
    [Fact]
    public void Content_let_go_unsent_goes_up_when_read_again()
    {
        var scheduler = new CrucibleUploadScheduler();
        scheduler.Queue(Bag(100, 0), Board, At(0));
        scheduler.Queue(Bag(100, 0), OtherBoard, At(0));
        scheduler.MarkDropped(scheduler.Poll(At(2))!, At(2), holdUntil: At(1801));
        Assert.Null(scheduler.Poll(At(1801)));

        scheduler.Queue(Bag(100, 1801), OtherBoard, At(1801));

        Assert.Equal(OtherBoard, scheduler.Poll(At(1803))!.TerritoryTypeId);
    }

    // --- Kinds ----------------------------------------------------------------------------------

    // The scheduler's name for each kind is the `kind` key the serializer writes for it, so change
    // detection and the wire agree.
    [Fact]
    public void Each_kind_has_the_name_the_wire_gives_it()
    {
        var results = new CrucibleResultsReading(
            "Standard", 1, 2, 3, new CrucibleScoreLine(4, 5), new CrucibleScoreLine(6, 7),
            new CrucibleScoreLine(8, 9), 10, 11, 12, "Exemplary", [], []);
        CrucibleObservation[] observations =
        [
            CrucibleUploadBuilder.Board(
                new CrucibleBoardReading(CrucibleBoardView.WholeBoard, [], null, null), T0, false),
            Team(false, 0),
            Bag(1, 0),
            CrucibleUploadBuilder.Offer(
                new CrucibleOfferReading(CrucibleOfferSource.Shop, 0, null, []), T0, false),
            CrucibleUploadBuilder.Results(results, T0, null, null, _ => null),
            CrucibleUploadBuilder.Self(1, 2, T0),
        ];

        foreach (var observation in observations)
        {
            var json = JsonNode.Parse(JsonSerializer.Serialize(observation, ApiJson.Options))!;
            Assert.Equal(json["kind"]!.GetValue<string>(), CrucibleUploadScheduler.KindOf(observation));
        }
    }
}
