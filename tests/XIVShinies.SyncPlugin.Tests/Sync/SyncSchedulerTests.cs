using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Decides when to upload, never how.
public class SyncSchedulerTests
{
    // Every scheduler method takes the time as an argument; tests move it by adding to this instant.
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

    // Poll hands out each piece of work once, or the caller would upload in a tight loop.
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

    // A quest turn-in can fire several unlocks a second apart. Restarting the window sends the whole
    // burst in one upload.
    [Fact]
    public void A_second_unlock_restarts_the_debounce_window()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.NotifyUnlock(CategoryKeys.Minions, T0 + TimeSpan.FromSeconds(3));

        // Five seconds after the first unlock, but two after the second.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(5)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromSeconds(8)));
    }

    // Each named category was acquired just now, so the upload dating them all is correct.
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
        scheduler.MarkUploaded(new SyncDue { Trigger = SyncTrigger.Login }, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(29)));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
        Assert.Null(due.Categories);
    }

    // The interval clock starts when a sweep is issued, not when its upload succeeds. Only a full
    // sweep sets it, so one failed login upload would otherwise stop periodic syncing for the session.
    [Fact]
    public void A_login_sweep_whose_upload_never_succeeds_still_starts_the_interval_clock()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        // Handed out, then the upload fails: MarkUploaded is never called.
        Assert.Equal(SyncTrigger.Login, scheduler.Poll(T0)!.Trigger);

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
    }

    // An unlock upload is not a full sweep. If it reset the clock, a player who unlocks steadily
    // would never sweep.
    [Fact]
    public void An_unlock_upload_does_not_postpone_the_interval_sweep()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(new SyncDue { Trigger = SyncTrigger.Login }, T0);
        var unlock = new SyncDue
        {
            Trigger = SyncTrigger.Unlock,
            Categories = new HashSet<string> { CategoryKeys.Mounts },
        };
        scheduler.MarkUploaded(unlock, T0 + TimeSpan.FromMinutes(29));

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

    // The sync card asks this before offering "Sync now".
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

    // A 429 during a login sync defers it; it must not cost the user that sync.
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

    // A wait for sync to resume holds work like any backoff until syncing comes back on.
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

    // The refused upload was already handed out, so an early end sends it again. A login or manual
    // sweep goes as itself.
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

    // A press that resumes syncing queues its own sweep, which covers the refused upload, so one sweep
    // goes, not two. The next test covers the other order.
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

    // "Sync now" is the strongest signal, so it outranks a queued unlock. It still waits out a
    // backoff, so a button cannot hammer a server that said stop.
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

    // A misconfigured or hostile /config must not make the plugin flood requests or stall forever.
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

    // An unlock earned on one character and uploaded after a switch would file it under the wrong
    // character's ContentId hash.
    [Fact]
    public void Reset_discards_queued_work()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Login, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    // A backoff applies to the token, not the character, so switching characters must not shake off
    // a 429.
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

    // A wait for sync to resume applies to the token, so switching characters keeps it. The upload
    // it owed was the previous character's, so Reset forgets it.
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

    // The next login sweeps at once rather than waiting out the previous character's interval.
    [Fact]
    public void Reset_does_not_leave_an_interval_pending_from_the_previous_character()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(new SyncDue { Trigger = SyncTrigger.Login }, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromHours(1)));
    }

    // Clocks move backwards, through NTP corrections or a laptop waking from sleep. A negative
    // elapsed time must not hold the debounce window open forever.
    [Fact]
    public void A_clock_that_jumps_backwards_still_lets_the_debounce_close()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        // The clock rewinds an hour, then advances past the window from there.
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0 - TimeSpan.FromHours(1));

        Assert.NotNull(scheduler.Poll(T0));
    }

    // --- Held facts --------------------------------------------------------------------------
    // Facts that can wait ride the next upload, so browsing them spends at most one upload per wait.

    [Fact]
    public void Held_facts_wait_for_another_upload()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);

        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait - TimeSpan.FromSeconds(1)));
    }

    // With nothing else going up, they go on their own once the wait is over, as an interval upload
    // that names only them.
    [Fact]
    public void Held_facts_go_alone_once_the_wait_is_over()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);

        var due = scheduler.Poll(T0 + SyncScheduler.HeldFactsWait);

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
        Assert.Equal(new[] { CategoryKeys.AchievementProgress }, due.Categories!);
        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait + TimeSpan.FromMinutes(1)));
    }

    // Unlike the unlock debounce, the wait does not slide, so steady browsing still uploads at most
    // once per wait.
    [Fact]
    public void More_held_facts_do_not_push_the_wait_out()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0 + TimeSpan.FromMinutes(4));

        Assert.NotNull(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));
    }

    [Fact]
    public void Held_facts_ride_an_unlock_upload()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0 + TimeSpan.FromMinutes(1));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(1) + scheduler.UnlockDebounce);

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Unlock, due.Trigger);
        Assert.Equal<IEnumerable<string>>(
            new SortedSet<string> { CategoryKeys.AchievementProgress, CategoryKeys.Mounts },
            new SortedSet<string>(due.Categories!));
        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));
    }

    // A full sweep carries every category the user has on, held ones included.
    [Fact]
    public void Held_facts_ride_a_requested_sweep()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        scheduler.Request(SyncTrigger.Manual, T0 + TimeSpan.FromMinutes(1));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1))!.Categories);
        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));
    }

    [Fact]
    public void Held_facts_ride_the_periodic_sweep()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(new SyncDue { Trigger = SyncTrigger.Login }, T0);
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0 + TimeSpan.FromMinutes(28));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));

        Assert.Equal(SyncTrigger.Interval, due!.Trigger);
        Assert.Null(due.Categories);
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(33)));
    }

    // Held facts going alone is not a sweep. If it reset the clock, steady browsing would hold every
    // other category back.
    [Fact]
    public void Held_facts_going_alone_do_not_postpone_the_periodic_sweep()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(new SyncDue { Trigger = SyncTrigger.Login }, T0);
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0 + TimeSpan.FromMinutes(20));
        var alone = scheduler.Poll(T0 + TimeSpan.FromMinutes(25))!;

        scheduler.MarkUploaded(alone, T0 + TimeSpan.FromMinutes(25));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.Equal(SyncTrigger.Interval, due!.Trigger);
        Assert.Null(due.Categories);
    }

    [Fact]
    public void Held_facts_wait_out_a_backoff()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(10));
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);

        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(10)));
    }

    // Held facts belong to the character who earned them.
    [Fact]
    public void Reset_discards_held_facts()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromHours(1)));
    }

    // The consent hover and the README promise "within five minutes", and docs/api-contract.md says
    // they go alone after five.
    [Fact]
    public void Held_facts_wait_five_minutes_as_the_copy_promises()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), SyncScheduler.HeldFactsWait);
    }

    // An unlock about to go carries them a few seconds later, which spares an upload.
    [Fact]
    public void Held_facts_due_during_an_unlock_debounce_wait_for_that_unlock()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        var unlockAt = T0 + SyncScheduler.HeldFactsWait - TimeSpan.FromSeconds(2);
        scheduler.NotifyUnlock(CategoryKeys.Mounts, unlockAt);

        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));

        var due = scheduler.Poll(unlockAt + scheduler.UnlockDebounce);
        Assert.Equal(SyncTrigger.Unlock, due!.Trigger);
        Assert.Contains(CategoryKeys.AchievementProgress, due.Categories!);
    }

    // The sweep owed on resume carries every category, so nothing is left to go alone after it.
    [Fact]
    public void Held_facts_ride_the_sweep_owed_when_sync_resumes()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.Poll(T0);
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0 + TimeSpan.FromMinutes(1));
        scheduler.BackOffUntilSyncResumes(T0 + TimeSpan.FromHours(1), SyncTrigger.Interval);

        scheduler.SyncResumed();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(2))!.Categories);
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(10)));
    }

    // Facts held after an upload carried the last ones start a wait of their own.
    [Fact]
    public void Facts_held_after_an_upload_start_a_new_wait()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0 + TimeSpan.FromMinutes(1));
        scheduler.Poll(T0 + TimeSpan.FromMinutes(1) + scheduler.UnlockDebounce);
        var heldAgainAt = T0 + TimeSpan.FromMinutes(3);

        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, heldAgainAt);

        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));
        Assert.NotNull(scheduler.Poll(heldAgainAt + SyncScheduler.HeldFactsWait));
    }

    // Held facts last only the session, so an upload that failed to carry them holds them again rather
    // than leaving them to the next periodic sweep.
    [Fact]
    public void Held_facts_an_upload_failed_to_carry_are_held_again()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        var failed = scheduler.Poll(T0 + SyncScheduler.HeldFactsWait)!;
        var failedAt = T0 + SyncScheduler.HeldFactsWait + TimeSpan.FromSeconds(3);

        scheduler.MarkFailed(failed, failedAt);

        var due = scheduler.Poll(failedAt + SyncScheduler.HeldFactsWait);
        Assert.Equal(new[] { CategoryKeys.AchievementProgress }, due!.Categories!);
    }

    [Fact]
    public void Held_facts_a_failed_sweep_carried_are_held_again()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        scheduler.Request(SyncTrigger.Manual, T0 + TimeSpan.FromMinutes(1));
        var failed = scheduler.Poll(T0 + TimeSpan.FromMinutes(1))!;

        scheduler.MarkFailed(failed, T0 + TimeSpan.FromMinutes(1));

        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(1) + SyncScheduler.HeldFactsWait));
    }

    [Fact]
    public void A_failed_upload_that_carried_no_held_facts_holds_nothing()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Manual, T0);
        var failed = scheduler.Poll(T0)!;

        scheduler.MarkFailed(failed, T0);

        Assert.Null(scheduler.Poll(T0 + SyncScheduler.HeldFactsWait));
    }

    [Fact]
    public void A_clock_that_jumps_backwards_does_not_stretch_the_wait()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyHeld(CategoryKeys.AchievementProgress, T0);
        var rewound = T0 - TimeSpan.FromHours(1);

        Assert.Null(scheduler.Poll(rewound));

        Assert.NotNull(scheduler.Poll(rewound + SyncScheduler.HeldFactsWait));
    }
}
