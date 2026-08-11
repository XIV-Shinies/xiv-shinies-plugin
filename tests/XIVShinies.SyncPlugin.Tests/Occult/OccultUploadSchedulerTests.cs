using System;
using Xunit;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// Decides *when* the occult uploader fires and with which trigger word, never *what* it sends
// (the tracker owns the snapshot). Same pure-scheduler discipline as SyncSchedulerTests: no
// clock, no game, no network — every method takes the current time.
public class OccultUploadSchedulerTests
{
    // An arbitrary fixed instant. Tests move time by adding to it, never by waiting.
    private static readonly DateTimeOffset T0 = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_scheduler_has_nothing_to_do()
    {
        Assert.Null(new OccultUploadScheduler().Poll(T0));
    }

    // --- Enter ---------------------------------------------------------------------------

    [Fact]
    public void Entering_an_instance_is_due_immediately()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);

        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));
    }

    [Fact]
    public void Polling_consumes_the_pending_work()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);

        Assert.NotNull(scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0));
    }

    // The enter upload carries the same full snapshot a change would, so a change that raced
    // the enter is redundant and must not spend a second request.
    [Fact]
    public void An_enter_swallows_a_pending_change()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.NotifyEntered(T0);

        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(10)));
    }

    // The common real ordering: territory change queues the enter, then the instance's first
    // Apply registers as a change (every id is new). The snapshot is read when the enter is
    // ISSUED, so that change is already covered — a second upload would be byte-identical.
    [Fact]
    public void An_enter_swallows_a_change_that_arrived_before_it_was_issued()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0 + TimeSpan.FromSeconds(1)));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(10)));
    }

    // But a change noticed AFTER the enter went out is new information and fires on its own.
    [Fact]
    public void A_change_after_the_enter_was_issued_fires_on_its_own()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));

        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromSeconds(3)));
    }

    // --- Change debounce -----------------------------------------------------------------

    [Fact]
    public void A_change_waits_for_the_debounce_window_to_close()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(1)));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromSeconds(2)));
    }

    // A CE flip often lands alongside FATE flips in the same second or two. Each new change
    // slides the window so the burst rides in one upload.
    [Fact]
    public void A_second_change_restarts_the_debounce_window()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(2)));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Many_changes_collapse_into_one_upload()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.NotifyChanged(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));

        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromSeconds(3)));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(4)));
    }

    // --- Heartbeat -----------------------------------------------------------------------

    // The heartbeat is an idle re-upload keeping presence and tracker liveness fresh. Its
    // interval runs from the last trigger the scheduler issued, whatever kind it was.
    [Fact]
    public void A_heartbeat_becomes_due_after_the_interval_since_the_last_issued_trigger()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(59)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void A_successful_change_upload_postpones_the_heartbeat()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(30));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromSeconds(32)));
        scheduler.MarkUploaded(T0 + TimeSpan.FromSeconds(32));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(60)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(92)));
    }

    // A failed heartbeat must wait out a fresh interval, not retry every tick. The interval
    // clock stamps when the heartbeat is ISSUED (the SyncScheduler pattern).
    [Fact]
    public void A_heartbeat_is_not_retried_every_tick_after_a_failed_upload()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(60)));
        // No MarkUploaded — the upload failed. The next heartbeat is an interval away.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(61)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(120)));
    }

    // The heartbeat clock anchors when work is ISSUED, not when it succeeds — so one
    // transient failure on the enter upload cannot silently end presence reporting for the
    // whole visit; the heartbeat retries the snapshot an interval later.
    [Fact]
    public void A_failed_enter_upload_still_leads_to_a_heartbeat()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));

        // No MarkUploaded — the enter upload failed.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(59)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(60)));
    }

    // An NTP correction or laptop resume can move the clock backwards. The anchor must
    // follow it, or the heartbeat stalls for the length of the jump and the server ages
    // the reporter out.
    [Fact]
    public void A_backwards_clock_jump_does_not_stall_the_heartbeat()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        var jumpedBack = T0 - TimeSpan.FromMinutes(30);
        Assert.Null(scheduler.Poll(jumpedBack));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(jumpedBack + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void No_heartbeat_after_leaving_the_instance()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        scheduler.NotifyLeft(T0 + TimeSpan.FromSeconds(10));
        Assert.Equal(OccultTrigger.Leave, scheduler.Poll(T0 + TimeSpan.FromSeconds(10)));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(10)));
    }

    // --- Leave ---------------------------------------------------------------------------

    // An enter queued before the leave describes a visit that is already over; letting it
    // fire would re-assert presence in an instance the character left.
    [Fact]
    public void A_leave_cancels_a_stale_pending_enter()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.NotifyLeft(T0);

        Assert.Equal(OccultTrigger.Leave, scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(5)));
    }

    // The other order is a real itinerary: leave instance A, enter instance B. Both fire,
    // leave first.
    [Fact]
    public void Leaving_one_instance_and_entering_another_fires_both_in_order()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyLeft(T0);
        scheduler.NotifyEntered(T0);

        Assert.Equal(OccultTrigger.Leave, scheduler.Poll(T0));
        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));
    }

    [Fact]
    public void Leaving_is_due_immediately_and_drops_a_pending_change()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        // The leave upload carries the final snapshot; the debouncing change is redundant.
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(5));
        scheduler.NotifyLeft(T0 + TimeSpan.FromSeconds(6));

        Assert.Equal(OccultTrigger.Leave, scheduler.Poll(T0 + TimeSpan.FromSeconds(6)));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(10)));
    }

    // --- Backoff -------------------------------------------------------------------------

    // Deferred, never dropped: pending work survives the backoff (matching SyncScheduler).
    [Fact]
    public void A_backoff_defers_pending_work_without_dropping_it()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(2));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void A_backoff_never_shortens_one_already_in_force()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(1));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(2)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(5)));
    }

    // --- Config --------------------------------------------------------------------------

    // 90 sits strictly inside the clamp range, so this only passes if the config value is
    // genuinely honored (a clamped-to-bound value could not tell the difference).
    [Fact]
    public void The_heartbeat_interval_comes_from_config_clamped_to_a_sane_range()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.ApplyHeartbeat(heartbeatSeconds: 90);

        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(89)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(90)));
    }

    // A zero or negative cadence from a misconfigured (or hostile — the backend URL is
    // user-overridable) server must not become a request flood.
    [Fact]
    public void An_absurd_heartbeat_interval_is_clamped()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.ApplyHeartbeat(heartbeatSeconds: 0);

        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(1)));
        Assert.NotNull(scheduler.Poll(T0 + OccultUploadScheduler.MinHeartbeat));
    }

    // Both bounds hold for any server-supplied value — the enormous end would silently break
    // presence (the server ages a reporter out after ~3 missed heartbeats).
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void The_heartbeat_interval_is_always_within_bounds(int heartbeatSeconds)
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.ApplyHeartbeat(heartbeatSeconds);

        Assert.InRange(
            scheduler.Heartbeat, OccultUploadScheduler.MinHeartbeat, OccultUploadScheduler.MaxHeartbeat);
    }

    // --- Reset (logout) ------------------------------------------------------------------

    [Fact]
    public void Reset_forgets_all_queued_work_and_the_heartbeat()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(5));

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(10)));
    }

    // The backoff is the server talking to the TOKEN, not the character — relogging must
    // not become a way to shake off a rate limit.
    [Fact]
    public void Reset_keeps_a_backoff_in_force()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));
        scheduler.Reset();

        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(10));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromMinutes(5)));
    }
}
