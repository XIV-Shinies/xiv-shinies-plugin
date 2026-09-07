using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides whether an unhappy upload outcome is worth reporting, remembering what it already said.
/// </summary>
/// <remarks>
/// <para>
/// The conditions behind a failed upload last far longer than the gap between sweeps: an outage, a
/// network that blocks the backend, a schema the server tightened. Left unguarded, one of those
/// writes the same line every few minutes for as long as it lasts.
/// </para>
/// <para>
/// A success clears the memory, which is what separates this from a plain latch. A server that
/// fails, recovers, and fails again has failed twice, and the second time is news — a latch would
/// report the first and then stay quiet for the rest of the session.
/// </para>
/// <para>
/// Not thread-safe. The upload path is one at a time behind its in-flight flag.
/// </para>
/// </remarks>
public sealed class SyncOutcomeWarnings
{
    /// <summary>The outcomes already reported since the last successful upload.</summary>
    private readonly HashSet<ApiStatus> reported = [];

    /// <summary>True when this outcome has not been reported since the last success.</summary>
    /// <remarks>
    /// Records as it tests. Kept per status rather than as a single flag, so going quiet about a
    /// 500 does not also go quiet about the network error that follows it.
    /// </remarks>
    public bool ShouldReport(ApiStatus status) => reported.Add(status);

    /// <summary>
    /// Records that an upload succeeded, so a later failure is reportable again. True when this
    /// success ended a run of failures, which is the moment worth announcing.
    /// </summary>
    /// <remarks>
    /// Clears every remembered outcome, not just the one that happened to arrive last: an upload
    /// that worked ends the whole run of failures before it. The return value exists because an
    /// outage that stops is otherwise invisible — the failure earns a line and the recovery earns
    /// silence, leaving a reader unable to tell a fixed problem from an ongoing one.
    /// </remarks>
    public bool NoteSuccess()
    {
        var endedARunOfFailures = reported.Count > 0;
        reported.Clear();
        return endedARunOfFailures;
    }
}
