using System;
using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// One upload path's wait after a 503 saying syncing is switched off, and when the website's
// /config may end it early. Polls are numbered as they start.
public class SyncResumeWaitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

    // A refusal at T0, when three polls had started, with an hour's wait.
    private static SyncResumeWait Armed()
    {
        var wait = new SyncResumeWait();
        Assert.True(wait.Arm(pollsStarted: 3, T0, T0.AddHours(1)));
        return wait;
    }

    [Fact]
    public void A_later_poll_saying_the_switch_is_on_ends_the_wait()
    {
        Assert.True(Armed().TryEnd(answeredPoll: 4, switchOn: true, T0.AddMinutes(2)));
    }

    // A poll already on its way when the refusal arrived may carry the answer from before the
    // switch went off.
    [Fact]
    public void A_poll_started_before_the_refusal_does_not_end_it()
    {
        Assert.False(Armed().TryEnd(answeredPoll: 3, switchOn: true, T0.AddMinutes(2)));
    }

    [Fact]
    public void A_poll_saying_the_switch_is_still_off_does_not_end_it()
    {
        Assert.False(Armed().TryEnd(answeredPoll: 4, switchOn: false, T0.AddMinutes(2)));
    }

    // Ending is a one-time event, so the caller's "back on" step runs once.
    [Fact]
    public void The_wait_ends_once()
    {
        var wait = Armed();

        Assert.True(wait.TryEnd(4, true, T0.AddMinutes(2)));
        Assert.False(wait.TryEnd(5, true, T0.AddMinutes(4)));
    }

    [Fact]
    public void Nothing_ends_without_a_refusal()
    {
        Assert.False(new SyncResumeWait().TryEnd(4, true, T0));
    }

    // A second refusal raises the bar to the polls started by then.
    [Fact]
    public void A_later_refusal_waits_for_a_poll_started_after_it()
    {
        var wait = Armed();
        Assert.True(wait.Arm(pollsStarted: 5, T0.AddMinutes(1), T0.AddHours(1)));

        Assert.False(wait.TryEnd(5, true, T0.AddMinutes(2)));
        Assert.True(wait.TryEnd(6, true, T0.AddMinutes(4)));
    }

    // A wait that ran its course needs nothing ending, so the caller does not announce a resume.
    [Fact]
    public void A_wait_that_has_run_out_is_not_ended()
    {
        Assert.False(Armed().TryEnd(4, true, T0.AddHours(1)));
    }

    // A refusal soon after an early end waits in full. Otherwise a website whose /config says
    // syncing is on while its uploads say it is off would turn each hour's wait into a few minutes.
    [Fact]
    public void A_refusal_inside_a_wait_that_ended_early_waits_its_whole_wait()
    {
        var wait = Armed();
        wait.TryEnd(4, true, T0.AddMinutes(2));

        Assert.False(wait.Arm(pollsStarted: 4, T0.AddMinutes(3), T0.AddMinutes(63)));
        Assert.False(wait.TryEnd(5, true, T0.AddMinutes(5)));
    }

    // An accepted upload shows the early end was right, so a player who switches syncing off again
    // within the hour resumes as soon as it is back on.
    [Fact]
    public void An_accepted_upload_lets_the_next_refusal_end_early_again()
    {
        var wait = Armed();
        wait.TryEnd(4, true, T0.AddMinutes(2));
        wait.UploadAccepted();

        Assert.True(wait.Arm(pollsStarted: 6, T0.AddMinutes(10), T0.AddMinutes(70)));
        Assert.True(wait.TryEnd(7, true, T0.AddMinutes(12)));
    }

    // Once the wait that ended early would have run out anyway, a new refusal may end early again.
    [Fact]
    public void A_refusal_after_that_wait_would_have_ended_may_end_early_again()
    {
        var wait = Armed();
        wait.TryEnd(4, true, T0.AddMinutes(2));

        Assert.True(wait.Arm(pollsStarted: 8, T0.AddHours(1), T0.AddHours(2)));
        Assert.True(wait.TryEnd(9, true, T0.AddHours(1).AddMinutes(2)));
    }
}
