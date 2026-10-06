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
/// master switch is off, and telling a user to fix their character's claim does not restart
/// anything while the server is paused.
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

    /// <summary>
    /// Whether the card's sentences invite a "Sync now" press: the cadence sentence and the line
    /// beneath the read-status panel.
    /// </summary>
    /// <remarks>
    /// Beyond the states where a press would upload nothing at all, a press while the server has asked
    /// the plugin to wait uploads nothing until the wait ends, so "update immediately" would be false.
    /// A halt is the exception: the press itself is what lifts it, wait or no wait. Neither sentence
    /// draws during a halt, so that answer reaches the button, through <see cref="SyncNowEnabled"/>.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    public static bool SyncNowOffered(SyncStatusKind kind, bool backingOff) =>
        !ManualSyncWouldDoNothing(kind)
        && (!backingOff || kind == SyncStatusKind.BlockedPendingUserAction);

    /// <summary>Whether the "Sync now" button is enabled.</summary>
    /// <remarks>
    /// Wherever the sentences invite a press, and in one state more: waiting for a character during a
    /// server-requested wait. There a press restarts finding the character after it gave up, which
    /// the wait does not hold back, so the button stays live although no sentence promises an upload.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    public static bool SyncNowEnabled(SyncStatusKind kind, bool backingOff) =>
        SyncNowOffered(kind, backingOff)
        || (backingOff && kind == SyncStatusKind.WaitingForCharacter);

    /// <summary>What the card says while the server has asked the plugin to wait.</summary>
    // `const` fixes the value when the code compiles.
    public const string WaitLine = "Waiting before the next upload, as the server asked.";

    /// <summary>
    /// Whether the card should add <see cref="WaitLine"/> beneath its status line: a wait is in force
    /// and nothing else on the card already says so.
    /// </summary>
    /// <remarks>
    /// The status line states the wait itself when it is the outcome of the refused upload. A wait
    /// outlives that outcome, though: it survives a relog, which clears the last outcome, and the card
    /// would otherwise say nothing about why uploads are held back. The states where a press would do
    /// nothing anyway, and a halt, have their own sentence and need no second one.
    /// </remarks>
    /// <param name="kind">The status the card is stating, from <see cref="Select"/>.</param>
    /// <param name="lastStatus">The last upload's outcome, or null when none happened this session.</param>
    /// <param name="backingOff">Whether the server has asked the plugin to wait.</param>
    // `ApiStatus?` may be null, like `ApiStatus | null`. `is A or B` is true when the value is
    // either one.
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

        // `$"...{x}..."` is an interpolated string, like a template literal. `a ? b : c` picks b when
        // a is true, else c, as in TypeScript. "Most" is load-bearing: only some collections are
        // announced by the game, and the rest wait for the sweep.
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
    /// <param name="backingOff">Whether the server's wait is still in force.</param>
    public static string DeferredUploadLine(bool backingOff) =>
        backingOff
            ? WaitLine
            : "The last upload was held back at the server's request. The next sync uploads as usual.";

    /// <summary>
    /// True when a collection the game announces is switched on, permitted by the server, and was not
    /// skipped on the last pass (before the first pass, no row has a skip reason): a collection the game
    /// will not answer for yet sends nothing on an unlock.
    /// </summary>
    private static bool AnyAnnouncedCollectionOn(IReadOnlyList<CategorySettingsRow> rows)
    {
        // `foreach` walks every item in turn, like `for (const row of rows)`.
        foreach (var row in rows)
        {
            if (row.UploadsOnUnlock && row.IsEffectivelyOn && row.SkipReason is null)
                return true;
        }

        return false;
    }
}
