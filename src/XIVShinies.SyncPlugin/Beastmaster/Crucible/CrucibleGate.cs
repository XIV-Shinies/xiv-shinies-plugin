using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Decides whether the Crucible run sharing may read windows and upload, right now.
/// </summary>
/// <remarks>
/// Beyond the gates every upload passes (master switch, completed onboarding, usable token, the
/// server's global kill switch), the sharing needs its own two: the user's
/// <see cref="PluginSettings.ShareCrucibleRuns"/> toggle and the server's <c>crucibleRuns.enabled</c>
/// switch. A config with no <c>crucibleRuns</c> block reads as off: a server that does not advertise
/// this endpoint would answer every upload with a 404, and the rule keeps the sharing quiet until the
/// session's first <c>/config</c>.
/// </remarks>
public static class CrucibleGate
{
    /// <summary>True when the sharing may read the Crucible's windows and upload them.</summary>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static bool CanShare(PluginSettings settings, ConfigResponse? remoteConfig)
    {
        if (!UploadGate.CanUpload(settings, remoteConfig))
            return false;

        if (!settings.ShareCrucibleRuns)
            return false;

        // The server must have advertised the endpoint AND left it switched on.
        return remoteConfig?.CrucibleRuns is { Enabled: true };
    }

    /// <summary>
    /// True when the server has answered and rules the sharing out: the state a settings toggle for
    /// the sharing draws grayed and chipped "Off".
    /// </summary>
    /// <remarks>
    /// Once a config has arrived, this is the complement of every server-side term in
    /// <see cref="CanShare"/>, so a control calls the sharing unavailable on exactly the gate's terms.
    /// Before the first <c>/config</c>, a toggle shows the user's own choice, an intention rather than
    /// a live upload, since the gate still refuses.
    /// </remarks>
    public static bool ServerHasSwitchedOff(ConfigResponse? remoteConfig) =>
        remoteConfig is not null
        && (!remoteConfig.Enabled || remoteConfig.CrucibleRuns is not { Enabled: true });

    /// <summary>
    /// True when the user's master switch is why the sharing is not running: it is off while the
    /// sharing's toggle is ticked, the token is usable, and the server has not ruled the sharing out.
    /// </summary>
    /// <remarks>
    /// <see cref="Occult.OccultGate.StoppedBySyncSwitch"/> explains the terms, and
    /// <see cref="Windows.SharingCardLine"/> decides what the card draws for it.
    /// </remarks>
    public static bool StoppedBySyncSwitch(PluginSettings settings, ConfigResponse? remoteConfig) =>
        settings.OnboardingComplete
        && !settings.MasterEnabled
        && settings.HasUsableToken()
        && settings.ShareCrucibleRuns
        && !ServerHasSwitchedOff(remoteConfig);

    /// <summary>
    /// What an "Off" chip for the sharing should say, or null while the server still permits it.
    /// </summary>
    /// <remarks>
    /// A paused server is named first: it stops every upload, so naming this feature's switch would
    /// send the user looking for a decision nobody made. A config with no <c>crucibleRuns</c> block
    /// comes next, since that server offers no switch to describe. Past that, the server's note (see
    /// <see cref="CrucibleRunsConfig.Note"/>), folded to one bounded line so it cannot break the chip,
    /// explains the switch, and a missing or blank note falls back to the generic line.
    /// </remarks>
    /// <param name="host">The configured website's address, which the generic sentences name.</param>
    public static string? ServerOffText(ConfigResponse? remoteConfig, string host)
    {
        if (!ServerHasSwitchedOff(remoteConfig))
            return null;

        if (remoteConfig is { Enabled: false })
            return ServerOffCopy.Paused(host);

        if (remoteConfig?.CrucibleRuns is null)
            return ServerOffCopy.NotOffered(host);

        return ServerText.SingleLine(remoteConfig.CrucibleRuns.Note) ?? ServerOffCopy.Feature(host);
    }
}
