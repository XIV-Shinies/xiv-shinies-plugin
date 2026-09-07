using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// What to do after an upload attempt: retry it, wait, or stop and tell the user.
/// </summary>
/// <remarks>
/// Pure decisions over an <see cref="ApiStatus"/>, so the rules are unit-tested rather than buried
/// in the orchestrator's control flow. The overriding rule is politeness: a server that answered
/// "stop" or "wait" is never argued with.
/// </remarks>
public static class RetryPolicy
{
    /// <summary>How long to wait after a 429 that carried no <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait after a 503 that carried no <c>Retry-After</c>. Deliberately long: the
    /// global kill switch usually means the server wants breathing room.
    /// </summary>
    public static readonly TimeSpan DefaultSyncDisabledBackoff = TimeSpan.FromHours(1);

    /// <summary>How long to pause before the single in-place retry of a transient failure.</summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The number of in-place retries a transient failure is allowed.</summary>
    private const int MaxTransientRetries = 1;

    /// <summary>
    /// True when this failure should be retried immediately (after a short delay).
    /// </summary>
    /// <param name="status">The outcome of the attempt just made.</param>
    /// <param name="attempt">How many attempts have already been made; the first is 0.</param>
    /// <remarks>
    /// Only transient failures qualify, and only once. Server-side writes are idempotent, so a
    /// repeated upload cannot double-apply. A terminal failure or a "wait" instruction is never
    /// retried here.
    /// </remarks>
    public static bool ShouldRetryNow(ApiStatus status, int attempt) =>
        ApiStatusMap.IsRetryable(status) && attempt < MaxTransientRetries;

    /// <summary>
    /// When the server told us to wait, the moment we may try again. Null when it did not.
    /// </summary>
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
    /// True when syncing must stop until the user does something — paste a fresh token, claim the
    /// character on the website, or fix the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These never heal on their own, so hurrying back to them would be a pointless loop against
    /// the server. An upload caller surfaces the reason in the UI and stays quiet until the user
    /// acts.
    /// </para>
    /// <para>
    /// The config poll keeps going through one of them — an unclaimed character — because
    /// <c>/config</c> answers that caller normally, and the poll is how the plugin learns a
    /// server-side state has changed that the user cannot see from inside the game. It stops for
    /// the token-shaped ones, which <c>/config</c> refuses exactly as the upload did.
    /// </para>
    /// </remarks>
    public static bool RequiresUserAction(ApiStatus status) =>
        status is ApiStatus.InvalidToken
            or ApiStatus.CharacterNotClaimed
            or ApiStatus.NotConfigured;
}
