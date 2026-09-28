using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

/// <summary>
/// How much of a frame's sync work may run — see <see cref="SyncTickPlan"/> for why the ordering
/// lives outside the frame callback.
/// </summary>
public class SyncTickPlanTests
{
    // Which halts leave the poll worth making; SyncTickPlan.PollSurvivesHalt says why. No halt at
    // all is not a refusal.
    [Theory]
    [InlineData(ApiStatus.CharacterNotClaimed, true)]
    [InlineData(ApiStatus.CharacterNotVerified, true)]
    [InlineData(ApiStatus.CharacterAmbiguous, true)]
    [InlineData(ApiStatus.CharacterBoundElsewhere, true)]
    [InlineData(ApiStatus.InvalidToken, false)]
    [InlineData(ApiStatus.NotConfigured, false)]
    [InlineData(null, false)]
    public void A_poll_survives_only_a_character_refusal(ApiStatus? haltStatus, bool expected)
    {
        Assert.Equal(expected, SyncTickPlan.PollSurvivesHalt(haltStatus));
    }

    // The two rules together, as the frame sees them: a character refusal still polls, a token
    // halt stops everything.
    [Theory]
    [InlineData(ApiStatus.CharacterNotVerified, TickAction.PollOnly)]
    [InlineData(ApiStatus.InvalidToken, TickAction.Nothing)]
    public void A_halted_tick_polls_through_a_character_refusal_only(
        ApiStatus haltStatus, TickAction expected)
    {
        Assert.Equal(
            expected,
            SyncTickPlan.Decide(
                canContactServer: true,
                SyncTickPlan.PollSurvivesHalt(haltStatus),
                blockedPendingUserAction: true));
    }

    // Consent outranks everything. A fresh install must make no request at all, whatever else is
    // true — this is the check that keeps it silent.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Without_consent_nothing_runs(bool pollIsWorthwhile, bool blocked)
    {
        Assert.Equal(
            TickAction.Nothing,
            SyncTickPlan.Decide(canContactServer: false, pollIsWorthwhile, blocked));
    }

    [Fact]
    public void A_healthy_tick_does_everything()
    {
        Assert.Equal(
            TickAction.Full,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: true, blockedPendingUserAction: false));
    }

    // The point of the whole class: a halt stops the collecting and uploading without stopping a
    // poll the server would still answer normally — which is how a server-side change reaches the
    // plugin while the halt holds.
    [Fact]
    public void A_halt_the_poll_can_see_past_still_polls()
    {
        Assert.Equal(
            TickAction.PollOnly,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: true, blockedPendingUserAction: true));
    }

    // And the other half: a halt the poll would hit in exactly the same way stops it too, rather
    // than spending a request on the same refusal forever.
    [Fact]
    public void A_halt_the_poll_cannot_see_past_stops_it_too()
    {
        Assert.Equal(
            TickAction.Nothing,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: false, blockedPendingUserAction: true));
    }

    // Worthwhileness is only ever consulted under a halt: an unblocked tick polls regardless,
    // because that poll is how a kill switch is discovered in the first place.
    [Fact]
    public void An_unblocked_tick_polls_whether_or_not_a_halt_would_have_allowed_it()
    {
        Assert.Equal(
            TickAction.Full,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: false, blockedPendingUserAction: false));
    }
}
