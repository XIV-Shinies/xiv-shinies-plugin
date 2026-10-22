using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Which failed uploads are worth a line. An outage repeats one status every few minutes for hours,
// but an outage that ends and returns is two events, and the second must be reported.
public class SyncOutcomeWarningsTests
{
    [Fact]
    public void An_outcome_not_seen_before_is_reported()
    {
        var outcomes = new SyncOutcomeWarnings();

        Assert.True(outcomes.ShouldReport(ApiStatus.ServerError));
    }

    [Fact]
    public void The_same_outcome_again_is_not_reported()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.ShouldReport(ApiStatus.ServerError);

        Assert.False(outcomes.ShouldReport(ApiStatus.ServerError));
    }

    // A network blip and a 500 are different news; silence about one must not cover the other.
    [Fact]
    public void A_different_outcome_is_reported_on_its_own_terms()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.ShouldReport(ApiStatus.ServerError);

        Assert.True(outcomes.ShouldReport(ApiStatus.NetworkError));
    }

    // Why a plain latch is not enough: after the first failure it would stay silent all session.
    [Fact]
    public void An_outcome_that_returns_after_a_success_is_reported_again()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.ShouldReport(ApiStatus.ServerError);
        outcomes.NoteSuccess();

        Assert.True(outcomes.ShouldReport(ApiStatus.ServerError));
    }

    // An upload that worked ends the whole run of failures, not just the last status.
    [Fact]
    public void A_success_clears_every_remembered_outcome()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.ShouldReport(ApiStatus.ServerError);
        outcomes.ShouldReport(ApiStatus.NetworkError);
        outcomes.NoteSuccess();

        Assert.True(outcomes.ShouldReport(ApiStatus.ServerError));
        Assert.True(outcomes.ShouldReport(ApiStatus.NetworkError));
    }

    // Success is not a reported outcome, so noting it twice must not silence the next failure.
    [Fact]
    public void Repeated_successes_leave_the_next_failure_reportable()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.NoteSuccess();
        outcomes.NoteSuccess();

        Assert.True(outcomes.ShouldReport(ApiStatus.ServerError));
    }
}
