using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Which failed uploads are worth a line. An outage lasts for hours and the sweep comes round every
// few minutes, so the same status arrives over and over; but an outage that ends and returns is two
// separate events, and the second one has to be reportable.
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

    // A network blip and a 500 are different things to a reader, so being quiet about one must not
    // make the plugin quiet about the other.
    [Fact]
    public void A_different_outcome_is_reported_on_its_own_terms()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.ShouldReport(ApiStatus.ServerError);

        Assert.True(outcomes.ShouldReport(ApiStatus.NetworkError));
    }

    // The reason a plain latch is not enough. A server that fails, recovers, and fails again has
    // genuinely failed twice, and the second failure is news — without this the plugin would go
    // silent for the rest of the session after the first one.
    [Fact]
    public void An_outcome_that_returns_after_a_success_is_reported_again()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.ShouldReport(ApiStatus.ServerError);
        outcomes.NoteSuccess();

        Assert.True(outcomes.ShouldReport(ApiStatus.ServerError));
    }

    // A success clears everything, not just the status that happened to arrive last: an upload that
    // worked says the whole previous run of failures is over.
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

    // Success is not itself an outcome anyone reports, so noting one twice must not become a way to
    // make the next failure unreportable.
    [Fact]
    public void Repeated_successes_leave_the_next_failure_reportable()
    {
        var outcomes = new SyncOutcomeWarnings();

        outcomes.NoteSuccess();
        outcomes.NoteSuccess();

        Assert.True(outcomes.ShouldReport(ApiStatus.ServerError));
    }
}
