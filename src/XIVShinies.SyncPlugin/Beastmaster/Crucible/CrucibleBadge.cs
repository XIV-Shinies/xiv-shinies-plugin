using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Decides which chip the Crucible sharing's consent card wears ("Off", "New" or none), and when the
/// user counts as having seen the card.
/// </summary>
/// <remarks>
/// The sharing starts off, so a "New" chip announces it, ruled like the collections' chips: "Off"
/// outranks it, only the settings screen draws it, a click counts as seeing the card, and the chip
/// stays up for the rest of the viewing that recorded it. Unlike a collection, the sharing exists only
/// where the server advertises it, so short of a click the card neither announces itself nor is
/// recorded until the server permits it, and never shows "New" and then turns "Off". It sits alone at
/// the foot of a long list, so it counts as seen only once its checkbox has been on screen.
/// </remarks>
public static class CrucibleBadge
{
    /// <summary>
    /// The chip the card wears: "Off" while the server rules the sharing out, else "New" or none.
    /// </summary>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    /// <param name="showNewChip">
    /// Whether the surface announces the card: the settings screen does, and the setup wizard, which
    /// shows the card as part of its purpose, does not.
    /// </param>
    /// <param name="badgedThisSession">Whether the "New" chip went up since the window opened.</param>
    public static CategoryBadgeKind BadgeFor(
        bool seen, ConfigResponse? remoteConfig, bool showNewChip, bool badgedThisSession)
    {
        // Answered first, so the card never wears "New" beside a box the server has grayed out.
        if (CrucibleGate.ServerHasSwitchedOff(remoteConfig))
            return CategoryBadgeKind.Off;

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
    public static bool IsNew(bool seen, ConfigResponse? remoteConfig, bool badgedThisSession) =>
        ServerPermits(remoteConfig) && (!seen || badgedThisSession);

    /// <summary>True when this drawing of the card should record it as seen.</summary>
    /// <param name="onScreen">Whether the card's checkbox was inside the visible part of the window.</param>
    /// <param name="clicked">Whether the user clicked the card's checkbox on this frame.</param>
    /// <remarks>
    /// A click always counts: a "New" chip appearing later beside a box the user ticked would be
    /// wrong. Otherwise the card counts only when on screen while the server permits the sharing; a
    /// card drawn grayed, or below the visible part of a long list, has introduced nothing yet.
    /// </remarks>
    public static bool ShowingRetires(bool seen, ConfigResponse? remoteConfig, bool onScreen, bool clicked) =>
        !seen && (clicked || (onScreen && ServerPermits(remoteConfig)));

    /// <summary>True when the server has answered and permits the sharing.</summary>
    private static bool ServerPermits(ConfigResponse? remoteConfig) =>
        remoteConfig is not null && !CrucibleGate.ServerHasSwitchedOff(remoteConfig);
}
