using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>Answers whether a snapshot can identify its instance to the server.</summary>
/// <remarks>
/// The server matches an upload to a tracker by shared <c>(encounter, sinceUtc)</c> pairs, and only
/// server-assigned epochs count, such as a preparing CE's phase deadline or a FATE's start time. A
/// down entry's stamp comes from the plugin's own clock, so it anchors nothing. Without a qualifying
/// pair an upload can only create a duplicate tracker or get <c>unresolved</c>, so the uploader holds
/// its enter until this returns true, for a bounded settle window.
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
