using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
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

    /// <summary>
    /// The plugin has stopped trying to read the logged-in character, while a collection or a sharing
    /// feature is waiting on it.
    /// </summary>
    CharacterNotRead,

    /// <summary>The server permits collections, but the user has none of them switched on.</summary>
    NothingSwitchedOnByUser,

    /// <summary>
    /// The server permits no collection at all, without having paused syncing outright.
    /// </summary>
    NothingPermittedByServer,

    /// <summary>Nobody is logged in.</summary>
    LoggedOut,

    /// <summary>Logged in, and the character is still being read.</summary>
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
/// The precedence is a promise about what the user sees; the window draws whichever kind it is
/// handed. A state outranks those below it because stating a lower one would be false or useless:
/// "will try again" is a lie while the master switch is off.
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
    /// <param name="characterNotRead">Whether the plugin has stopped trying to read the character.</param>
    /// <param name="sharingCanRun">
    /// Whether the settings and the server let the live tracker or the Crucible sharing run.
    /// </param>
    /// <param name="loggedIn">
    /// Whether a character is logged in. Asked only while no character has been identified.
    /// </param>
    public static SyncStatusKind Select(
        bool masterEnabled,
        IReadOnlyList<CategorySettingsRow> rows,
        bool blockedPendingUserAction,
        bool hasCharacter,
        bool hasUploaded,
        bool characterNotRead,
        bool sharingCanRun,
        bool loggedIn)
    {
        if (!masterEnabled)
            return SyncStatusKind.SwitchedOffByUser;

        if (ManifestConsent.ServerHasPausedEverything(rows))
            return SyncStatusKind.PausedByServer;

        if (blockedPendingUserAction)
            return SyncStatusKind.BlockedPendingUserAction;

        // Below the halt, whose press clears this too; above the quiet states, since only it says nothing
        // uploads. With nothing waiting on the character, the nothing-collected line is truer.
        if (characterNotRead && (ManifestConsent.AnyEffectivelyOn(rows) || sharingCanRun))
            return SyncStatusKind.CharacterNotRead;

        // Below the faults and above the waits: nothing is wrong, but it explains a silence. Which of
        // the two it is decides whom the sentence names, so the user is never blamed for the server.
        if (!ManifestConsent.AnyEffectivelyOn(rows))
        {
            // No rows at all is nobody's decision, so it must not blame the server. Unreachable while
            // the registry yields collectors.
            return rows.Count == 0 || ManifestConsent.AnyServerEnabled(rows)
                ? SyncStatusKind.NothingSwitchedOnByUser
                : SyncStatusKind.NothingPermittedByServer;
        }

        if (!hasCharacter)
            return loggedIn ? SyncStatusKind.WaitingForCharacter : SyncStatusKind.LoggedOut;

        return hasUploaded ? SyncStatusKind.LastUploadOutcome : SyncStatusKind.NothingUploadedYet;
    }

    /// <summary>
    /// Whether the card may describe a pipeline that is going to do something, which gates its
    /// cadence promise and its "Reading from:" panel.
    /// </summary>
    /// <remarks>
    /// Derived from the status, not the facts, so the card cannot state a pause and promise a cadence
    /// beneath it. These three are the states where nothing stops the pipeline.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    public static bool CadenceHolds(SyncStatusKind kind) =>
        kind is SyncStatusKind.WaitingForCharacter
            or SyncStatusKind.LastUploadOutcome
            or SyncStatusKind.NothingUploadedYet;

    /// <summary>
    /// Whether a "Sync now" press would add no upload at all, in a state where nothing would upload
    /// once a server-requested wait ended either, so the button should not offer to.
    /// </summary>
    /// <remarks>
    /// The first two are refused at the upload gate, the next two collect an empty snapshot the
    /// uploader skips, and while nobody is logged in a press merges into the login sync. In all five
    /// the "Syncing" flash would reassure beneath a line saying nothing will upload.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    public static bool ManualSyncWouldDoNothing(SyncStatusKind kind) =>
        kind is SyncStatusKind.SwitchedOffByUser
            or SyncStatusKind.PausedByServer
            or SyncStatusKind.NothingSwitchedOnByUser
            or SyncStatusKind.NothingPermittedByServer
            or SyncStatusKind.LoggedOut;

    /// <summary>
    /// Whether the card's sentences invite a "Sync now" press: the cadence sentence and the line
    /// beneath the read-status panel.
    /// </summary>
    /// <remarks>
    /// Two states beyond <see cref="ManualSyncWouldDoNothing"/> bring nothing sooner: while the
    /// character is being read a press merges into the login sync, and during a server-requested wait
    /// it uploads nothing until the wait ends. A halt still offers the press during a wait, since the
    /// press lifts the halt. The character wait stays out of <see cref="ManualSyncWouldDoNothing"/>
    /// because a server-requested wait still needs its line there (see <see cref="NeedsWaitLine"/>).
    /// Neither sentence draws during a halt, so there the answer reaches only the button (see
    /// <see cref="SyncNowEnabled"/>).
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    public static bool SyncNowOffered(SyncStatusKind kind, bool backingOff) =>
        !ManualSyncWouldDoNothing(kind)
        && kind != SyncStatusKind.WaitingForCharacter
        && (!backingOff || kind == SyncStatusKind.BlockedPendingUserAction);

    /// <summary>Whether the "Sync now" button is enabled.</summary>
    /// <remarks>
    /// Wherever the sentences invite a press, and in one state more: a character the plugin stopped
    /// trying to read, during a server-requested wait. There a press starts reading the character
    /// again, which the wait does not hold back, so the button stays live although no upload follows
    /// until the wait ends.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    public static bool SyncNowEnabled(SyncStatusKind kind, bool backingOff) =>
        SyncNowOffered(kind, backingOff)
        || (backingOff && kind == SyncStatusKind.CharacterNotRead);

    /// <summary>What the card says in <see cref="SyncStatusKind.CharacterNotRead"/>.</summary>
    public const string CharacterNotReadSentence =
        "Your character could not be read, so nothing is uploaded. Press Sync now to try again.";

    /// <summary>What the card says in <see cref="SyncStatusKind.WaitingForCharacter"/>.</summary>
    public const string WaitingForCharacterSentence =
        "Reading your character — syncing starts once it has been read.";

    /// <summary>What the card says in <see cref="SyncStatusKind.LoggedOut"/>.</summary>
    public const string LoggedOutSentence = "Log in to a character to start syncing.";

    /// <summary>What the card says while the website has asked the plugin to wait.</summary>
    /// <param name="host">The configured website's address.</param>
    public static string WaitLine(string host) => $"Waiting before the next upload, as {host} asked.";

    /// <summary>
    /// Whether the card should add <see cref="WaitLine"/> beneath its status line: a wait is in force
    /// and nothing else on the card already says so.
    /// </summary>
    /// <remarks>
    /// The status line already states a wait that is the refused upload's outcome, but a wait survives
    /// a relog, which clears that outcome. A halt and the states in
    /// <see cref="ManualSyncWouldDoNothing"/> need no second line: their own already says why nothing
    /// uploads.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    /// <param name="lastStatus">The last upload's outcome, or null when none happened this session.</param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    public static bool NeedsWaitLine(SyncStatusKind kind, ApiStatus? lastStatus, bool backingOff)
    {
        if (!backingOff || ManualSyncWouldDoNothing(kind) || kind == SyncStatusKind.BlockedPendingUserAction)
            return false;

        var statusLineSaysIt = kind == SyncStatusKind.LastUploadOutcome
            && lastStatus is ApiStatus.RateLimited or ApiStatus.SyncDisabled;
        return !statusLineSaysIt;
    }

    /// <summary>
    /// The sentence that sets the user's expectation of when their collections reach the site.
    /// </summary>
    /// <remarks>
    /// It promises uploads within seconds only while a switched-on collection is one the game
    /// announces (see <see cref="CategorySettingsRow.UploadsOnUnlock"/>) and was not skipped on the
    /// last pass, and never during a server-requested wait, which holds those uploads back too. It
    /// invites a "Sync now" press only while one is on offer. The cadence is the scheduler's live
    /// interval, which the server tunes.
    /// </remarks>
    /// <param name="rows">This frame's category rows.</param>
    /// <param name="fullSyncInterval">How often the scheduled sweep runs.</param>
    /// <param name="syncNowOffered">
    /// Whether the card offers "Sync now" (see <see cref="SyncNowOffered"/>).
    /// </param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    public static string CadenceSentence(
        IReadOnlyList<CategorySettingsRow> rows,
        TimeSpan fullSyncInterval,
        bool syncNowOffered,
        bool backingOff)
    {
        var interval = TimeText.Interval(fullSyncInterval);

        // "Most" is load-bearing: only some collections are announced by the game, and the rest wait
        // for the sweep.
        var schedule = !backingOff && AnyAnnouncedCollectionOn(rows)
            ? $"Most new unlocks upload within seconds. Everything else syncs automatically every {interval}"
            : $"Your collections sync automatically every {interval}";

        return syncNowOffered ? $"{schedule} — press Sync now to update immediately." : $"{schedule}.";
    }

    /// <summary>
    /// The line beneath the read-status panel's unread sources: what picks up a fix made in game.
    /// </summary>
    /// <param name="syncNowOffered">
    /// Whether the card offers "Sync now" (see <see cref="SyncNowOffered"/>).
    /// </param>
    public static string MissingFollowUp(bool syncNowOffered) =>
        "Where a line above names an action, do it in game, " +
        (syncNowOffered ? "then press Sync now." : "and the next sync picks it up.");

    /// <summary>
    /// The status line for an upload the server answered with "wait" (a 429 or 503), which stays the
    /// last outcome after the wait itself has ended.
    /// </summary>
    /// <param name="backingOff">Whether the website's wait is still in force.</param>
    /// <param name="host">The configured website's address.</param>
    public static string DeferredUploadLine(bool backingOff, string host) =>
        backingOff
            ? WaitLine(host)
            : $"The last upload was held back at {host}'s request. The next sync uploads as usual.";

    /// <summary>
    /// True when a switched-on, permitted collection the game announces was not skipped last pass
    /// (before the first pass, none was).
    /// </summary>
    /// <remarks>A collection the game will not answer for yet sends nothing on an unlock.</remarks>
    private static bool AnyAnnouncedCollectionOn(IReadOnlyList<CategorySettingsRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.UploadsOnUnlock && row.IsEffectivelyOn && row.SkipReason is null)
                return true;
        }

        return false;
    }
}
