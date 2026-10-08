using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// How what the game shows reaches the scheduler: the visit, where each snapshot is filed, and the
// closing snapshot each window sends once per opening. The scheduler is handed the current moment, so
// every test sets the time explicitly. Each `[Fact]` is one test, like `it(...)` in Jest.
public class CrucibleFeedTests
{
    // `uint` is a whole number that can never be negative (an unsigned integer), the type of a
    // territory id; `const` fixes the value when the code compiles.
    private const uint Board = CrucibleTerritories.FirstBoard;
    private const uint Entrance = 148;

    // `new(...)` with no type name builds the field's declared type, here a DateTimeOffset.
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A moment this many seconds after <see cref="T0"/>.</summary>
    // `=>` makes the expression after it the whole method body, like an arrow function.
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>What makes a team snapshot in this mode, as a window read would hand it over.</summary>
    // `(at, closed) => ...` is a lambda that becomes the snapshot maker, like an arrow function. `[]`
    // is an empty list.
    private static CrucibleSnapshot Team(CrucibleTeamMode mode) =>
        (at, closed) => CrucibleUploadBuilder.Team(new CrucibleTeamReading(mode, null, []), at, closed);

    /// <summary>A feed with the scheduler it fills, so a test can poll what went up.</summary>
    // A method can hand back two values at once as a tuple, like returning `[a, b]` in TypeScript.
    private static (CrucibleFeed Feed, CrucibleUploadScheduler Scheduler) NewFeed()
    {
        var scheduler = new CrucibleUploadScheduler();
        return (new CrucibleFeed(scheduler), scheduler);
    }

    /// <summary>Polls the next upload and settles it as accepted.</summary>
    private static CrucibleUpload SendNext(CrucibleUploadScheduler scheduler, double seconds)
    {
        var upload = scheduler.Poll(At(seconds))!;
        scheduler.MarkAccepted(upload, At(seconds));
        return upload;
    }

    // A window that closes just after the character zones out belongs to the board it was read in:
    // its closing snapshot goes up under that board, ahead of the leave.
    [Fact]
    public void A_window_closing_after_leaving_goes_up_under_its_board_before_the_leave()
    {
        // `var (feed, scheduler) = ...` takes the returned tuple apart into two variables.
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Follow(Board, At(0));
        SendNext(scheduler, 2);
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(5));
        SendNext(scheduler, 7);

        Assert.Equal(CrucibleVisitChange.Left, feed.Follow(Entrance, At(10)));
        feed.Close(CrucibleWindows.Team, At(10.1));

        var closing = SendNext(scheduler, 10.1);
        Assert.Equal(Board, closing.TerritoryTypeId);

        // `Assert.Single` hands back the one item and fails unless there is exactly one;
        // `Assert.IsType<T>` checks the item's type and hands it back as that type.
        Assert.True(Assert.IsType<CrucibleTeamObservation>(Assert.Single(closing.Observations)).Closed);
        Assert.Equal(CrucibleTrigger.Leave, SendNext(scheduler, 12.1).Trigger);
    }

    // A board's window redrawn on the first frames back at the entrance is refused there, so the
    // window still closes under the board, ahead of the leave.
    [Fact]
    public void A_board_window_redrawn_after_leaving_still_closes_under_its_board()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Follow(Board, At(0));
        SendNext(scheduler, 2);
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(5));
        SendNext(scheduler, 7);

        feed.Follow(Entrance, At(10));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Entrance, At(10.05));
        feed.Close(CrucibleWindows.Team, At(10.1));

        var closing = SendNext(scheduler, 10.1);
        Assert.Equal(Board, closing.TerritoryTypeId);
        Assert.True(Assert.IsType<CrucibleTeamObservation>(Assert.Single(closing.Observations)).Closed);
        Assert.Equal(CrucibleTrigger.Leave, SendNext(scheduler, 12.1).Trigger);
    }

    // The entrance's roster pick, closing just after the character zones into the board, belongs to
    // the entrance: it goes up there as a change, ahead of the board's enter.
    [Fact]
    public void An_entrance_window_closing_after_entering_goes_up_under_the_entrance()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.RosterPick), Entrance, At(0));
        SendNext(scheduler, 2);

        feed.Follow(Board, At(5));
        feed.Close(CrucibleWindows.Team, At(5.1));

        // Two tuples compare by value, part by part, so one assertion checks both.
        var closing = SendNext(scheduler, 7.1);
        Assert.Equal((CrucibleTrigger.Change, Entrance), (closing.Trigger, closing.TerritoryTypeId));

        var enter = SendNext(scheduler, 7.1);
        Assert.Equal((CrucibleTrigger.Enter, Board), (enter.Trigger, enter.TerritoryTypeId));
    }

    // The roster pick can be redrawn just after the character zones into the board. The server
    // refuses it from a board, so that reading does not join the board's enter.
    [Fact]
    public void An_entrance_window_read_inside_a_board_is_not_queued()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Follow(Board, At(5));

        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.RosterPick), Board, At(5.5));

        var enter = SendNext(scheduler, 8);
        Assert.Equal(CrucibleTrigger.Enter, enter.Trigger);
        Assert.Empty(enter.Observations);
    }

    // A reading refused where it was read leaves the window's last admitted reading in place, so a
    // roster pick redrawn just after zoning in still closes under the entrance.
    [Fact]
    public void A_refused_reading_leaves_the_window_closing_under_its_last_admitted_place()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.RosterPick), Entrance, At(0));
        SendNext(scheduler, 2);

        feed.Follow(Board, At(5));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.RosterPick), Board, At(5.05));
        feed.Close(CrucibleWindows.Team, At(5.1));

        var closing = SendNext(scheduler, 7.1);
        Assert.Equal((CrucibleTrigger.Change, Entrance), (closing.Trigger, closing.TerritoryTypeId));
        Assert.True(Assert.IsType<CrucibleTeamObservation>(Assert.Single(closing.Observations)).Closed);

        var enter = SendNext(scheduler, 7.1);
        Assert.Equal((CrucibleTrigger.Enter, Board), (enter.Trigger, enter.TerritoryTypeId));
        Assert.Empty(enter.Observations);
    }

    // A lineup goes up only from a board, so a lineup read at the entrance is not queued.
    [Fact]
    public void A_snapshot_its_place_does_not_admit_is_not_queued()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));

        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Entrance, At(0));

        Assert.Null(scheduler.Poll(At(100)));
    }

    // A window may close through more than one event; it sends one closing snapshot per opening.
    [Fact]
    public void A_window_sends_one_closing_snapshot_per_opening()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);

        feed.Close(CrucibleWindows.Team, At(5));
        feed.Close(CrucibleWindows.Team, At(5));
        SendNext(scheduler, 7);

        Assert.Null(scheduler.Poll(At(30)));
    }

    // A window that closes with no reading taken since it opened has nothing to close.
    [Fact]
    public void A_window_closing_without_a_reading_sends_nothing()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        SendNext(scheduler, 2);

        feed.Close(CrucibleWindows.Team, At(5));

        Assert.Null(scheduler.Poll(At(30)));
    }

    // With no read at the close, the closing snapshot is the last reading, stamped with the moment the
    // window closed.
    [Fact]
    public void A_closing_snapshot_is_stamped_when_its_window_closes()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);

        feed.Close(CrucibleWindows.Team, At(5));

        var closing = Assert.Single(SendNext(scheduler, 7).Observations);
        Assert.Equal("2026-10-05T12:00:05Z", closing.ObservedAtUtc);
    }

    // A window's contents can change without a redraw, so the window read again as it closes is its
    // final state, and that reading is what goes up. The two readings here differ in mode only so the
    // test can tell which one was sent.
    [Fact]
    public void A_closing_snapshot_carries_the_window_as_read_at_the_close()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);

        var outcome = feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Browse));

        Assert.Equal(CrucibleCloseOutcome.ReadAtClose, outcome);
        var observations = SendNext(scheduler, 7).Observations;
        var closing = Assert.IsType<CrucibleTeamObservation>(Assert.Single(observations));
        Assert.True(closing.Closed);

        // `(int)` turns the named mode into the number the snapshot carries.
        Assert.Equal((int)CrucibleTeamMode.Browse, closing.Mode);
        Assert.Equal("2026-10-05T12:00:05Z", closing.ObservedAtUtc);
    }

    // A read at the close that matches the last reading says so, which tells a window that changed
    // since its last admitted reading apart from one that did not.
    [Fact]
    public void A_read_at_the_close_matching_the_last_reading_is_reported_unchanged()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);

        var outcome = feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Lineup));

        Assert.Equal(CrucibleCloseOutcome.ReadAtCloseUnchanged, outcome);
    }

    // With no read at the close, the last reading's own content goes up.
    [Fact]
    public void A_window_not_read_at_its_close_sends_its_last_reading()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);

        var outcome = feed.Close(CrucibleWindows.Team, At(5));

        Assert.Equal(CrucibleCloseOutcome.LastReading, outcome);
        var closing = Assert.IsType<CrucibleTeamObservation>(
            Assert.Single(SendNext(scheduler, 7).Observations));
        Assert.Equal((int)CrucibleTeamMode.Lineup, closing.Mode);
    }

    // A read at the close that the window's own territory refuses (a lineup where a roster pick was
    // open at the entrance) gives way to the last reading, so the opening still gets its close.
    [Fact]
    public void A_read_at_the_close_refused_where_the_window_was_open_falls_back_to_the_last_reading()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.RosterPick), Entrance, At(0));
        SendNext(scheduler, 2);

        var outcome = feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Lineup));

        Assert.Equal(CrucibleCloseOutcome.LastReadingAtCloseRefused, outcome);
        var closing = SendNext(scheduler, 7);
        Assert.Equal(Entrance, closing.TerritoryTypeId);
        var team = Assert.IsType<CrucibleTeamObservation>(Assert.Single(closing.Observations));
        Assert.True(team.Closed);
        Assert.Equal((int)CrucibleTeamMode.RosterPick, team.Mode);
    }

    // A window's second close event finds no opening and says so.
    [Fact]
    public void A_second_close_event_reports_no_opening()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);
        feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Lineup));

        Assert.Equal(
            CrucibleCloseOutcome.NoOpening,
            feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Lineup)));
    }

    // A reading at the close is still one closing snapshot per opening.
    [Fact]
    public void A_window_read_at_its_close_still_sends_one_closing_snapshot_per_opening()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));
        SendNext(scheduler, 2);

        feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Browse));
        feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Browse));
        SendNext(scheduler, 7);

        Assert.Null(scheduler.Poll(At(30)));
    }

    // The reading at the close is filed where the window was open, as the last reading would be:
    // a board's window closing just after the character zones out still goes up under the board.
    [Fact]
    public void A_window_read_at_its_close_after_leaving_goes_up_under_its_board()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Entrance, At(0));
        feed.Follow(Board, At(0));
        SendNext(scheduler, 2);
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(5));
        SendNext(scheduler, 7);

        feed.Follow(Entrance, At(10));
        feed.Close(CrucibleWindows.Team, At(10.1), Team(CrucibleTeamMode.Browse));

        var closing = SendNext(scheduler, 10.1);
        Assert.Equal(Board, closing.TerritoryTypeId);
        Assert.Equal(
            (int)CrucibleTeamMode.Browse,
            Assert.IsType<CrucibleTeamObservation>(Assert.Single(closing.Observations)).Mode);
    }

    // A window with no admitted reading since it opened sends nothing at its close, whatever was read
    // then: the reading taken while open is what marks an opening, so a window closing through two
    // events cannot send twice.
    [Fact]
    public void A_window_closing_without_an_earlier_reading_sends_nothing_even_when_read_at_the_close()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        SendNext(scheduler, 2);

        feed.Close(CrucibleWindows.Team, At(5), Team(CrucibleTeamMode.Lineup));

        Assert.Null(scheduler.Poll(At(30)));
    }

    // The run HUD stays open and has no closing snapshot, so closing it sends nothing. In the lambda
    // below, `_` names a parameter it ignores: a bag has no closed flag.
    [Fact]
    public void A_window_without_a_closing_snapshot_sends_nothing_on_closing()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(
            CrucibleWindows.Hud,
            (at, _) => CrucibleUploadBuilder.Bag(new CrucibleBagReading(100, [], []), at),
            Board,
            At(0));
        SendNext(scheduler, 2);

        feed.Close(CrucibleWindows.Hud, At(5));

        Assert.Null(scheduler.Poll(At(30)));
    }

    // A visit starts with its bag still to read, until a bag snapshot is offered or the visit ends.
    [Fact]
    public void A_visit_waits_for_its_bag_until_one_is_offered()
    {
        var (feed, _) = NewFeed();
        Assert.False(feed.BagPending);

        feed.Follow(Board, At(0));
        Assert.True(feed.BagPending);

        feed.Offer(CrucibleUploadBuilder.Bag(new CrucibleBagReading(100, [], []), At(1)), Board, At(1));
        Assert.False(feed.BagPending);
    }

    // A bag read from the run HUD ends the wait the same way.
    [Fact]
    public void A_bag_read_from_the_hud_ends_the_wait_for_the_bag()
    {
        var (feed, _) = NewFeed();
        feed.Follow(Board, At(0));

        feed.Read(
            CrucibleWindows.Hud,
            (at, _) => CrucibleUploadBuilder.Bag(new CrucibleBagReading(100, [], []), at),
            Board,
            At(1));

        Assert.False(feed.BagPending);
    }

    [Fact]
    public void Leaving_ends_the_wait_for_the_bag()
    {
        var (feed, _) = NewFeed();
        feed.Follow(Board, At(0));

        feed.Follow(Entrance, At(5));

        Assert.False(feed.BagPending);
    }

    // Starting inside a board sends the baseline as a change, and zoning in sends it as an enter.
    [Fact]
    public void Starting_inside_a_board_sends_a_change_and_zoning_in_sends_an_enter()
    {
        var (feed, scheduler) = NewFeed();
        Assert.Equal(CrucibleVisitChange.AlreadyInside, feed.Follow(Board, At(0)));
        feed.Offer(CrucibleUploadBuilder.Self(500, 1000, At(0)), Board, At(0));
        Assert.Equal(CrucibleTrigger.Change, SendNext(scheduler, 2).Trigger);

        feed.Follow(Entrance, At(10));
        SendNext(scheduler, 12);
        Assert.Equal(CrucibleVisitChange.Entered, feed.Follow(Board, At(20)));
        Assert.Equal(CrucibleTrigger.Enter, SendNext(scheduler, 22).Trigger);
    }

    // A reset forgets the visit, the remembered readings, the pending bag and the queue.
    [Fact]
    public void A_reset_forgets_everything()
    {
        var (feed, scheduler) = NewFeed();
        feed.Follow(Board, At(0));
        feed.Read(CrucibleWindows.Team, Team(CrucibleTeamMode.Lineup), Board, At(0));

        feed.Reset();
        feed.Close(CrucibleWindows.Team, At(1));

        Assert.Null(feed.Board);
        Assert.False(feed.BagPending);
        Assert.Null(scheduler.Poll(At(30)));
    }
}
