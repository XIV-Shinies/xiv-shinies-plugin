using System;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>What the live tracker does after one of its uploads is answered.</summary>
public enum OccultOutcomeKind
{
    /// <summary>The server applied the snapshot.</summary>
    Applied,

    /// <summary>
    /// The server asked for quiet (429 or 503): wait until <see cref="OccultOutcome.Until"/>, or
    /// possibly less when <see cref="OccultOutcome.EndsWhenSyncResumes"/> is set.
    /// </summary>
    BackOff,

    /// <summary>
    /// The client refused to send, over an unusable token or backend setting. No request left the
    /// machine, so a backoff would only delay the first upload once the setting is fixed.
    /// </summary>
    Skip,

    /// <summary>
    /// A refusal only the player can fix, such as a rejected token or a character the server would
    /// not match: raise the halt shared with /sync.
    /// </summary>
    Halt,

    /// <summary>
    /// A terminal rejection of the payload (400, 405, 413), a plugin bug the same snapshot would hit
    /// again: go quiet until <see cref="OccultOutcome.Until"/>. Nothing for the player to do.
    /// </summary>
    Pause,

    /// <summary>A transient failure (a network blip, a 5xx): the next snapshot carries everything.</summary>
    Transient,
}

/// <summary>An outcome, with the moment a waiting outcome may try again.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Until">When a <see cref="OccultOutcomeKind.BackOff"/> or
/// <see cref="OccultOutcomeKind.Pause"/> ends; null for every other outcome.</param>
/// <param name="EndsWhenSyncResumes">True when the website saying syncing is back on may end a
/// <see cref="OccultOutcomeKind.BackOff"/> before <paramref name="Until"/> (see
/// <see cref="RetryPolicy.EndsWhenSyncResumes"/>).</param>
public readonly record struct OccultOutcome(
    OccultOutcomeKind Kind, DateTimeOffset? Until = null, bool EndsWhenSyncResumes = false);

/// <summary>Decides what the live tracker does with an upload's answer.</summary>
/// <remarks>
/// The check order is part of the rule, because the status sets overlap.
/// <see cref="ApiStatus.NotConfigured"/> comes before the halt, though
/// <see cref="RetryPolicy.RequiresUserAction"/> counts it: no request left the machine, and the
/// config poll raises that halt itself. The halt comes before the pause, because
/// <see cref="ApiStatusMap.IsTerminal"/> counts every user-action status too.
/// </remarks>
public static class OccultOutcomePolicy
{
    /// <summary>How long the tracker stays quiet after a terminal rejection of its payload.</summary>
    public static readonly TimeSpan TerminalRejectionBackoff = TimeSpan.FromMinutes(15);

    /// <summary>The outcome for one answer.</summary>
    /// <param name="status">The answer's status.</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, if it sent one.</param>
    /// <param name="now">The current time, which a waiting outcome counts from.</param>
    /// <param name="errorCode">The error body's code, if it had one.</param>
    public static OccultOutcome Classify(
        ApiStatus status, TimeSpan? retryAfter, DateTimeOffset now, string? errorCode = null)
    {
        if (status == ApiStatus.Ok)
            return new OccultOutcome(OccultOutcomeKind.Applied);

        if (RetryPolicy.BackoffUntil(status, retryAfter, now) is { } until)
        {
            return new OccultOutcome(
                OccultOutcomeKind.BackOff, until, RetryPolicy.EndsWhenSyncResumes(status, errorCode));
        }

        if (status == ApiStatus.NotConfigured)
            return new OccultOutcome(OccultOutcomeKind.Skip);

        if (RetryPolicy.RequiresUserAction(status))
            return new OccultOutcome(OccultOutcomeKind.Halt);

        if (ApiStatusMap.IsTerminal(status))
            return new OccultOutcome(OccultOutcomeKind.Pause, now + TerminalRejectionBackoff);

        return new OccultOutcome(OccultOutcomeKind.Transient);
    }
}
