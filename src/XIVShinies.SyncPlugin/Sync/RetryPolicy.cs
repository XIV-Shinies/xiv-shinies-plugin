using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// What to do after an upload attempt: retry it, wait, or stop and tell the user.
/// </summary>
/// <remarks>
/// Pure decisions over an <see cref="ApiStatus"/>, so the rules are unit-tested rather than buried
/// in the orchestrator's control flow. The overriding rule is politeness: a server that answered
/// "stop" or "wait" is never argued with. The one wait that ends early is a 503
/// <c>sync_disabled</c>, and only once the website's own <c>/config</c> says the switch is back on
/// (see <see cref="EndsWhenSyncResumes"/>).
/// </remarks>
public static class RetryPolicy
{
    /// <summary>How long to wait after a 429 that carried no <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait after a 503 that carried no <c>Retry-After</c>: the same hour the website
    /// sends with <c>sync_disabled</c>, so losing the header never shortens the wait.
    /// </summary>
    public static readonly TimeSpan DefaultSyncDisabledBackoff = TimeSpan.FromHours(1);

    /// <summary>How long to pause before the single in-place retry of a transient failure.</summary>
    public static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>The error code of a 503 sent because syncing is switched off.</summary>
    public const string SyncDisabledErrorCode = "sync_disabled";

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
    /// Whether the wait this answer starts may end before its deadline, once the website says
    /// syncing is back on.
    /// </summary>
    /// <param name="status">The outcome of the attempt just made.</param>
    /// <param name="errorCode">The error body's code, or null when there was none.</param>
    /// <remarks>
    /// Only a 503 whose code says syncing is switched off qualifies. The switch behind it may be the
    /// player's own "Sync from the plugin" on the website (see <see cref="ServerOffCopy.Paused"/>),
    /// which they can turn back on in seconds, and the rest of an hour's wait after that would
    /// ignore the website's own answer. Every other wait (a 429, a busy server, a tracker that cannot
    /// answer, a proxy page) has nothing to do with that switch, and runs its full length. The code is
    /// matched exactly, character for character, as the other codes are.
    /// </remarks>
    public static bool EndsWhenSyncResumes(ApiStatus status, string? errorCode) =>
        status == ApiStatus.SyncDisabled
        && string.Equals(errorCode, SyncDisabledErrorCode, StringComparison.Ordinal);

    /// <summary>
    /// Whether a <c>/config</c> answer ends a wait that <see cref="EndsWhenSyncResumes"/> let end
    /// early.
    /// </summary>
    /// <param name="syncEnabled">
    /// Whether the answer says the upload path's own switch is on: <c>enabled</c> for the collections
    /// upload, and that and its own block's <c>enabled</c> for the live tracker and the Crucible
    /// sharing.
    /// </param>
    /// <param name="pollNumber">Which poll gave the answer, counting polls from 1 as they start.</param>
    /// <param name="pollsStartedAtRefusal">How many polls had started when the refusal arrived.</param>
    /// <remarks>
    /// Only a poll that started after the refusal counts. One already on its way may carry the
    /// website's answer from before the switch went off, and ending the wait on it would send the
    /// next upload straight into the same refusal.
    /// </remarks>
    public static bool ResumesSync(bool syncEnabled, int pollNumber, int pollsStartedAtRefusal) =>
        syncEnabled && pollNumber > pollsStartedAtRefusal;

    /// <summary>
    /// True when syncing must stop until the user does something — paste a fresh token, sort out
    /// the character's claim on the website, or fix the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These never heal on their own, so hurrying back to them would be a pointless loop against
    /// the server. An upload caller surfaces the reason in the UI and stays quiet until the user
    /// acts.
    /// </para>
    /// <para>
    /// Whether the config poll keeps going through one of these halts depends on which status
    /// raised it; <see cref="SyncTickPlan.PollSurvivesHalt"/> is that rule.
    /// </para>
    /// </remarks>
    // `||` joins two whole conditions; the `or` after `is` joins the values of one pattern (see
    // ApiStatusMap.IsTerminal for the longer note).
    public static bool RequiresUserAction(ApiStatus status) =>
        ApiStatusMap.IsCharacterRefusal(status)
            || status is ApiStatus.InvalidToken or ApiStatus.NotConfigured;
}
