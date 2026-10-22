using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// What the Crucible sharing does with each answer to an upload: settle it as accepted, send it again
// later, let it go, or stop until the player fixes something.
public class CrucibleOutcomePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan RejectionHold = CrucibleOutcomePolicy.RejectionHold;

    /// <summary>The outcome every status the client knows must have, with no Retry-After.</summary>
    private static readonly Dictionary<ApiStatus, CrucibleOutcome> Expected = new()
    {
        [ApiStatus.Ok] = new(CrucibleOutcomeKind.Accepted),
        [ApiStatus.RateLimited] = new(CrucibleOutcomeKind.Retry, Now + RetryPolicy.DefaultRateLimitBackoff),
        [ApiStatus.SyncDisabled] =
            new(CrucibleOutcomeKind.Retry, Now + RetryPolicy.DefaultSyncDisabledBackoff),
        [ApiStatus.ServerError] = new(CrucibleOutcomeKind.Retry, Now),
        [ApiStatus.NetworkError] = new(CrucibleOutcomeKind.Retry, Now),
        [ApiStatus.Unknown] = new(CrucibleOutcomeKind.Retry, Now),
        [ApiStatus.NotConfigured] = new(CrucibleOutcomeKind.Retry, Now),
        [ApiStatus.InvalidToken] = new(CrucibleOutcomeKind.Halt),
        [ApiStatus.CharacterNotClaimed] = new(CrucibleOutcomeKind.Halt),
        [ApiStatus.CharacterNotVerified] = new(CrucibleOutcomeKind.Halt),
        [ApiStatus.CharacterAmbiguous] = new(CrucibleOutcomeKind.Halt),
        [ApiStatus.CharacterBoundElsewhere] = new(CrucibleOutcomeKind.Halt),
        [ApiStatus.InvalidPayload] = new(CrucibleOutcomeKind.Drop, Now + RejectionHold),
        [ApiStatus.MethodNotAllowed] = new(CrucibleOutcomeKind.Drop, Now + RejectionHold),
        [ApiStatus.PayloadTooLarge] = new(CrucibleOutcomeKind.Drop, Now + RejectionHold),
    };

    // The table must name every status, so a status added later fails here until its outcome is
    // decided.
    [Fact]
    public void Every_status_has_the_outcome_its_meaning_calls_for()
    {
        Assert.Equal(Enum.GetValues<ApiStatus>().Length, Expected.Count);

        foreach (var status in Enum.GetValues<ApiStatus>())
            Assert.Equal(Expected[status], CrucibleOutcomePolicy.Classify(status, retryAfter: null, Now));
    }

    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void A_429_or_503_retries_after_the_servers_wait(ApiStatus status)
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now.AddSeconds(90)),
            CrucibleOutcomePolicy.Classify(status, TimeSpan.FromSeconds(90), Now));
    }

    // The scheduler's doubling wait then decides when the upload goes again (see
    // CrucibleOutcomePolicy.Classify).
    [Fact]
    public void A_busy_503_without_a_wait_asks_for_no_wait_of_its_own()
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now),
            CrucibleOutcomePolicy.Classify(
                ApiStatus.SyncDisabled, retryAfter: null, Now, CrucibleOutcomePolicy.BusyErrorCode));
    }

    [Fact]
    public void A_busy_503_with_a_wait_keeps_the_servers_wait()
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now.AddSeconds(3)),
            CrucibleOutcomePolicy.Classify(
                ApiStatus.SyncDisabled, TimeSpan.FromSeconds(3), Now, CrucibleOutcomePolicy.BusyErrorCode));
    }

    [Theory]
    [InlineData("sync_disabled")]
    [InlineData(null)]
    public void Another_503_without_a_wait_keeps_the_long_wait(string? errorCode)
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now + RetryPolicy.DefaultSyncDisabledBackoff),
            CrucibleOutcomePolicy.Classify(ApiStatus.SyncDisabled, retryAfter: null, Now, errorCode));
    }

    [Fact]
    public void The_busy_code_on_another_status_changes_nothing()
    {
        foreach (var status in Enum.GetValues<ApiStatus>())
        {
            if (status == ApiStatus.SyncDisabled)
                continue;

            Assert.Equal(
                Expected[status],
                CrucibleOutcomePolicy.Classify(
                    status, retryAfter: null, Now, CrucibleOutcomePolicy.BusyErrorCode));
        }
    }

    [Theory]
    [InlineData("busy", true)]
    [InlineData("Busy", false)]
    [InlineData("BUSY", false)]
    [InlineData("", false)]
    [InlineData("sync_disabled", false)]
    [InlineData(null, false)]
    public void Only_the_exact_code_is_busy(string? errorCode, bool busy)
    {
        Assert.Equal(busy, CrucibleOutcomePolicy.IsBusy(errorCode));
    }

    // The hold keeps everything back in case every upload is being refused.
    [Fact]
    public void A_refused_payload_holds_for_a_minute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), CrucibleOutcomePolicy.RejectionHold);
    }
}
