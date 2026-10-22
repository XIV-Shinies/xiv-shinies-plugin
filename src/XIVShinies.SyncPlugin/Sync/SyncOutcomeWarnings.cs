using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides whether an unhappy upload outcome is worth reporting, remembering what it already said.
/// </summary>
/// <remarks>
/// A failed upload's cause, such as an outage or a blocked network, usually outlasts the gap between
/// sweeps, so unguarded it would log the same line every few minutes. A success clears the memory, so
/// a server that fails, recovers and fails again is reported twice. Not thread-safe: the upload path
/// runs one at a time behind its in-flight flag.
/// </remarks>
public sealed class SyncOutcomeWarnings
{
    /// <summary>The outcomes already reported since the last successful upload.</summary>
    private readonly HashSet<ApiStatus> reported = [];

    /// <summary>True when this outcome has not been reported since the last success.</summary>
    /// <remarks>
    /// Records as it tests. Kept per status, so going quiet about a 500 does not also silence the
    /// network error that follows it.
    /// </remarks>
    public bool ShouldReport(ApiStatus status) => reported.Add(status);

    /// <summary>
    /// Records that an upload succeeded, so a later failure is reportable again. True when this
    /// success ended a run of failures, which is the moment worth announcing.
    /// </summary>
    /// <remarks>
    /// Clears every remembered outcome, since a working upload ends the whole run. Without the
    /// announcement, a fixed outage would look like an ongoing one.
    /// </remarks>
    public bool NoteSuccess()
    {
        var endedARunOfFailures = reported.Count > 0;
        reported.Clear();
        return endedARunOfFailures;
    }
}
