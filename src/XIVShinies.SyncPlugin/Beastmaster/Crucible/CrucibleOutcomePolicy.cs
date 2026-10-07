using System;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>What the Crucible run sharing does after one of its uploads is answered.</summary>
// An `enum` is a fixed set of named values, numbers underneath, like a TypeScript numeric `enum`.
public enum CrucibleOutcomeKind
{
    /// <summary>The server took the upload.</summary>
    Accepted,

    /// <summary>
    /// The same upload goes again once <see cref="CrucibleOutcome.Until"/> has passed, or later if the
    /// scheduler's least wait is longer.
    /// </summary>
    Retry,

    /// <summary>
    /// The server refused the upload itself, and sending it again would be refused the same way: let it
    /// go, and send nothing more until <see cref="CrucibleOutcome.Until"/>.
    /// </summary>
    Drop,

    /// <summary>
    /// A refusal only the player can fix (a rejected token, or a character the server would not match):
    /// raise the halt shared with /sync, and let the upload go.
    /// </summary>
    Halt,
}

/// <summary>An outcome, with the moment a waiting outcome ends.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Until">
/// When a <see cref="CrucibleOutcomeKind.Retry"/> may go again, or when a
/// <see cref="CrucibleOutcomeKind.Drop"/>'s hold ends; null for the other outcomes.
/// </param>
// A `record struct`: a small value with value equality, copied rather than shared like a class. A `?`
// after a type means the value may be null, like `T | null`.
public readonly record struct CrucibleOutcome(CrucibleOutcomeKind Kind, DateTimeOffset? Until = null);

/// <summary>Decides what the Crucible run sharing does with each answer to an upload.</summary>
/// <remarks>
/// <para>
/// The order of the checks is part of the rule, because the status sets overlap: a request the
/// settings stopped counts as needing the player too, but no request left the machine, so it is kept
/// to retry rather than reported as a refusal; and every refusal only the player can fix also counts
/// as terminal, so the halt is decided before the drop.
/// </para>
/// <para>
/// A retry waits for whatever the server asked, or for nothing more; the scheduler then holds it to
/// its own least wait, doubled for each failure in a row (see <see cref="CrucibleUploadScheduler"/>).
/// </para>
/// </remarks>
public static class CrucibleOutcomePolicy
{
    /// <summary>
    /// How long nothing goes after a refused payload, so a run whose every upload is refused does not
    /// keep being refused at full speed. Short, because one refused snapshot says nothing about the
    /// next one, and a run is live.
    /// </summary>
    // `static readonly` rather than `const`: a `TimeSpan` (a length of time) is built when the
    // program runs, and `const` only takes values fixed when the code compiles.
    public static readonly TimeSpan RejectionHold = TimeSpan.FromMinutes(1);

    /// <summary>The outcome for one answer.</summary>
    /// <param name="status">The answer's status.</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, if it sent one.</param>
    /// <param name="now">The current moment, which a waiting outcome counts from.</param>
    public static CrucibleOutcome Classify(ApiStatus status, TimeSpan? retryAfter, DateTimeOffset now)
    {
        if (status == ApiStatus.Ok)
            return new CrucibleOutcome(CrucibleOutcomeKind.Accepted);

        // A 429 or 503: the server asked for quiet, for as long as it said. `is { } until` matches
        // when there is a value and names it.
        if (RetryPolicy.BackoffUntil(status, retryAfter, now) is { } until)
            return new CrucibleOutcome(CrucibleOutcomeKind.Retry, until);

        if (status == ApiStatus.NotConfigured)
            return new CrucibleOutcome(CrucibleOutcomeKind.Retry, now);

        if (RetryPolicy.RequiresUserAction(status))
            return new CrucibleOutcome(CrucibleOutcomeKind.Halt);

        if (ApiStatusMap.IsTerminal(status))
            return new CrucibleOutcome(CrucibleOutcomeKind.Drop, now + RejectionHold);

        // A network failure, a 5xx, or a status the contract does not define, any of which may pass
        // on its own.
        return new CrucibleOutcome(CrucibleOutcomeKind.Retry, now);
    }
}
