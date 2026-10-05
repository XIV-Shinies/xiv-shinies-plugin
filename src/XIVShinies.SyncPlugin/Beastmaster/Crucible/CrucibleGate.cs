using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Decides whether the Crucible run sharing may read windows and upload, right now.
/// </summary>
/// <remarks>
/// <para>
/// The sharing's consent gate. Beyond the gates every upload passes (master switch, completed
/// onboarding, usable token, the server's global kill switch), it needs the sharing's own two: the
/// user's <see cref="PluginSettings.ShareCrucibleRuns"/> toggle and the server's
/// <c>crucibleRuns.enabled</c> switch.
/// </para>
/// <para>
/// A config with no <c>crucibleRuns</c> block reads as off. This is a separate endpoint, and a
/// server that does not advertise it would answer every upload with a 404; the same rule keeps
/// the sharing quiet until the first <c>/config</c> of the session arrives.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class CrucibleGate
{
    /// <summary>True when the sharing may read the Crucible's windows and upload them.</summary>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    // A `?` after a type means the value may be null, like `T | null`.
    public static bool CanShare(PluginSettings settings, ConfigResponse? remoteConfig)
    {
        // Everything /sync requires: consent, onboarding, a usable token, and the server's global
        // kill switch.
        if (!UploadGate.CanUpload(settings, remoteConfig))
            return false;

        // The sharing's own consent.
        if (!settings.ShareCrucibleRuns)
            return false;

        // The server must have advertised the endpoint AND left it switched on. `?.` reads the
        // block only when there is a config, and `is { Enabled: true }` matches a block that is
        // present and switched on, like `config?.crucibleRuns?.enabled === true`.
        return remoteConfig?.CrucibleRuns is { Enabled: true };
    }

    /// <summary>
    /// True when the server has answered and its answer rules the sharing out: the state a
    /// settings toggle for the sharing should draw grayed and chipped "Off".
    /// </summary>
    /// <remarks>
    /// The complement of every server-side term in <see cref="CanShare"/> once a config has
    /// arrived, so a control calls the sharing unavailable on exactly the terms the gate refuses
    /// it. Before the first <c>/config</c> the server has said nothing, so a toggle keeps showing
    /// the user's own choice; the gate still refuses in that window, so the toggle shows an
    /// intention rather than a live upload.
    /// </remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    // `=>` makes the expression after it the whole method body. `is not { Enabled: true }` is true
    // for a block that is switched off and for no block at all, so a server that never advertised
    // the endpoint counts as off.
    public static bool ServerHasSwitchedOff(ConfigResponse? remoteConfig) =>
        remoteConfig is not null
        && (!remoteConfig.Enabled || remoteConfig.CrucibleRuns is not { Enabled: true });

    /// <summary>
    /// What an "Off" chip for the sharing should say, or null while the server still permits it.
    /// </summary>
    /// <remarks>
    /// A paused server is named first: it stops every upload, so naming this feature's switch
    /// instead would send the user looking for a decision about the sharing that nobody made. Past
    /// that, the server's own note explains the switch when it sent one (see
    /// <see cref="CrucibleRunsConfig.Note"/>), bounded and folded to a single line so a long or
    /// multi-line note cannot break the chip; otherwise the generic line does.
    /// </remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    public static string? ServerOffText(ConfigResponse? remoteConfig)
    {
        if (!ServerHasSwitchedOff(remoteConfig))
            return null;

        if (remoteConfig is { Enabled: false })
            return ServerOffCopy.Paused;

        // `??` uses the right-hand value when the left is null, as in TypeScript: a missing or
        // blank note falls back to the generic line.
        return ServerText.SingleLine(remoteConfig?.CrucibleRuns?.Note) ?? ServerOffCopy.Feature;
    }
}
