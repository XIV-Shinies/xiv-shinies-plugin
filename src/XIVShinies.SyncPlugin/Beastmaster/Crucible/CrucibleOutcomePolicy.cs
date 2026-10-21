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
    /// The same upload goes again once <see cref="CrucibleOutcome.Until"/> has passed (or, when
    /// <see cref="CrucibleOutcome.EndsWhenSyncResumes"/>, once the website says the sharing is back
    /// on), and never before the scheduler's least wait.
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
/// <param name="EndsWhenSyncResumes">
/// Whether the website saying syncing is back on may end a retry's wait before
/// <paramref name="Until"/> (see <see cref="RetryPolicy.EndsWhenSyncResumes"/>).
/// </param>
// A `record struct`: a small value with value equality, copied rather than shared like a class. A `?`
// after a type means the value may be null, like `T | null`. `= null` and `= false` give a parameter
// a default, like `until: T | null = null` in TypeScript: a caller may leave it out, and it then takes
// that value.
public readonly record struct CrucibleOutcome(
    CrucibleOutcomeKind Kind, DateTimeOffset? Until = null, bool EndsWhenSyncResumes = false);

/// <summary>Decides what the Crucible run sharing does with each answer to an upload.</summary>
/// <remarks>
/// <para>
/// The order of the checks is part of the rule, because the status sets overlap: a request the
/// settings stopped counts as needing the player too, but no request left the machine, so it is kept
/// to retry rather than reported as a refusal; and every refusal only the player can fix also counts
/// as terminal, so the halt is decided before the drop.
/// </para>
/// <para>
/// A retry waits for whatever the server asked, or for nothing more, or until the website says the
/// sharing is back on after a switched-off 503; the scheduler then holds it to its own least wait,
/// doubled for each failure in a row (see <see cref="CrucibleUploadScheduler"/>).
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

    /// <summary>
    /// The error code of a 503 sent when the server could not take the upload in time. The same
    /// content goes again; it says nothing about any switch being off.
    /// </summary>
    public const string BusyErrorCode = "busy";

    /// <summary>Whether an error body's code is <see cref="BusyErrorCode"/>, matched exactly.</summary>
    /// <param name="errorCode">The error body's code, or null when there was none.</param>
    // The `=>` body is a single expression whose value the method returns, like an arrow function
    // without braces. `StringComparison.Ordinal` compares character for character, as `===` does, so
    // "Busy" is not a match. `string.Equals` is safe on a null code, which simply does not match.
    public static bool IsBusy(string? errorCode) =>
        string.Equals(errorCode, BusyErrorCode, StringComparison.Ordinal);

    /// <summary>The outcome for one answer.</summary>
    /// <param name="status">The answer's status.</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, if it sent one.</param>
    /// <param name="now">The current moment, which a waiting outcome counts from.</param>
    /// <param name="errorCode">The error body's code, if it had one. Defaults to null.</param>
    public static CrucibleOutcome Classify(
        ApiStatus status, TimeSpan? retryAfter, DateTimeOffset now, string? errorCode = null)
    {
        if (status == ApiStatus.Ok)
            return new CrucibleOutcome(CrucibleOutcomeKind.Accepted);

        // A busy 503 whose Retry-After was lost on the way (a proxy stripping it) goes again on the
        // scheduler's own doubling wait. Without this it would take the hour a 503 with no wait gets
        // when a switch is off, holding every Crucible upload that long. `is null` tests for null,
        // like `=== null`.
        if (status == ApiStatus.SyncDisabled && retryAfter is null && IsBusy(errorCode))
        {
            return new CrucibleOutcome(CrucibleOutcomeKind.Retry, now);
        }

        // A 429 or 503: the server asked for quiet, for as long as it said, or until syncing is back
        // on for a 503 saying it is switched off. `is { } until` matches when there is a value and
        // names it.
        if (RetryPolicy.BackoffUntil(status, retryAfter, now) is { } until)
        {
            return new CrucibleOutcome(
                CrucibleOutcomeKind.Retry, until, RetryPolicy.EndsWhenSyncResumes(status, errorCode));
        }

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
