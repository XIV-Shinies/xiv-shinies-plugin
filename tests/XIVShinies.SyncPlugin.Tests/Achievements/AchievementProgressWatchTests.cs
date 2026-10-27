using Xunit;
using XIVShinies.SyncPlugin.Achievements;

namespace XIVShinies.SyncPlugin.Tests.Achievements;

// The game keeps one progress reading and rewrites it when the server answers. The watch sees the
// fields every frame and says when they hold an answer it has not taken yet.
public class AchievementProgressWatchTests
{
    private static ProgressFields Loaded(uint id, uint current, uint max) =>
        new(ProgressRequestState.Loaded, id, current, max);

    // A request leaves the previous answer's values in place.
    private static ProgressFields Requested(uint id, uint current, uint max) =>
        new(ProgressRequestState.Requested, id, current, max);

    private static AchievementProgressWatch Baselined(ProgressFields fields)
    {
        var watch = new AchievementProgressWatch();
        Assert.Null(watch.TakeNewAnswer(fields));
        return watch;
    }

    // At load and at each session edge the fields may hold an answer from before, or another
    // character's, so the first look only learns what is there.
    [Fact]
    public void The_first_look_is_a_baseline_never_an_answer()
    {
        var watch = new AchievementProgressWatch();

        Assert.Null(watch.TakeNewAnswer(Loaded(3, 750, 1000)));
    }

    [Fact]
    public void An_answer_landing_after_a_request_is_taken()
    {
        var watch = Baselined(new ProgressFields(ProgressRequestState.Invalid, 0, 0, 0));

        Assert.Null(watch.TakeNewAnswer(Requested(0, 0, 0)));

        Assert.Equal(Loaded(3, 750, 1000), watch.TakeNewAnswer(Loaded(3, 750, 1000)));
    }

    // While the request waits, the values are the previous answer's, not the one asked for.
    [Fact]
    public void Nothing_is_taken_while_a_request_waits()
    {
        var watch = Baselined(Loaded(3, 750, 1000));

        Assert.Null(watch.TakeNewAnswer(Requested(3, 750, 1000)));
        Assert.Null(watch.TakeNewAnswer(Requested(3, 750, 1000)));
    }

    // The fields are read every frame and stay loaded; the answer is one observation, not one per frame.
    [Fact]
    public void An_answer_is_taken_once()
    {
        var watch = Baselined(Requested(0, 0, 0));

        Assert.NotNull(watch.TakeNewAnswer(Loaded(3, 750, 1000)));
        Assert.Null(watch.TakeNewAnswer(Loaded(3, 750, 1000)));
    }

    // Opening the same achievement again asks again, and its answer is a newer observation even when
    // the count has not moved.
    [Fact]
    public void The_same_values_answered_again_after_a_request_are_taken_again()
    {
        var watch = Baselined(Loaded(3, 750, 1000));

        watch.TakeNewAnswer(Requested(3, 750, 1000));

        Assert.Equal(Loaded(3, 750, 1000), watch.TakeNewAnswer(Loaded(3, 750, 1000)));
    }

    // The game can write a new answer without a request the watch saw in between.
    [Fact]
    public void New_values_while_loaded_are_taken()
    {
        var watch = Baselined(Loaded(3, 750, 1000));

        Assert.Equal(Loaded(4, 2, 5), watch.TakeNewAnswer(Loaded(4, 2, 5)));
    }

    // An answer the caller dropped, such as one the Party Finder asked for with the Achievements
    // window closed, must not come back as new when the window opens later.
    [Fact]
    public void An_answer_already_seen_is_not_taken_again_later()
    {
        var watch = Baselined(Requested(0, 0, 0));
        watch.TakeNewAnswer(Loaded(3811, 0, 1));

        Assert.Null(watch.TakeNewAnswer(Loaded(3811, 0, 1)));
    }

    [Fact]
    public void After_a_reset_the_next_look_is_a_baseline_again()
    {
        var watch = Baselined(Requested(0, 0, 0));

        watch.Reset();

        Assert.Null(watch.TakeNewAnswer(Loaded(3, 750, 1000)));
    }

    [Fact]
    public void An_answer_that_failed_to_load_is_not_taken()
    {
        var watch = Baselined(Requested(3, 0, 0));

        Assert.Null(watch.TakeNewAnswer(new ProgressFields(ProgressRequestState.Invalid, 3, 0, 0)));
    }
}
