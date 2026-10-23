using System;
using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// A "Sync now" press made while the website has syncing off: the plugin asks /config once, and
// syncs if the answer says syncing is back on. Polls are numbered as they start.
public class PausedPressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_press_asks_for_a_poll_at_once()
    {
        var press = new PausedPress();
        press.Press(pollsStarted: 3);

        Assert.True(press.PollDue(pollsStarted: 3, pollInFlight: false, T0));
    }

    [Fact]
    public void Nothing_is_due_without_a_press()
    {
        Assert.False(new PausedPress().PollDue(3, false, T0));
    }

    // Once a poll has started after the press, that poll answers it: one poll per press.
    [Fact]
    public void A_press_asks_for_one_poll()
    {
        var press = new PausedPress();
        press.Press(3);
        press.PollStarted(T0);

        Assert.False(press.PollDue(pollsStarted: 4, pollInFlight: true, T0));
        Assert.False(press.PollDue(pollsStarted: 4, pollInFlight: false, T0.AddMinutes(1)));
    }

    // A poll that started before the press is still on its way; the press's own poll waits for it.
    [Fact]
    public void A_poll_in_flight_holds_the_press_poll_back()
    {
        var press = new PausedPress();
        press.Press(3);

        Assert.False(press.PollDue(3, pollInFlight: true, T0));
        Assert.True(press.PollDue(3, pollInFlight: false, T0));
    }

    // Presses start at most one poll per floor, however fast the button goes.
    [Fact]
    public void Presses_poll_at_most_once_per_floor()
    {
        var press = new PausedPress();
        press.Press(3);
        press.PollStarted(T0);
        press.Settle(4, syncEnabled: false);

        press.Press(4);

        Assert.False(press.PollDue(4, false, T0 + PausedPress.PollFloor - TimeSpan.FromSeconds(1)));
        Assert.True(press.PollDue(4, false, T0 + PausedPress.PollFloor));
    }

    // A clock that jumps backwards does not hold the press's poll off by however far it jumped.
    [Fact]
    public void A_clock_that_jumps_backwards_does_not_hold_the_press_poll()
    {
        var press = new PausedPress();
        press.Press(3);
        press.PollStarted(T0);
        press.Settle(4, false);
        press.Press(4);

        Assert.True(press.PollDue(4, false, T0.AddHours(-1)));
    }

    [Fact]
    public void An_answer_saying_syncing_is_back_on_sweeps()
    {
        var press = new PausedPress();
        press.Press(3);

        Assert.True(press.Settle(pollNumber: 4, syncEnabled: true));
    }

    // An off answer or no answer still settles the press, so it asks for no further poll.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void An_answer_saying_off_or_no_answer_settles_the_press_without_a_sweep(bool? syncEnabled)
    {
        var press = new PausedPress();
        press.Press(3);

        Assert.False(press.Settle(4, syncEnabled));
        Assert.False(press.PollDue(4, false, T0.AddMinutes(1)));
    }

    // A poll that started before the press answers for the moment before it, so the press waits for
    // its own poll.
    [Fact]
    public void A_poll_started_before_the_press_does_not_answer_it()
    {
        var press = new PausedPress();
        press.Press(3);

        Assert.False(press.Settle(pollNumber: 3, syncEnabled: true));
        Assert.True(press.PollDue(3, false, T0));
    }

    // A second press while the first's poll is on its way waits for a poll of its own, after the
    // floor: the player may have turned syncing back on in between.
    [Fact]
    public void A_later_press_waits_for_a_poll_started_after_it()
    {
        var press = new PausedPress();
        press.Press(3);
        press.PollStarted(T0);

        press.Press(4);

        Assert.False(press.Settle(4, syncEnabled: false));
        Assert.True(press.PollDue(4, false, T0 + PausedPress.PollFloor));
        Assert.True(press.Settle(5, syncEnabled: true));
    }

    // A press is answered once, so one press queues at most one sweep.
    [Fact]
    public void A_press_is_answered_once()
    {
        var press = new PausedPress();
        press.Press(3);

        Assert.True(press.Settle(4, true));
        Assert.False(press.Settle(5, true));
    }
}
