using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Decides which chip the Crucible run sharing's consent card wears ("Off", "New" or none), and when
/// the user counts as having seen the card.
/// </summary>
/// <remarks>
/// <para>
/// The sharing starts off, so a user who updates would otherwise have to find its card on their own.
/// The "New" chip shares the collections' shape: "Off" outranks it, only the settings screen draws
/// it, a click counts as seeing the card, and once the card is recorded as seen the chip stays up for
/// the rest of that viewing (the window keeps a per-session flag for it).
/// </para>
/// <para>
/// It differs in two ways. An ordinary collection exists whatever the server says, so it announces
/// itself, and the wizard records it, before the first <c>/config</c> arrives. The sharing exists
/// only on a server that advertises it, so short of a click the card does neither until the server
/// has answered and permits it, rather than showing "New" and then turning "Off", or being recorded
/// before the server has offered it. And because the card sits alone at the foot of a long list, it
/// counts as seen only once its checkbox has actually been on screen, rather than whenever the
/// list is drawn.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class CrucibleBadge
{
    /// <summary>
    /// The chip the card wears: "Off" while the server rules the sharing out, else "New" or none.
    /// </summary>
    /// <param name="seen">Whether the card has already been recorded as seen.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    /// <param name="showNewChip">
    /// Whether the surface announces the card: the settings screen does, the setup wizard, which shows
    /// the card as part of its purpose, does not.
    /// </param>
    /// <param name="badgedThisSession">Whether the "New" chip went up since the window opened.</param>
    // `ConfigResponse?` may be null, like `ConfigResponse | null` in TypeScript. `CategoryBadgeKind` is
    // an enum, a fixed set of named values like a TypeScript string-literal union; it is the same
    // three-way answer the collection rows use, so both draw their chips the same way.
    public static CategoryBadgeKind BadgeFor(
        bool seen, ConfigResponse? remoteConfig, bool showNewChip, bool badgedThisSession)
    {
        // Answered first, so the card never wears "New" beside a box the server has grayed out.
        if (CrucibleGate.ServerHasSwitchedOff(remoteConfig))
            return CategoryBadgeKind.Off;

        // `a ? b : c` picks b when a is true, else c, as in TypeScript.
        return showNewChip && IsNew(seen, remoteConfig, badgedThisSession)
            ? CategoryBadgeKind.New
            : CategoryBadgeKind.None;
    }

    /// <summary>
    /// True when the card counts as new: the server permits the sharing, and the card is unseen or its
    /// chip already went up during this viewing.
    /// </summary>
    /// <remarks>
    /// Also asked by the folded Collections header (see <c>MainWindow.DrawSettings</c>).
    /// </remarks>
    /// <param name="seen">Whether the card has already been recorded as seen.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    /// <param name="badgedThisSession">Whether the "New" chip went up since the window opened.</param>
    // `=>` makes the expression after it the whole method body, like an arrow function's concise body.
    public static bool IsNew(bool seen, ConfigResponse? remoteConfig, bool badgedThisSession) =>
        ServerPermits(remoteConfig) && (!seen || badgedThisSession);

    /// <summary>True when this drawing of the card should record it as seen.</summary>
    /// <param name="seen">Whether the card has already been recorded as seen.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    /// <param name="onScreen">Whether the card's checkbox was inside the visible part of the window.</param>
    /// <param name="clicked">Whether the user clicked the card's checkbox on this frame.</param>
    /// <remarks>
    /// A click always counts: the user has plainly seen the card, and a "New" chip appearing later
    /// beside a box they ticked would be wrong. Otherwise the card counts only when it was on screen
    /// while the server permitted the sharing, the viewing the chip was waiting for. A card drawn
    /// grayed, or below the visible part of a long list, has not introduced anything yet.
    /// </remarks>
    public static bool ShowingRetires(bool seen, ConfigResponse? remoteConfig, bool onScreen, bool clicked) =>
        !seen && (clicked || (onScreen && ServerPermits(remoteConfig)));

    /// <summary>True when the server has answered and permits the sharing.</summary>
    // `is not null` is a null check; `&&` stops at the first false, so the second half only runs
    // once there is a config.
    private static bool ServerPermits(ConfigResponse? remoteConfig) =>
        remoteConfig is not null && !CrucibleGate.ServerHasSwitchedOff(remoteConfig);
}
