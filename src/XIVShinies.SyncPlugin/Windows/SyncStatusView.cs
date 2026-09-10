using System.Collections.Generic;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// Which of the sync card's mutually exclusive states the user is in.
/// </summary>
/// <remarks>
/// The order the members are declared in is the order they outrank each other, and
/// <see cref="SyncStatusView.Select"/> is where that order lives.
/// </remarks>
public enum SyncStatusKind
{
    /// <summary>The user's own master switch is off.</summary>
    SwitchedOffByUser,

    /// <summary>The server has paused syncing for everyone.</summary>
    PausedByServer,

    /// <summary>Syncing has stopped for something only the user can resolve.</summary>
    BlockedPendingUserAction,

    /// <summary>The server permits collections, but the user has none of them switched on.</summary>
    NothingSwitchedOnByUser,

    /// <summary>
    /// The server permits no collection at all, without having paused syncing outright.
    /// </summary>
    NothingPermittedByServer,

    /// <summary>No character is loaded yet.</summary>
    WaitingForCharacter,

    /// <summary>An upload has happened this session, and its outcome is the status.</summary>
    LastUploadOutcome,

    /// <summary>Everything is ready and nothing has been uploaded yet.</summary>
    NothingUploadedYet,
}

/// <summary>
/// Chooses which status the sync card states, from the facts that decide it.
/// </summary>
/// <remarks>
/// <para>
/// The precedence is the whole content of this class, and it is a promise about what the user
/// sees rather than an implementation detail — so it lives where a test can reach it, the same
/// reason <see cref="CategorySettingsView.BadgeFor"/> does. The window renders whichever kind it is
/// handed and decides nothing.
/// </para>
/// <para>
/// Each state overrides the ones below it because stating a lower one while a higher one holds
/// would be false or useless: reporting the last upload's "will try again" is a lie while the
/// master switch is off, and telling a user to claim their character does not restart anything
/// while the server is paused.
/// </para>
/// </remarks>
public static class SyncStatusView
{
    /// <summary>Picks the one status the card should state.</summary>
    /// <param name="masterEnabled">The user's own on/off switch for syncing.</param>
    /// <param name="rows">This frame's category rows.</param>
    /// <param name="blockedPendingUserAction">
    /// Whether syncing has stopped for something only the user can resolve.
    /// </param>
    /// <param name="hasCharacter">Whether a character has been identified.</param>
    /// <param name="hasUploaded">Whether an upload has completed this session.</param>
    public static SyncStatusKind Select(
        bool masterEnabled,
        IReadOnlyList<CategorySettingsRow> rows,
        bool blockedPendingUserAction,
        bool hasCharacter,
        bool hasUploaded)
    {
        if (!masterEnabled)
            return SyncStatusKind.SwitchedOffByUser;

        if (ManifestConsent.ServerHasPausedEverything(rows))
            return SyncStatusKind.PausedByServer;

        if (blockedPendingUserAction)
            return SyncStatusKind.BlockedPendingUserAction;

        // Below the broken states and above the waiting ones: nothing here is wrong, so it must not
        // outrank a fault, but it does explain a silence the states below would leave unexplained.
        //
        // Which of the two it is decides who the sentence may name. A server that has switched
        // every collection off individually has not paused — so the rows are the only explanation
        // on screen, and they sit inside a card the user can collapse. Saying "you have nothing
        // switched on" there would blame them for someone else's decision.
        if (!ManifestConsent.AnyEffectivelyOn(rows))
        {
            // No rows at all is nobody's decision — there is no collection for the server to have
            // refused — so it must not draw the sentence that blames one. Unreachable while the
            // registry always yields collectors; answered explicitly because the alternative
            // reading is the accusatory one.
            return rows.Count == 0 || ManifestConsent.AnyServerEnabled(rows)
                ? SyncStatusKind.NothingSwitchedOnByUser
                : SyncStatusKind.NothingPermittedByServer;
        }

        if (!hasCharacter)
            return SyncStatusKind.WaitingForCharacter;

        return hasUploaded ? SyncStatusKind.LastUploadOutcome : SyncStatusKind.NothingUploadedYet;
    }

    /// <summary>
    /// Whether the card may describe a pipeline that is going to do something — what gates its
    /// cadence promise and its "Reading from:" panel.
    /// </summary>
    /// <remarks>
    /// Derived from the status rather than re-asked of the facts, because two answers to one
    /// question can disagree: a card that states a pause and then promises a cadence beneath it has
    /// contradicted itself on the same screen. The three states below are exactly the ones where
    /// nothing is stopping the pipeline — everything above them in the ladder is a reason it is not
    /// running.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    public static bool CadenceHolds(SyncStatusKind kind) =>
        kind is SyncStatusKind.WaitingForCharacter
            or SyncStatusKind.LastUploadOutcome
            or SyncStatusKind.NothingUploadedYet;

    /// <summary>
    /// Whether pressing "Sync now" would achieve nothing, so the button should not offer to.
    /// </summary>
    /// <remarks>
    /// A press produces no upload in any of these: the first two are refused at the upload gate,
    /// and the last two collect an empty snapshot the uploader then skips. In all four the button
    /// would still play its "Syncing" flash — a moment of reassurance directly beneath a line
    /// saying nothing is syncing.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    public static bool ManualSyncWouldDoNothing(SyncStatusKind kind) =>
        kind is SyncStatusKind.SwitchedOffByUser
            or SyncStatusKind.PausedByServer
            or SyncStatusKind.NothingSwitchedOnByUser
            or SyncStatusKind.NothingPermittedByServer;
}
