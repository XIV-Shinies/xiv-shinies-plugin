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
    /// Only an <see cref="SyncTrigger.Unlock"/> narrows this; everything else is a full sweep. The
    /// server permanently dates every category in an <c>unlock</c> upload to that moment, so naming one
    /// that did not change would date it wrongly.
    /// </remarks>
    public IReadOnlySet<string>? Categories { get; init; }
}
