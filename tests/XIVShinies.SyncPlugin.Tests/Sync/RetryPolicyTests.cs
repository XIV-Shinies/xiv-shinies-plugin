using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// What to do after an upload fails. Getting it wrong hammers a server that already said stop.
public class RetryPolicyTests
{
    // The server refused on principle, so the same request can only fail again.
    [Theory]
    [InlineData(ApiStatus.InvalidToken)]
    [InlineData(ApiStatus.CharacterNotClaimed)]
    [InlineData(ApiStatus.CharacterNotVerified)]
    [InlineData(ApiStatus.CharacterAmbiguous)]
    [InlineData(ApiStatus.CharacterBoundElsewhere)]
    [InlineData(ApiStatus.InvalidPayload)]
    [InlineData(ApiStatus.PayloadTooLarge)]
    [InlineData(ApiStatus.MethodNotAllowed)]
    [InlineData(ApiStatus.NotConfigured)]
    public void Never_retries_a_terminal_failure(ApiStatus status)
    {
        Assert.False(RetryPolicy.ShouldRetryNow(status, attempt: 0));
    }

    [Fact]
    public void Does_not_retry_a_success()
    {
        Assert.False(RetryPolicy.ShouldRetryNow(ApiStatus.Ok, attempt: 0));
    }

    // Writes are idempotent server-side, so one immediate retry of a transient failure is safe.
    [Theory]
    [InlineData(ApiStatus.ServerError)]
    [InlineData(ApiStatus.NetworkError)]
    public void Retries_a_transient_failure_exactly_once(ApiStatus status)
    {
        Assert.True(RetryPolicy.ShouldRetryNow(status, attempt: 0));
        Assert.False(RetryPolicy.ShouldRetryNow(status, attempt: 1));
    }

    // 429 and 503 mean "wait": the caller stops uploading until the backoff expires.
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void Never_retries_immediately_when_told_to_back_off(ApiStatus status)
    {
        Assert.False(RetryPolicy.ShouldRetryNow(status, attempt: 0));
    }

    [Fact]
    public void Honors_the_servers_retry_after_when_it_sends_one()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var until = RetryPolicy.BackoffUntil(
            ApiStatus.RateLimited, TimeSpan.FromSeconds(90), now);

        Assert.Equal(now.AddSeconds(90), until);
    }

    // A proxy can strip the contract's Retry-After header, so fall back to a documented default
    // rather than retrying instantly.
    [Fact]
    public void Falls_back_to_a_short_wait_when_rate_limited_without_a_header()
    {
        var now = DateTimeOffset.UnixEpoch;

        var until = RetryPolicy.BackoffUntil(ApiStatus.RateLimited, retryAfter: null, now);

        Assert.Equal(now.Add(RetryPolicy.DefaultRateLimitBackoff), until);
    }

    // Without the header, the wait is the hour the website sends with `sync_disabled`.
    [Fact]
    public void Falls_back_to_an_hour_when_sync_is_disabled_without_a_header()
    {
        var now = DateTimeOffset.UnixEpoch;

        var until = RetryPolicy.BackoffUntil(ApiStatus.SyncDisabled, retryAfter: null, now);

        Assert.Equal(now.Add(RetryPolicy.DefaultSyncDisabledBackoff), until);
    }

    // Only a 503 with the exact code `sync_disabled` may end early. A 429, a busy server, a tracker
    // that cannot answer and a proxy page with no body keep their whole wait.
    [Theory]
    [InlineData(ApiStatus.SyncDisabled, "sync_disabled", true)]
    [InlineData(ApiStatus.SyncDisabled, "busy", false)]
    [InlineData(ApiStatus.SyncDisabled, "tracker_unavailable", false)]
    [InlineData(ApiStatus.SyncDisabled, null, false)]
    [InlineData(ApiStatus.SyncDisabled, "SYNC_DISABLED", false)]
    [InlineData(ApiStatus.RateLimited, "sync_disabled", false)]
    public void Only_a_switched_off_503_waits_for_sync_to_resume(
        ApiStatus status, string? errorCode, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.EndsWhenSyncResumes(status, errorCode));
    }

    // Polls are numbered as they start. One already on its way at the refusal, 3 or lower here, may
    // carry the answer from before syncing went off, so only a later poll saying it is on counts.
    [Theory]
    [InlineData(true, 4, 3, true)]
    [InlineData(true, 3, 3, false)]
    [InlineData(true, 2, 3, false)]
    [InlineData(false, 4, 3, false)]
    public void Only_a_later_poll_saying_sync_is_on_ends_the_wait(
        bool syncEnabled, int pollNumber, int pollsStartedAtRefusal, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.ResumesSync(syncEnabled, pollNumber, pollsStartedAtRefusal));
    }

    [Fact]
    public void Imposes_no_backoff_for_statuses_that_are_not_a_wait_instruction()
    {
        var now = DateTimeOffset.UnixEpoch;

        Assert.Null(RetryPolicy.BackoffUntil(ApiStatus.Ok, null, now));
        Assert.Null(RetryPolicy.BackoffUntil(ApiStatus.ServerError, null, now));
        Assert.Null(RetryPolicy.BackoffUntil(ApiStatus.InvalidToken, null, now));
    }

    // A 401 never heals on retry. Syncing stops until the user pastes a new token, rather than
    // retrying every interval forever.
    [Fact]
    public void An_invalid_token_stops_syncing_until_the_user_intervenes()
    {
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.InvalidToken));
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.CharacterNotClaimed));
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.NotConfigured));

        // Every character refusal needs the player to act; waiting never fixes one.
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.CharacterNotVerified));
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.CharacterAmbiguous));
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.CharacterBoundElsewhere));

        Assert.False(RetryPolicy.RequiresUserAction(ApiStatus.ServerError));
        Assert.False(RetryPolicy.RequiresUserAction(ApiStatus.RateLimited));
        Assert.False(RetryPolicy.RequiresUserAction(ApiStatus.Ok));
    }
}
