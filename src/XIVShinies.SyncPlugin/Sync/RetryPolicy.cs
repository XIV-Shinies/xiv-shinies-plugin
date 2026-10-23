using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// What to do after an upload attempt: retry it, wait, or stop and tell the user.
/// </summary>
/// <remarks>
/// Reads neither the network nor the clock; every input is handed in. A server that answered "stop"
/// or "wait" is never argued with. The one wait that ends early is a 503 <c>sync_disabled</c>, and
/// only once the website's <c>/config</c> says syncing is back on (see
/// <see cref="EndsWhenSyncResumes"/>).
/// </remarks>
public static class RetryPolicy
{
    /// <summary>How long to wait after a 429 that carried no <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait after a 503 that carried no <c>Retry-After</c>: the hour the website sends with
    /// <c>sync_disabled</c>, so a stripped header never shortens the wait.
    /// </summary>
    public static readonly TimeSpan DefaultSyncDisabledBackoff = TimeSpan.FromHours(1);

    /// <summary>How long to pause before the single in-place retry of a transient failure.</summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The error code of a 503 sent because syncing is switched off.</summary>
    public const string SyncDisabledErrorCode = "sync_disabled";

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
    /// True when the wait this answer starts may end before its deadline, once the website says
    /// syncing is back on.
    /// </summary>
    /// <param name="status">The outcome of the attempt just made.</param>
    /// <param name="errorCode">The error body's code, or null when there was none.</param>
    /// <remarks>
    /// Only a 503 whose code is exactly <c>sync_disabled</c> qualifies. Its switch may be the player's
    /// own "Sync from the plugin" (see <see cref="ServerOffCopy.Paused"/>), which they can turn back on
    /// in seconds. Every other wait (a 429, a busy server, a tracker that cannot answer, a proxy page)
    /// has nothing to do with that switch and runs its full length.
    /// </remarks>
    public static bool EndsWhenSyncResumes(ApiStatus status, string? errorCode) =>
        status == ApiStatus.SyncDisabled
        && string.Equals(errorCode, SyncDisabledErrorCode, StringComparison.Ordinal);

    /// <summary>
    /// True when a <c>/config</c> answer ends a wait that <see cref="EndsWhenSyncResumes"/> let end
    /// early.
    /// </summary>
    /// <param name="syncEnabled">
    /// Whether the answer says this upload path's switch is on: <c>enabled</c> for the collections
    /// upload, and also its own block's <c>enabled</c> for the live tracker and the Crucible sharing.
    /// </param>
    /// <param name="pollNumber">Which poll gave the answer, counting polls from 1 as they start.</param>
    /// <param name="pollsStartedAtRefusal">How many polls had started when the refusal arrived.</param>
    /// <remarks>
    /// Only a poll that started after the refusal counts. One already on its way may carry the answer
    /// from before the switch went off, and would send the next upload straight into the same refusal.
    /// </remarks>
    public static bool ResumesSync(bool syncEnabled, int pollNumber, int pollsStartedAtRefusal) =>
        syncEnabled && pollNumber > pollsStartedAtRefusal;

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
