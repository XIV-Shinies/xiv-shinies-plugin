using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// A piece of work the scheduler has decided is due: what to collect, and why.
/// </summary>
public sealed record SyncDue
{
    /// <summary>Why this upload is happening. Travels to the server in the payload.</summary>
    public required SyncTrigger Trigger { get; init; }

    /// <summary>
    /// The categories to collect, or <c>null</c> for "every category the user has enabled".
    /// </summary>
    /// <remarks>
    /// An <see cref="SyncTrigger.Unlock"/> narrows this, and so does an <see cref="SyncTrigger.Interval"/>
    /// carrying only held facts; everything else is a full sweep. The server permanently dates every
    /// category in an <c>unlock</c> upload to that moment, so naming one that did not change would
    /// date it wrongly. Held facts carry their own observation times, so they may ride along.
    /// </remarks>
    public IReadOnlySet<string>? Categories { get; init; }

    /// <summary>The held categories this upload took from the scheduler; empty when it took none.</summary>
    /// <remarks>
    /// Held again if the upload fails in a way a later try could fix (see
    /// <see cref="SyncScheduler.MarkFailed"/>).
    /// </remarks>
    public IReadOnlySet<string> HeldCategories { get; init; } = new HashSet<string>();
}
