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

    /// <summary>
    /// True when the server has answered and its answer rules the tracker out — what the settings
    /// toggle draws greyed and chipped "Off".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complement of <see cref="CanTrack"/>'s last line, and it lives beside it so the two
    /// cannot drift: the control must call a state unavailable on exactly the terms the gate
    /// refuses to run on. A config carrying no <c>occultTracker</c> block counts as off here for
    /// the same reason it does there — a server that never advertised the endpoint cannot serve
    /// it, so a toggle reading "on" would describe something that is not happening.
    /// </para>
    /// <para>
    /// A config that has NOT arrived is not off. It forbids nothing, exactly as it forbids no
    /// category, so the toggle keeps showing the user's own choice until the server answers. The
    /// tracker cannot run in that window either, but nothing is uploaded during it, so a
    /// momentary "on" describes an intention rather than misreporting a live behavior.
    /// </para>
    /// </remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static bool ServerHasSwitchedOff(ConfigResponse? remoteConfig) =>
        remoteConfig is not null && remoteConfig.OccultTracker is not { Enabled: true };
}
