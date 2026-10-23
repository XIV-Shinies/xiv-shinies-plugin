using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

/// <summary>
/// How much of a frame's sync work may run, by the two rules <see cref="SyncTickPlan"/> states.
/// </summary>
public class SyncTickPlanTests
{
    // A poll brought forward comes no later than `soon`, and never later than it was already due.
    [Theory]
    [InlineData(30, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void A_poll_brought_forward_is_never_pushed_back(int dueInMinutes, int expectedInMinutes)
    {
        var now = new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            now.AddMinutes(expectedInMinutes),
            SyncTickPlan.BringPollForward(now.AddMinutes(dueInMinutes), now.AddMinutes(2)));
    }

    // A null due time means the first opportunity, which is already sooner than `soon`.
    [Fact]
    public void A_poll_due_at_the_first_opportunity_stays_due()
    {
        var soon = new DateTimeOffset(2026, 10, 20, 20, 2, 0, TimeSpan.Zero);

        Assert.Null(SyncTickPlan.BringPollForward(due: null, soon));
    }

    // SyncTickPlan.PollSurvivesHalt says why these halts leave the poll worth making. No halt at all
    // is not a refusal.
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

    // Consent outranks everything: a fresh install makes no request at all.
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

    // A halt stops collecting and uploading but not a poll the server still answers normally, which
    // is how a server-side change reaches the plugin while the halt holds.
    [Fact]
    public void A_halt_the_poll_can_see_past_still_polls()
    {
        Assert.Equal(
            TickAction.PollOnly,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: true, blockedPendingUserAction: true));
    }

    // A halt the poll would hit the same way stops the poll too, or every tick would spend a request
    // on the same refusal.
    [Fact]
    public void A_halt_the_poll_cannot_see_past_stops_it_too()
    {
        Assert.Equal(
            TickAction.Nothing,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: false, blockedPendingUserAction: true));
    }

    // Worthwhileness counts only under a halt: an unblocked tick's poll is how the plugin discovers a
    // kill switch.
    [Fact]
    public void An_unblocked_tick_polls_whether_or_not_a_halt_would_have_allowed_it()
    {
        Assert.Equal(
            TickAction.Full,
            SyncTickPlan.Decide(
                canContactServer: true, pollIsWorthwhile: false, blockedPendingUserAction: false));
    }
}
