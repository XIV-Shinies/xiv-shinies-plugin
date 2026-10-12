using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// What the Crucible sharing does with each answer to an upload: settle it as accepted, send it again
// later, let it go, or stop until the player fixes something. Each `[Fact]` is one test, like `it(...)`
// in Jest; a `[Theory]` runs once per `[InlineData]` row, like `it.each`.
public class CrucibleOutcomePolicyTests
{
    // `new(...)` with no type name builds the declared type: here a DateTimeOffset, and below each
    // dictionary value, a CrucibleOutcome.
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a refused payload holds everything back.</summary>
    private static readonly TimeSpan RejectionHold = CrucibleOutcomePolicy.RejectionHold;

    /// <summary>The outcome every status the client knows must have, with no Retry-After.</summary>
    // `new() { [key] = value, ... }` builds the dictionary with these entries, like an object literal.
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

    // Every status has the outcome the table names, and the table names every status, so a status
    // added later fails here until its outcome is decided. `Enum.GetValues<T>()` lists every value an
    // enum defines.
    [Fact]
    public void Every_status_has_the_outcome_its_meaning_calls_for()
    {
        Assert.Equal(Enum.GetValues<ApiStatus>().Length, Expected.Count);

        foreach (var status in Enum.GetValues<ApiStatus>())
            Assert.Equal(Expected[status], CrucibleOutcomePolicy.Classify(status, retryAfter: null, Now));
    }

    // A server that asks for quiet gets it, for as long as it asked.
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void A_429_or_503_retries_after_the_servers_wait(ApiStatus status)
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now.AddSeconds(90)),
            CrucibleOutcomePolicy.Classify(status, TimeSpan.FromSeconds(90), Now));
    }

    // A busy 503 that lost its Retry-After asks for no wait of its own, so the scheduler's doubling
    // wait decides when it goes again (see CrucibleOutcomePolicy.Classify).
    [Fact]
    public void A_busy_503_without_a_wait_asks_for_no_wait_of_its_own()
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now),
            CrucibleOutcomePolicy.Classify(
                ApiStatus.SyncDisabled, retryAfter: null, Now, CrucibleOutcomePolicy.BusyErrorCode));
    }

    // A busy 503 that keeps its Retry-After waits for as long as the server asked, like any other.
    [Fact]
    public void A_busy_503_with_a_wait_keeps_the_servers_wait()
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now.AddSeconds(3)),
            CrucibleOutcomePolicy.Classify(
                ApiStatus.SyncDisabled, TimeSpan.FromSeconds(3), Now, CrucibleOutcomePolicy.BusyErrorCode));
    }

    // Any other 503 without a wait gets the long wait a switched-off 503 gets. `string?` may be
    // null, like `string | null`, so a row can pass no code at all.
    [Theory]
    [InlineData("sync_disabled")]
    [InlineData(null)]
    public void Another_503_without_a_wait_keeps_the_long_wait(string? errorCode)
    {
        Assert.Equal(
            new CrucibleOutcome(CrucibleOutcomeKind.Retry, Now + RetryPolicy.DefaultSyncDisabledBackoff),
            CrucibleOutcomePolicy.Classify(ApiStatus.SyncDisabled, retryAfter: null, Now, errorCode));
    }

    // The busy code matters only on a 503: on every other status the outcome is the one the table
    // names.
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

    // Only the exact code is busy: the match is case-sensitive, and no code at all is not busy.
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

    // A refused payload holds everything back for a minute, in case every upload is being refused.
    [Fact]
    public void A_refused_payload_holds_for_a_minute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), CrucibleOutcomePolicy.RejectionHold);
    }
}
