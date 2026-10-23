using System;
using Xunit;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// The scheduler decides when the occult uploader fires and with which trigger; the tracker owns
// what it sends.
public class OccultUploadSchedulerTests
{
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

    // An enter carries the full snapshot a change would, so a racing change must not spend a second
    // request.
    [Fact]
    public void An_enter_swallows_a_pending_change()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.NotifyEntered(T0);

        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(10)));
    }

    // The usual order: the territory change queues the enter, then the instance's first Apply
    // registers as a change because every id is new. The snapshot is read when the enter is issued,
    // so a second upload would be byte-identical.
    [Fact]
    public void An_enter_swallows_a_change_that_arrived_before_it_was_issued()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0 + TimeSpan.FromSeconds(1)));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(10)));
    }

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

    // A CE flip often lands with FATE flips within a second or two, so the burst rides in one upload.
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

    // The heartbeat is an idle re-upload that keeps presence and tracker liveness fresh.
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

    // The interval clock stamps when the heartbeat is issued, so a failure waits out a fresh interval.
    [Fact]
    public void A_heartbeat_is_not_retried_every_tick_after_a_failed_upload()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.Poll(T0);
        scheduler.MarkUploaded(T0);

        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(60)));
        // No MarkUploaded: the upload failed.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(61)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(120)));
    }

    // One transient failure on the enter upload must not end presence reporting for the whole visit;
    // the heartbeat retries the snapshot an interval later.
    [Fact]
    public void A_failed_enter_upload_still_leads_to_a_heartbeat()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        Assert.Equal(OccultTrigger.Enter, scheduler.Poll(T0));

        // No MarkUploaded: the enter upload failed.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(59)));
        Assert.Equal(OccultTrigger.Heartbeat, scheduler.Poll(T0 + TimeSpan.FromSeconds(60)));
    }

    // An NTP correction or laptop resume can move the clock back. The anchor must follow, or the
    // heartbeat stalls for the length of the jump and the server ages the reporter out.
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

    // An enter queued before the leave describes a visit that is over; firing it would re-assert
    // presence in an instance the character left.
    [Fact]
    public void A_leave_cancels_a_stale_pending_enter()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyEntered(T0);
        scheduler.NotifyLeft(T0);

        Assert.Equal(OccultTrigger.Leave, scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(5)));
    }

    // Leave then enter is a real itinerary: from instance A to instance B.
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

    // A wait for sync to resume holds the tracker until syncing comes back on, and no longer; see
    // RetryPolicy.EndsWhenSyncResumes.
    [Fact]
    public void A_wait_for_sync_to_resume_ends_when_sync_resumes()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));

        scheduler.SyncResumed();

        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    // With no word that syncing is back on, the wait still ends when the server said it would.
    [Fact]
    public void A_wait_for_sync_to_resume_still_ends_at_its_deadline()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(59)));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromHours(1)));
    }

    // Syncing coming back on says nothing about load, so a rate limit in force outlasts it.
    [Fact]
    public void Sync_resuming_leaves_a_rate_limit_in_force()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1));

        scheduler.SyncResumed();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(4)));
        Assert.Equal(OccultTrigger.Change, scheduler.Poll(T0 + TimeSpan.FromMinutes(5)));
    }

    // A second refusal never shortens the wait the first one set.
    [Fact]
    public void A_wait_for_sync_to_resume_is_never_shortened()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.NotifyChanged(T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1));
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromMinutes(10));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(30)));
    }

    // A wait for sync to resume answers the token, not the character, so relogging keeps it.
    [Fact]
    public void Reset_keeps_a_wait_for_sync_to_resume()
    {
        var scheduler = new OccultUploadScheduler();
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1));
        scheduler.Reset();

        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(10));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    // --- Config --------------------------------------------------------------------------

    // 90 sits strictly inside the clamp range, so only an honored config value passes.
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

    // A zero or negative cadence from a misconfigured or hostile server must not become a request
    // flood; the backend URL is user-overridable.
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

    // An enormous interval would break presence: the server ages a reporter out after about three
    // missed heartbeats.
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

    // The backoff answers the token, not the character, so relogging must not shake off a rate limit.
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
