using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// Sentences on the consent surfaces that make a claim about the user's choices, each written so it
/// is true in every state the screen can be in.
/// </summary>
/// <remarks>
/// Each claim is decided from the same rows and <c>/config</c> the controls beneath it are drawn from
/// (the wizard intro's user-driven inputs as they stood when its step opened), so a sentence and its
/// controls cannot describe different states. Kept apart from the window so the states that would
/// make a sentence false are reachable from tests.
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class ConsentCopy
{
    /// <summary>The wizard's consent step's opening sentence.</summary>
    /// <param name="anythingSwitchedOn">
    /// Whether the user has already chosen a collection or the Crucible run sharing (see
    /// <see cref="AnythingChosenOn"/>), as on a wizard opened again before its first finish: consent
    /// is saved on each click, so those choices remain.
    /// </param>
    /// <param name="trackerOffered">Whether the server lets the wizard offer the live tracker.</param>
    /// <param name="trackerOn">The user's stored live tracker choice.</param>
    /// <param name="userHasAChoice">
    /// Whether the server leaves anything to choose (see <see cref="UserHasAChoice"/>).
    /// </param>
    /// <remarks>
    /// "Start switched off" is only said while nothing is chosen on, and the tracker's "starts on"
    /// only while the server offers it and it is chosen on; otherwise the sentence would contradict
    /// the boxes beneath it, or the choices behind them (see <see cref="AnythingChosenOn"/>). With
    /// nothing to choose, as during a pause, it says so instead of inviting a choice.
    /// </remarks>
    public static string WizardIntro(
        bool anythingSwitchedOn, bool trackerOffered, bool trackerOn, bool userHasAChoice)
    {
        if (!userHasAChoice)
        {
            return "Nothing about your progress is sent: the server has everything below switched " +
                "off for now. Once it allows them, you can choose what to upload in the settings.";
        }

        // `var` lets the compiler infer the type, like an unannotated `let`. `a ? b : c` picks b when a
        // is true, else c, as in TypeScript.
        var defaults = anythingSwitchedOn
            ? "Nothing about your progress is sent unless you switch it on here. "
            : "The collections below, and sharing your Crucible runs, all start switched off — " +
              "nothing about your progress is sent unless you turn it on here. ";

        // `string.Empty` is the empty string, `""`.
        var tracker = trackerOffered && trackerOn
            ? "Sharing live Occult instance state starts on; untick it below if you would rather not. "
            : string.Empty;

        return "Choose what to upload. " + defaults + tracker + "You can change any of this later.";
    }

    /// <summary>
    /// Whether the user has already chosen anything on the consent step: a collection, or the Crucible
    /// run sharing.
    /// </summary>
    /// <remarks>
    /// Asks what is stored, not what the boxes draw: a choice the server has grayed out shows unticked
    /// but is still the user's, and it takes effect once the server permits it, so "start switched
    /// off" would misdescribe it.
    /// </remarks>
    /// <param name="rows">This frame's category rows.</param>
    /// <param name="shareCrucibleRuns">The user's stored choice for the Crucible run sharing.</param>
    // `IReadOnlyList<T>` is a list its holder cannot change, like `readonly T[]`.
    public static bool AnythingChosenOn(IReadOnlyList<CategorySettingsRow> rows, bool shareCrucibleRuns)
    {
        if (shareCrucibleRuns)
            return true;

        // `foreach` walks every item in turn, like `for (const row of rows)`.
        foreach (var row in rows)
        {
            if (row.UserEnabled)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the server leaves the user anything to choose: a collection, the Crucible run sharing
    /// or the live tracker it permits.
    /// </summary>
    /// <remarks>
    /// What a privacy card asks before saying "you choose". Under a pause, or with everything switched
    /// off, every box is grayed and the claim would be false. Before the first <c>/config</c> arrives
    /// the server has refused nothing, so the choice stands.
    /// </remarks>
    /// <param name="rows">This frame's category rows.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    // `ConfigResponse?` may be null, like `ConfigResponse | null` in TypeScript. `=>` makes the
    // expression after it the whole method body; `||` stops at the first true.
    public static bool UserHasAChoice(
        IReadOnlyList<CategorySettingsRow> rows, ConfigResponse? remoteConfig) =>
        ManifestConsent.AnyServerEnabled(rows)
        || !CrucibleGate.ServerHasSwitchedOff(remoteConfig)
        || !OccultGate.ServerHasSwitchedOff(remoteConfig);
}
