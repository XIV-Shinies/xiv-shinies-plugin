using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Decides whether the live occult tracker may run at all, right now.
/// </summary>
/// <remarks>
/// <para>
/// This is the tracker's consent gate, in the same pure-and-tested mold as
/// <see cref="UploadGate"/> and <see cref="Collectors.CollectorGate"/>: the manager asks it
/// every tick and stops the moment it says no, so consent revocation takes effect within a
/// second. Layered on top of the base gates (master switch, completed onboarding, usable
/// token, server's global kill switch) are the tracker's own two: the user's
/// <see cref="PluginSettings.ShareOccultInstanceState"/> toggle and the server's
/// <c>occultTracker.enabled</c> switch.
/// </para>
/// <para>
/// A config with no <c>occultTracker</c> block reads as OFF — deliberately opposite to the
/// unknown-category rule. An unknown /sync category defaults enabled because the server
/// strips keys it does not recognize; this is a separate ENDPOINT, and a server that does
/// not advertise it would answer every upload with a 404. The same rule keeps the tracker
/// quiet until the first /config of the session arrives.
/// </para>
/// </remarks>
public static class OccultGate
{
    /// <summary>True when the occult tracker may read the instance and upload its state.</summary>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static bool CanTrack(PluginSettings settings, ConfigResponse? remoteConfig)
    {
        // Everything /sync requires: consent, onboarding, a usable token, and the server's
        // global kill switch.
        if (!UploadGate.CanUpload(settings, remoteConfig))
            return false;

        // The feature's own opt-out.
        if (!settings.ShareOccultInstanceState)
            return false;

        // The server must have advertised the endpoint AND left it switched on.
        return remoteConfig?.OccultTracker is { Enabled: true };
    }
}
