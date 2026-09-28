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
    /// The server asked for quiet (429 or 503): wait until <see cref="OccultOutcome.Until"/>.
    /// </summary>
    BackOff,

    /// <summary>
    /// The client refused to send (an unusable token or backend setting). No request left the
    /// machine, so there is nothing to pace, and a backoff would only delay the first upload once
    /// the setting is fixed.
    /// </summary>
    Skip,

    /// <summary>
    /// A refusal only the player can fix (a rejected token, or a character the server would not
    /// match): raise the halt shared with /sync.
    /// </summary>
    Halt,

    /// <summary>
    /// A terminal rejection of the payload itself (400, 405, 413), a plugin bug the same snapshot
    /// would hit again: go quiet until <see cref="OccultOutcome.Until"/>. Nothing for the player to do.
    /// </summary>
    Pause,

    /// <summary>A transient failure (a network blip, a 5xx): the next snapshot carries everything.</summary>
    Transient,
}

/// <summary>An outcome, with the moment a waiting outcome may try again.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Until">When a <see cref="OccultOutcomeKind.BackOff"/> or
/// <see cref="OccultOutcomeKind.Pause"/> ends; null for every other outcome.</param>
// A `record struct`: a small value with value equality, copied rather than shared like a class.
public readonly record struct OccultOutcome(OccultOutcomeKind Kind, DateTimeOffset? Until = null);

/// <summary>
/// Decides what the live tracker does with an upload's answer. Pure, so the rule is unit-tested
/// rather than buried in the upload task.
/// </summary>
/// <remarks>
/// The order of the checks is part of the rule, because the status sets overlap.
/// <see cref="ApiStatus.NotConfigured"/> comes before the halt, although
/// <see cref="RetryPolicy.RequiresUserAction"/> counts it: no request left the machine, so there is
/// no refusal to report, and the config poll raises that halt on its own. The halt comes before the
/// pause, because <see cref="ApiStatusMap.IsTerminal"/> counts every user-action status too.
/// </remarks>
public static class OccultOutcomePolicy
{
    /// <summary>How long the tracker stays quiet after a terminal rejection of its payload.</summary>
    public static readonly TimeSpan TerminalRejectionBackoff = TimeSpan.FromMinutes(15);

    /// <summary>The outcome for one answer.</summary>
    /// <param name="status">The answer's status.</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, if it sent one.</param>
    /// <param name="now">The current time, which a waiting outcome counts from.</param>
    public static OccultOutcome Classify(ApiStatus status, TimeSpan? retryAfter, DateTimeOffset now)
    {
        if (status == ApiStatus.Ok)
            return new OccultOutcome(OccultOutcomeKind.Applied);

        if (RetryPolicy.BackoffUntil(status, retryAfter, now) is { } until)
            return new OccultOutcome(OccultOutcomeKind.BackOff, until);

        if (status == ApiStatus.NotConfigured)
            return new OccultOutcome(OccultOutcomeKind.Skip);

        if (RetryPolicy.RequiresUserAction(status))
            return new OccultOutcome(OccultOutcomeKind.Halt);

        if (ApiStatusMap.IsTerminal(status))
            return new OccultOutcome(OccultOutcomeKind.Pause, now + TerminalRejectionBackoff);

        return new OccultOutcome(OccultOutcomeKind.Transient);
    }
}
