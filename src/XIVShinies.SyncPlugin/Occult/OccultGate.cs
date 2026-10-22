using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>Decides whether the live occult tracker may run right now.</summary>
/// <remarks>
/// The tracker's consent gate, beside <see cref="UploadGate"/> and
/// <see cref="Collectors.CollectorGate"/>; the manager asks it every tick, so revoking consent takes
/// effect within a second. On top of the base gates it adds the user's
/// <see cref="PluginSettings.ShareOccultInstanceState"/> and the server's <c>occultTracker.enabled</c>.
/// A config with no <c>occultTracker</c> block reads as off, unlike an unknown /sync category, because
/// a server that does not advertise this separate endpoint would answer every upload with 404; the
/// same rule keeps the tracker quiet until the session's first /config arrives.
/// </remarks>
public static class OccultGate
{
    /// <summary>True when the occult tracker may read the instance and upload its state.</summary>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static bool CanTrack(PluginSettings settings, ConfigResponse? remoteConfig)
    {
        // Everything /sync requires: consent, onboarding, a usable token, and the server's global
        // kill switch.
        if (!UploadGate.CanUpload(settings, remoteConfig))
            return false;

        if (!settings.ShareOccultInstanceState)
            return false;

        // The server must advertise the endpoint and leave it on.
        return remoteConfig?.OccultTracker is { Enabled: true };
    }

    /// <summary>
    /// True when the server has answered and rules the tracker out: what the settings toggle draws
    /// grayed with an "Off" chip.
    /// </summary>
    /// <remarks>
    /// The complement of every server-side term in <see cref="CanTrack"/>, kept beside it so the toggle
    /// calls the tracker unavailable on exactly the terms the gate refuses. The global pause counts even
    /// though a compliant server folds it into <c>occultTracker.enabled</c>, because the backend URL is
    /// user-overridable; a missing <c>occultTracker</c> block counts as off. A config that has not
    /// arrived forbids nothing, so the toggle shows the user's choice until the server answers, and
    /// nothing uploads meanwhile.
    /// </remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static bool ServerHasSwitchedOff(ConfigResponse? remoteConfig) =>
        remoteConfig is not null
        && (!remoteConfig.Enabled || remoteConfig.OccultTracker is not { Enabled: true });

    /// <summary>
    /// True when the user's master switch is why the tracker is not running: the switch is off while
    /// the toggle is ticked, the token usable, and the server has not ruled the tracker out.
    /// </summary>
    /// <remarks>
    /// The tracker's card names this (see <see cref="Windows.SharingCardLine"/>), and the toggle keeps
    /// its tick meanwhile. It needs onboarding, because the wizard draws the card before its last step
    /// turns the switch on, and a usable token, without which the switch is not the reason. A config
    /// that has not arrived rules nothing out; past onboarding, a session that starts with the switch
    /// off fetches none until it is switched on.
    /// </remarks>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static bool StoppedBySyncSwitch(PluginSettings settings, ConfigResponse? remoteConfig) =>
        settings.OnboardingComplete
        && !settings.MasterEnabled
        && settings.HasUsableToken()
        && settings.ShareOccultInstanceState
        && !ServerHasSwitchedOff(remoteConfig);

    /// <summary>
    /// What the tracker's "Off" chip says, or null while the server still permits the tracker.
    /// </summary>
    /// <remarks>
    /// Three facts share the chip: a paused server, a server that does not offer the tracker (no
    /// <c>occultTracker</c> block), and a server that switched the tracker off. Only the last is a
    /// decision about the tracker, and naming it for the others sends the user looking for a decision
    /// nobody made.
    /// </remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    /// <param name="host">The configured website's address, which the sentences name.</param>
    public static string? ServerOffText(ConfigResponse? remoteConfig, string host) =>
        !ServerHasSwitchedOff(remoteConfig) ? null
            : remoteConfig is { Enabled: false } ? ServerOffCopy.Paused(host)
            : remoteConfig?.OccultTracker is null ? ServerOffCopy.NotOffered(host)
            : ServerOffCopy.Feature(host);
}
