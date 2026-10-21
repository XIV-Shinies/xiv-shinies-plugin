using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Decides *when* to upload, never *how*. No clock, no game, no network: every method takes the
// current time as an argument, so these tests run instantly instead of sleeping.
public class SyncSchedulerTests
{
    // An arbitrary fixed instant. Tests move time by adding to it, never by waiting.
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SyncScheduler NewScheduler() => new();

    [Fact]
    public void A_fresh_scheduler_has_nothing_to_do()
    {
        Assert.Null(NewScheduler().Poll(T0));
    }

    [Fact]
    public void A_login_request_becomes_due_immediately_and_sweeps_every_category()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        var due = scheduler.Poll(T0);

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Login, due.Trigger);
        // A null category set means "collect everything the user has enabled".
        Assert.Null(due.Categories);
    }

    // Poll hands the work out exactly once. Without this the caller would upload in a tight loop.
    [Fact]
    public void Polling_consumes_the_pending_work()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        Assert.NotNull(scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0));
    }

    [Fact]
    public void An_unlock_waits_for_the_debounce_window_to_close()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        // The burst may still be arriving; uploading now would send an incomplete picture.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(4)));

        var due = scheduler.Poll(T0 + TimeSpan.FromSeconds(5));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Unlock, due.Trigger);
    }

    // A quest turn-in can fire several unlocks a second apart. Each one restarts the window so the
    // whole burst rides in a single upload.
    [Fact]
    public void A_second_unlock_restarts_the_debounce_window()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.NotifyUnlock(CategoryKeys.Minions, T0 + TimeSpan.FromSeconds(3));

        // 5s after the first unlock, but only 2s after the second.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(5)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromSeconds(8)));
    }

    // Every category that genuinely unlocked during the burst rides along, and nothing else does.
    // Each one really was acquired just now, so dating them all is correct.
    [Fact]
    public void An_unlock_upload_names_only_the_categories_that_unlocked()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.NotifyUnlock(CategoryKeys.Minions, T0);
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0); // duplicate: coalesced, not repeated

        var due = scheduler.Poll(T0 + TimeSpan.FromSeconds(5));

        Assert.NotNull(due);
        Assert.Equal<IEnumerable<string>>(
            new SortedSet<string> { CategoryKeys.Minions, CategoryKeys.Mounts },
            new SortedSet<string>(due.Categories!));
    }

    [Fact]
    public void The_interval_sweep_comes_due_once_the_full_sync_period_has_elapsed()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(SyncTrigger.Login, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(29)));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
        Assert.Null(due.Categories);
    }

    // The interval clock is anchored when a sweep is ISSUED, not when its upload succeeds. Otherwise
    // one transient 500 during the login sweep would leave the clock unset — and since only a full
    // sweep ever sets it, periodic syncing would be silently dead for the rest of the session.
    [Fact]
    public void A_login_sweep_whose_upload_never_succeeds_still_starts_the_interval_clock()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        // The sweep is handed out, then its upload fails: MarkUploaded is never called.
        Assert.Equal(SyncTrigger.Login, scheduler.Poll(T0)!.Trigger);

        // Syncing must recover on its own at the next interval rather than stopping forever.
        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
    }

    // An unlock upload carried one category, so it is not a full sweep and must not pretend to be
    // one. Letting it reset the clock would let a steadily-unlocking player never sweep at all.
    [Fact]
    public void An_unlock_upload_does_not_postpone_the_interval_sweep()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(SyncTrigger.Login, T0);
        scheduler.MarkUploaded(SyncTrigger.Unlock, T0 + TimeSpan.FromMinutes(29));

        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Nothing_is_due_while_the_server_has_told_us_to_wait()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(4)));
    }

    // What the sync card asks before offering "Sync now": true from the server's instruction until the
    // moment it expires, and never before one arrives.
    [Fact]
    public void The_scheduler_reports_a_wait_until_it_expires()
    {
        var scheduler = NewScheduler();
        Assert.False(scheduler.IsBackingOff(T0));

        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        Assert.True(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(4)));
        Assert.False(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(5)));
    }

    // Asking is read-only: it must not end the wait the way handing out due work does.
    [Fact]
    public void Asking_about_a_wait_does_not_end_it()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        Assert.False(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(6)));
        Assert.True(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(4)));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(4)));
    }

    // Backoff defers work; it never throws it away. A 429 during a login sync must not cost the user
    // that sync entirely.
    [Fact]
    public void Work_deferred_by_a_backoff_survives_it()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(5));

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Login, due.Trigger);
    }

    // A wait the website's switch can end holds work like any other until the switch comes back on.
    [Fact]
    public void A_wait_for_sync_to_resume_holds_work_until_sync_resumes()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);

        Assert.True(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(1)));
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));

        scheduler.SyncResumed();

        Assert.False(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(1)));
        Assert.Equal(SyncTrigger.Login, scheduler.Poll(T0 + TimeSpan.FromMinutes(1))?.Trigger);
    }

    // With no word that syncing is back on, the wait still ends when the server said it would.
    [Fact]
    public void A_wait_for_sync_to_resume_still_ends_at_its_deadline()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(59)));
        Assert.False(scheduler.IsBackingOff(T0 + TimeSpan.FromHours(1)));
        Assert.Equal(SyncTrigger.Login, scheduler.Poll(T0 + TimeSpan.FromHours(1))?.Trigger);
    }

    // Syncing coming back on says nothing about load, so a rate limit in force outlasts it.
    [Fact]
    public void Sync_resuming_leaves_a_rate_limit_in_force()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);

        scheduler.SyncResumed();

        Assert.True(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(4)));
        Assert.False(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(5)));
    }

    // A second refusal never shortens the wait the first one set.
    [Fact]
    public void A_wait_for_sync_to_resume_is_never_shortened()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromMinutes(10), SyncTrigger.Interval);

        Assert.True(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(30)));
    }

    // The refused upload was handed out and settled, so an early end sends it again: a login or
    // manual sweep as itself.
    [Theory]
    [InlineData(SyncTrigger.Login)]
    [InlineData(SyncTrigger.Manual)]
    public void Sync_resuming_sends_a_refused_sweep_again(SyncTrigger refused)
    {
        var scheduler = NewScheduler();
        scheduler.Request(refused, T0);
        Assert.Equal(refused, scheduler.Poll(T0)?.Trigger);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), refused);

        scheduler.SyncResumed();

        Assert.Equal(refused, scheduler.Poll(T0 + TimeSpan.FromMinutes(2))?.Trigger);
    }

    // A refused interval or unlock upload brings the periodic sweep forward, which covers every
    // category an unlock upload would have named.
    [Theory]
    [InlineData(SyncTrigger.Interval)]
    [InlineData(SyncTrigger.Unlock)]
    public void Sync_resuming_after_a_refused_partial_upload_sweeps_at_once(SyncTrigger refused)
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.Poll(T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), refused);

        scheduler.SyncResumed();

        Assert.Equal(SyncTrigger.Interval, scheduler.Poll(T0 + TimeSpan.FromMinutes(2))?.Trigger);
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(3)));
    }

    // Two refusals owe the stronger upload: a login sweep outranks an interval one.
    [Fact]
    public void Sync_resuming_sends_the_stronger_of_two_refused_uploads()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Login);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);

        scheduler.SyncResumed();

        Assert.Equal(SyncTrigger.Login, scheduler.Poll(T0 + TimeSpan.FromMinutes(2))?.Trigger);
    }

    // A wait that runs out on its own owes nothing: the next upload goes on its own schedule, and a
    // later refusal's early end sends only what that refusal owed.
    [Fact]
    public void A_wait_that_runs_out_owes_no_upload()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Manual);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromHours(1)));

        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(2), SyncTrigger.Unlock);
        scheduler.SyncResumed();

        Assert.Equal(SyncTrigger.Interval, scheduler.Poll(T0 + TimeSpan.FromHours(1.1))?.Trigger);
    }

    // A press that resumes syncing queues its own sweep, which covers the refused upload: one sweep,
    // not two, whichever reaches the scheduler first.
    [Fact]
    public void A_requested_sweep_covers_the_upload_owed_on_resume()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.Poll(T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);

        scheduler.Request(SyncTrigger.Manual, T0 + TimeSpan.FromMinutes(2));
        scheduler.SyncResumed();

        Assert.Equal(SyncTrigger.Manual, scheduler.Poll(T0 + TimeSpan.FromMinutes(2))?.Trigger);
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void A_sweep_owed_on_resume_covers_a_requested_sweep_that_arrives_after_it()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.Poll(T0);
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);
        var answeredAt = T0 + TimeSpan.FromMinutes(2);

        scheduler.SyncResumed();
        Assert.Equal(SyncTrigger.Interval, scheduler.Poll(answeredAt)?.Trigger);

        scheduler.RequestUnlessSweptSince(SyncTrigger.Manual, answeredAt, answeredAt.AddSeconds(1));

        Assert.Null(scheduler.Poll(answeredAt.AddSeconds(2)));
    }

    // With no sweep since its reason, the request goes like any other.
    [Fact]
    public void A_request_with_no_sweep_since_its_reason_is_queued()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.Poll(T0);

        scheduler.RequestUnlessSweptSince(SyncTrigger.Manual, T0.AddMinutes(1), T0.AddMinutes(2));

        Assert.Equal(SyncTrigger.Manual, scheduler.Poll(T0.AddMinutes(2))?.Trigger);
    }

    // The user pressing "Sync now" is the strongest signal there is, so it outranks a queued unlock.
    // It still waits out a backoff: a button must not become a way to hammer a server that said stop.
    [Fact]
    public void A_manual_request_outranks_a_pending_unlock()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Manual, T0 + TimeSpan.FromSeconds(6));

        var due = scheduler.Poll(T0 + TimeSpan.FromSeconds(6));

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Manual, due.Trigger);
        Assert.Null(due.Categories);
    }

    // A full sweep covers everything an unlock would have sent, so the queued unlock is redundant.
    [Fact]
    public void A_full_sweep_clears_a_pending_unlock()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Manual, T0);

        Assert.Equal(SyncTrigger.Manual, scheduler.Poll(T0)!.Trigger);
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Intervals_from_the_server_replace_the_defaults()
    {
        var scheduler = NewScheduler();
        scheduler.ApplyIntervals(new ConfigIntervals {FullSyncMinutes = 10, UnlockDebounceSeconds = 20});
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(19)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromSeconds(20)));
    }

    // A misconfigured or hostile /config must not turn the plugin into a request flood, nor stall it
    // forever. The plugin honors the server's cadence only within sane bounds.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Absurd_intervals_are_clamped_rather_than_obeyed(int fullSyncMinutes, int debounceSeconds)
    {
        var scheduler = NewScheduler();
        scheduler.ApplyIntervals(new ConfigIntervals
        {
            FullSyncMinutes = fullSyncMinutes,
            UnlockDebounceSeconds = debounceSeconds,
        });

        Assert.InRange(scheduler.UnlockDebounce, SyncScheduler.MinUnlockDebounce, SyncScheduler.MaxUnlockDebounce);
        Assert.InRange(scheduler.FullSyncInterval, SyncScheduler.MinFullSyncInterval, SyncScheduler.MaxFullSyncInterval);
    }

    // Logging out must discard queued work. An unlock earned on one character, uploaded after
    // switching to another, would file that character's mount under the wrong ContentId hash.
    [Fact]
    public void Reset_discards_queued_work()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Login, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    // A backoff is the server talking to the token, not to the character. Switching characters must
    // not be a way to shake off a 429.
    [Fact]
    public void Reset_keeps_a_backoff_in_force()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        scheduler.Reset();
        scheduler.Request(SyncTrigger.Login, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(5)));
    }

    // Like a backoff, a wait for sync to resume is about the token, so switching characters keeps
    // it; the refused upload it owed was the previous character's, so that is forgotten.
    [Fact]
    public void Reset_keeps_a_wait_for_sync_to_resume_but_forgets_the_upload_it_owed()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Manual);

        scheduler.Reset();

        Assert.True(scheduler.IsBackingOff(T0 + TimeSpan.FromMinutes(1)));

        scheduler.SyncResumed();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(2)));
    }

    // Reset also forgets when the last sweep happened, so the next login sweeps immediately rather
    // than waiting out an interval measured against the previous character.
    [Fact]
    public void Reset_does_not_leave_an_interval_pending_from_the_previous_character()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(SyncTrigger.Login, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromHours(1)));
    }

    // Clocks move backwards: NTP corrections, and a laptop resuming from sleep. A negative elapsed
    // time must not make a debounce window that never closes.
    [Fact]
    public void A_clock_that_jumps_backwards_still_lets_the_debounce_close()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        // The clock rewinds an hour, then advances past the window from there.
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0 - TimeSpan.FromHours(1));

        Assert.NotNull(scheduler.Poll(T0));
    }
}
