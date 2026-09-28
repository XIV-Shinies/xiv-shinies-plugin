using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// What the live tracker does with each answer to one of its uploads; OccultOutcomePolicy's remarks
// explain the check order that settles the overlapping statuses.
public class OccultOutcomePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApiStatus.Ok, OccultOutcomeKind.Applied)]
    [InlineData(ApiStatus.RateLimited, OccultOutcomeKind.BackOff)]
    [InlineData(ApiStatus.SyncDisabled, OccultOutcomeKind.BackOff)]
    [InlineData(ApiStatus.NotConfigured, OccultOutcomeKind.Skip)]
    [InlineData(ApiStatus.InvalidToken, OccultOutcomeKind.Halt)]
    [InlineData(ApiStatus.CharacterNotClaimed, OccultOutcomeKind.Halt)]
    [InlineData(ApiStatus.CharacterNotVerified, OccultOutcomeKind.Halt)]
    [InlineData(ApiStatus.CharacterAmbiguous, OccultOutcomeKind.Halt)]
    [InlineData(ApiStatus.CharacterBoundElsewhere, OccultOutcomeKind.Halt)]
    [InlineData(ApiStatus.InvalidPayload, OccultOutcomeKind.Pause)]
    [InlineData(ApiStatus.MethodNotAllowed, OccultOutcomeKind.Pause)]
    [InlineData(ApiStatus.PayloadTooLarge, OccultOutcomeKind.Pause)]
    [InlineData(ApiStatus.ServerError, OccultOutcomeKind.Transient)]
    [InlineData(ApiStatus.NetworkError, OccultOutcomeKind.Transient)]
    [InlineData(ApiStatus.Unknown, OccultOutcomeKind.Transient)]
    public void Each_status_gets_its_outcome(ApiStatus status, OccultOutcomeKind expected)
    {
        Assert.Equal(expected, OccultOutcomePolicy.Classify(status, retryAfter: null, Now).Kind);
    }

    // Every status, not the hand-picked list above: the tracker halts exactly where the sync path
    // would stop for the user, except where the client itself refused to send.
    [Fact]
    public void The_tracker_halts_exactly_on_the_user_action_statuses_it_sent()
    {
        // Enum.GetValues<T>() returns every member of the enum as an array, as Object.values does
        // for a TS string enum.
        foreach (var status in Enum.GetValues<ApiStatus>())
        {
            var halts = OccultOutcomePolicy.Classify(status, retryAfter: null, Now).Kind
                == OccultOutcomeKind.Halt;

            Assert.Equal(
                RetryPolicy.RequiresUserAction(status) && status != ApiStatus.NotConfigured, halts);
        }
    }

    // A server's own wait is honored exactly; without one, the shared defaults apply.
    [Fact]
    public void A_back_off_waits_for_the_servers_retry_after()
    {
        var outcome = OccultOutcomePolicy.Classify(ApiStatus.RateLimited, TimeSpan.FromSeconds(90), Now);

        Assert.Equal(Now.AddSeconds(90), outcome.Until);
    }

    [Fact]
    public void A_terminal_rejection_pauses_for_the_fixed_quiet_period()
    {
        var outcome = OccultOutcomePolicy.Classify(ApiStatus.InvalidPayload, retryAfter: null, Now);

        Assert.Equal(Now + OccultOutcomePolicy.TerminalRejectionBackoff, outcome.Until);
    }

    // Only the two waiting outcomes carry a time; the rest are decided without one.
    [Theory]
    [InlineData(ApiStatus.Ok)]
    [InlineData(ApiStatus.NotConfigured)]
    [InlineData(ApiStatus.CharacterNotVerified)]
    [InlineData(ApiStatus.NetworkError)]
    public void Outcomes_that_do_not_wait_carry_no_time(ApiStatus status)
    {
        Assert.Null(OccultOutcomePolicy.Classify(status, retryAfter: null, Now).Until);
    }
}
