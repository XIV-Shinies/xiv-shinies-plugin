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

    // A refused payload holds everything back for a minute, in case every upload is being refused.
    [Fact]
    public void A_refused_payload_holds_for_a_minute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), CrucibleOutcomePolicy.RejectionHold);
    }
}
