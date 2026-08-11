using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Answers whether a snapshot can identify its instance to the server.
/// </summary>
/// <remarks>
/// The server matches an upload to a tracker through shared <c>(encounter, sinceUtc)</c>
/// pairs, and only <b>server-assigned</b> epochs count — a preparing CE's phase deadline or a
/// FATE's start time. A down entry's stamp is the plugin's own observation clock ("null
/// entries carry state but never identity", and a down stamp is the same kind of local
/// fact), so it anchors nothing. An upload with no qualifying pair can only create a
/// duplicate tracker or answer <c>unresolved</c> — which is why the uploader holds its
/// enter until this returns true, up to a bounded settle window.
/// </remarks>
public static class OccultFingerprint
{
    /// <summary>True when at least one entry carries a server-assigned identity epoch.</summary>
    public static bool IsFingerprintable(IReadOnlyList<OccultEncounterState> snapshot)
    {
        foreach (var state in snapshot)
        {
            if (state.SinceUtc is not null && state.Status is not OccultEncounterStatus.Down)
                return true;
        }

        return false;
    }
}
