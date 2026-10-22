using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// What to do after an upload attempt: retry it, wait, or stop and tell the user.
/// </summary>
/// <remarks>
/// Reads neither the network nor the clock; every input is handed in. A server that answered "stop"
/// or "wait" is never argued with.
/// </remarks>
public static class RetryPolicy
{
    /// <summary>How long to wait after a 429 that carried no <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait after a 503 that carried no <c>Retry-After</c>. Long, because the global kill
    /// switch usually means the server wants breathing room.
    /// </summary>
    public static readonly TimeSpan DefaultSyncDisabledBackoff = TimeSpan.FromHours(1);

    /// <summary>How long to pause before the single in-place retry of a transient failure.</summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);

    private const int MaxTransientRetries = 1;

    /// <summary>True when this failure should be retried in place, after a short delay.</summary>
    /// <param name="status">The outcome of the attempt just made.</param>
    /// <param name="attempt">How many attempts have already been made; the first is 0.</param>
    /// <remarks>
    /// Only transient failures qualify, and only once. Server writes are idempotent, so a repeated
    /// upload cannot double-apply.
    /// </remarks>
    public static bool ShouldRetryNow(ApiStatus status, int attempt) =>
        ApiStatusMap.IsRetryable(status) && attempt < MaxTransientRetries;

    /// <summary>When the server asked for a wait, the moment to try again; otherwise null.</summary>
    /// <param name="status">The outcome of the attempt just made.</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, if it sent one.</param>
    /// <param name="now">The current time.</param>
    public static DateTimeOffset? BackoffUntil(ApiStatus status, TimeSpan? retryAfter, DateTimeOffset now)
    {
        if (!ApiStatusMap.ShouldBackOff(status))
            return null;

        // Prefer what the server asked for. Fall back only when a proxy stripped the header.
        var wait = retryAfter ?? status switch
        {
            ApiStatus.SyncDisabled => DefaultSyncDisabledBackoff,
            _ => DefaultRateLimitBackoff,
        };

        return now + wait;
    }

    /// <summary>
    /// True when syncing must stop until the user does something: paste a fresh token, sort out the
    /// character's claim on the website, or fix the settings.
    /// </summary>
    /// <remarks>
    /// These never heal on their own, so retrying would only loop against the server; the caller shows
    /// the reason and waits for the user. <see cref="SyncTickPlan.PollSurvivesHalt"/> decides whether
    /// the config poll continues through one.
    /// </remarks>
    public static bool RequiresUserAction(ApiStatus status) =>
        ApiStatusMap.IsCharacterRefusal(status)
            || status is ApiStatus.InvalidToken or ApiStatus.NotConfigured;
}
