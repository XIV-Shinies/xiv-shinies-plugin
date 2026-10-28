using System;
using Xunit;
using XIVShinies.SyncPlugin.Achievements;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Achievements;

// One look a frame at the game's progress fields: whether an answer landed, whether it counts, and what
// is held for the next sync.
public class AchievementProgressCaptureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProgressFields Waiting = new(ProgressRequestState.Requested, 0, 0, 0);

    private static ProgressFields Loaded(uint id, uint current, uint max) =>
        new(ProgressRequestState.Loaded, id, current, max);

    private static AchievementProgressCapture Baselined()
    {
        var capture = new AchievementProgressCapture();
        capture.Look(Waiting, () => true, T0);
        return capture;
    }

    // The window lookup costs more than the four field reads, so it waits for an answer.
    [Fact]
    public void Whether_an_answer_counts_is_asked_only_when_one_lands()
    {
        var capture = Baselined();
        var asked = 0;

        capture.Look(Waiting, () => { asked++; return true; }, T0);
        Assert.Equal(0, asked);

        capture.Look(Loaded(3, 750, 1000), () => { asked++; return true; }, T0);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void An_answer_that_counts_is_held_with_the_time_it_was_seen()
    {
        var capture = Baselined();

        var outcome = capture.Look(Loaded(3, 750, 1000), () => true, T0.AddSeconds(2));

        Assert.Equal(ProgressLook.Taken, outcome.Look);
        var reading = Assert.Single(capture.Readings());
        var expected = new AchievementProgressReading
        {
            Id = 3, Current = 750, Max = 1000, ObservedAt = T0.AddSeconds(2),
        };
        Assert.Equal(expected, reading);
    }

    // The game's own login answer usually lands with the window shut; opening it later must not take it.
    [Fact]
    public void An_answer_that_does_not_count_is_never_held_even_once_it_would()
    {
        var capture = Baselined();

        Assert.Equal(ProgressLook.NotCounted, capture.Look(Loaded(3811, 0, 1), () => false, T0).Look);
        Assert.Equal(ProgressLook.NoAnswer, capture.Look(Loaded(3811, 0, 1), () => true, T0).Look);

        Assert.Empty(capture.Readings());
    }

    [Fact]
    public void An_answer_outside_the_contract_is_not_held()
    {
        var capture = Baselined();

        var outcome = capture.Look(Loaded(3, 1, 0), () => true, T0);

        Assert.Equal(ProgressLook.OutsideContract, outcome.Look);
        Assert.Equal(Loaded(3, 1, 0), outcome.Answer);
        Assert.Empty(capture.Readings());
    }

    // At a session edge, or while the collection is off, nothing taken before may carry over.
    [Fact]
    public void Forgetting_drops_the_readings_and_makes_the_next_look_a_baseline()
    {
        var capture = Baselined();
        capture.Look(Loaded(3, 750, 1000), () => true, T0);

        capture.Forget();

        Assert.Empty(capture.Readings());
        Assert.Equal(ProgressLook.NoAnswer, capture.Look(Loaded(4, 2, 5), () => true, T0).Look);
    }
}
