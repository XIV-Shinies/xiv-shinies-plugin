using System;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>What the Crucible run sharing does after one of its uploads is answered.</summary>
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
    /// The server refused the upload itself, and would refuse it again: let it go, and send nothing
    /// more until <see cref="CrucibleOutcome.Until"/>.
    /// </summary>
    Drop,

    /// <summary>
    /// A refusal only the player can fix, such as a rejected token or a character the server would not
    /// match: raise the halt shared with /sync, and let the upload go.
    /// </summary>
    Halt,
}

/// <summary>An outcome, with the moment a waiting outcome ends.</summary>
/// <param name="Until">
/// When a <see cref="CrucibleOutcomeKind.Retry"/> may go again, or when a
/// <see cref="CrucibleOutcomeKind.Drop"/>'s hold ends; null for the other outcomes.
/// </param>
public readonly record struct CrucibleOutcome(CrucibleOutcomeKind Kind, DateTimeOffset? Until = null);

/// <summary>Decides what the Crucible run sharing does with each answer to an upload.</summary>
/// <remarks>
/// The order of the checks is part of the rule, because the status sets overlap. A request the
/// settings stopped also counts as needing the player, but nothing left the machine, so it retries;
/// every refusal only the player can fix is also terminal, so the halt is decided before the drop. A
/// retry waits as long as the server asked, and the scheduler holds it to its own least wait, doubled
/// for each failure in a row (see <see cref="CrucibleUploadScheduler"/>).
/// </remarks>
public static class CrucibleOutcomePolicy
{
    /// <summary>
    /// How long nothing goes after a refused payload, so a run of refusals does not repeat at full
    /// speed. Short, because one refused snapshot says nothing about the next, and a run is live.
    /// </summary>
    public static readonly TimeSpan RejectionHold = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The error code of a 503 sent when the server could not take the upload in time. The same
    /// content goes again; it says nothing about any switch being off.
    /// </summary>
    public const string BusyErrorCode = "busy";

    /// <summary>Whether an error body's code is <see cref="BusyErrorCode"/>, matched exactly.</summary>
    public static bool IsBusy(string? errorCode) =>
        string.Equals(errorCode, BusyErrorCode, StringComparison.Ordinal);

    /// <summary>The outcome for one answer.</summary>
    /// <param name="now">The current moment, which a waiting outcome counts from.</param>
    public static CrucibleOutcome Classify(
        ApiStatus status, TimeSpan? retryAfter, DateTimeOffset now, string? errorCode = null)
    {
        if (status == ApiStatus.Ok)
            return new CrucibleOutcome(CrucibleOutcomeKind.Accepted);

        // A busy 503 with no Retry-After, as when a proxy strips it, retries on the scheduler's doubling
        // wait. Otherwise it would get a switched-off 503's hour, holding every Crucible upload that long.
        if (status == ApiStatus.SyncDisabled && retryAfter is null && IsBusy(errorCode))
        {
            return new CrucibleOutcome(CrucibleOutcomeKind.Retry, now);
        }

        // A 429 or 503: the server asked for quiet, for as long as it said.
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
