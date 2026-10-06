using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

/// <summary>
/// The sync card's precedence ladder: which single fact the card states when several are true.
/// </summary>
/// <remarks>
/// Most cases here pin an <b>override</b> rather than a lone state, because the ladder's whole job
/// is deciding what to say when more than one thing is true at once, and a test per state in
/// isolation would pass against almost any ordering. The two healthy-pipeline cases and the
/// empty-list case are the exceptions: they pin the ladder's floor and its one boundary.
/// </remarks>
public class SyncStatusViewTests
{
    private static CategorySettingsRow Row(
        bool userEnabled = true,
        bool serverEnabled = true,
        bool serverGloballyOff = false) => new()
    {
        Key = "quests",
        DisplayName = "Quests",
        Section = "Fakes",
        WhatGetsSent = "what quests sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        ServerGloballyOff = serverGloballyOff,
        UsesItemManifest = false,
    };

    /// <summary>A collection both sides permit, so nothing below the ladder's top is forced.</summary>
    private static IReadOnlyList<CategorySettingsRow> Running() => new[] {Row()};

    /// <summary>Every row off the way a server pause turns them off.</summary>
    private static IReadOnlyList<CategorySettingsRow> Paused() =>
        new[] {Row(serverEnabled: false, serverGloballyOff: true)};

    [Fact]
    public void A_healthy_pipeline_that_has_uploaded_states_the_last_outcome()
    {
        Assert.Equal(
            SyncStatusKind.LastUploadOutcome,
            SyncStatusView.Select(true, Running(), false, hasCharacter: true, hasUploaded: true));
    }

    [Fact]
    public void A_healthy_pipeline_that_has_not_uploaded_says_so()
    {
        Assert.Equal(
            SyncStatusKind.NothingUploadedYet,
            SyncStatusView.Select(true, Running(), false, hasCharacter: true, hasUploaded: false));
    }

    // The user's own switch beats everything: reporting the last upload's "will try again" would
    // be a lie, because the plugin will not try again until they switch it back.
    [Fact]
    public void The_users_own_switch_outranks_every_other_state()
    {
        Assert.Equal(
            SyncStatusKind.SwitchedOffByUser,
            SyncStatusView.Select(false, Paused(), true, hasCharacter: false, hasUploaded: true));
    }

    // A paused server explains the silence completely, and the actions the blocked state asks for
    // would not restart anything while the pause holds.
    [Fact]
    public void A_paused_server_outranks_a_block_the_user_could_otherwise_fix()
    {
        Assert.Equal(
            SyncStatusKind.PausedByServer,
            SyncStatusView.Select(true, Paused(), true, hasCharacter: true, hasUploaded: true));
    }

    // A pause turns every row off, so "nothing is switched on" is true at the same moment — and
    // must not be what the card says, because it would blame the user for the server's outage.
    [Fact]
    public void A_paused_server_is_not_reported_as_the_user_having_switched_everything_off()
    {
        Assert.Equal(
            SyncStatusKind.PausedByServer,
            SyncStatusView.Select(true, Paused(), false, hasCharacter: true, hasUploaded: true));
    }

    // A real fault outranks an idle pipeline: the user can fix the token, and being told about
    // empty checkboxes instead would hide the thing that actually needs doing.
    [Fact]
    public void A_block_outranks_nothing_being_switched_on()
    {
        var nothingOn = new[] {Row(userEnabled: false)};

        Assert.Equal(
            SyncStatusKind.BlockedPendingUserAction,
            SyncStatusView.Select(true, nothingOn, true, hasCharacter: true, hasUploaded: true));
    }

    // Nothing switched on is the user's own doing and is worth saying before the card falls
    // through to a character or upload state that would imply work is pending.
    [Fact]
    public void Nothing_switched_on_outranks_waiting_for_a_character()
    {
        var nothingOn = new[] {Row(userEnabled: false)};

        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            SyncStatusView.Select(true, nothingOn, false, hasCharacter: false, hasUploaded: false));
    }

    [Fact]
    public void A_missing_character_outranks_an_upload_outcome()
    {
        Assert.Equal(
            SyncStatusKind.WaitingForCharacter,
            SyncStatusView.Select(true, Running(), false, hasCharacter: false, hasUploaded: true));
    }

    // An empty list is nobody's decision, so it must not draw the sentence blaming the server —
    // there is no collection for a server to have refused.
    [Fact]
    public void No_rows_at_all_is_not_blamed_on_the_server()
    {
        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            SyncStatusView.Select(
                true, Array.Empty<CategorySettingsRow>(), false, hasCharacter: true,
                hasUploaded: true));
    }

    // The server can switch every collection off one by one without pausing. That is not the
    // user's doing, so the card must not say they switched everything off — and the per-row "Off"
    // chips that would explain it sit inside a card they can collapse.
    [Fact]
    public void Every_collection_switched_off_by_the_server_is_not_blamed_on_the_user()
    {
        var serverOff = new[] {Row(userEnabled: true, serverEnabled: false)};

        Assert.Equal(
            SyncStatusKind.NothingPermittedByServer,
            SyncStatusView.Select(true, serverOff, false, hasCharacter: true, hasUploaded: true));
    }

    // The mirror of the case above: the server permits it and the user declined, which is theirs
    // to see in their own empty checkboxes.
    [Fact]
    public void Every_collection_switched_off_by_the_user_is_not_blamed_on_the_server()
    {
        var userOff = new[] {Row(userEnabled: false, serverEnabled: true)};

        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            SyncStatusView.Select(true, userOff, false, hasCharacter: true, hasUploaded: true));
    }

    // --- What the card may promise, and what the button may offer -----------------------------

    // The cadence promise holds only where nothing is stopping the pipeline. Derived from the
    // status rather than re-asked of the facts, so a card cannot state a pause and promise a
    // cadence beneath it.
    [Theory]
    [InlineData(SyncStatusKind.WaitingForCharacter, true)]
    [InlineData(SyncStatusKind.LastUploadOutcome, true)]
    [InlineData(SyncStatusKind.NothingUploadedYet, true)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, false)]
    [InlineData(SyncStatusKind.PausedByServer, false)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, false)]
    public void The_cadence_promise_holds_only_where_nothing_stops_the_pipeline(
        SyncStatusKind kind, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.CadenceHolds(kind));
    }

    // Sync now is refused only where pressing it would produce no upload while the button still
    // played its "Syncing" flash — two states the upload gate refuses, two that collect nothing
    // for it to send. The halt deliberately keeps it live: that state's own copy tells the user
    // to press it.
    [Theory]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true)]
    [InlineData(SyncStatusKind.PausedByServer, true)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, false)]
    [InlineData(SyncStatusKind.LastUploadOutcome, false)]
    [InlineData(SyncStatusKind.NothingUploadedYet, false)]
    public void Sync_now_is_refused_only_where_pressing_it_would_do_nothing(
        SyncStatusKind kind, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.ManualSyncWouldDoNothing(kind));
    }

    // --- Whether the card offers "Sync now" --------------------------------------------------------

    // Every status, with and without a server-requested wait. The sentences invite a press only where
    // one uploads something now; a halt is the exception, since the press itself is what lifts it.
    [Theory]
    [InlineData(SyncStatusKind.SwitchedOffByUser, false, false)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true, false)]
    [InlineData(SyncStatusKind.PausedByServer, false, false)]
    [InlineData(SyncStatusKind.PausedByServer, true, false)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, true, true)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, false, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, false, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, false, true)]
    [InlineData(SyncStatusKind.WaitingForCharacter, true, false)]
    [InlineData(SyncStatusKind.LastUploadOutcome, false, true)]
    [InlineData(SyncStatusKind.LastUploadOutcome, true, false)]
    [InlineData(SyncStatusKind.NothingUploadedYet, false, true)]
    [InlineData(SyncStatusKind.NothingUploadedYet, true, false)]
    public void The_sentences_invite_a_press_only_where_one_uploads_now(
        SyncStatusKind kind, bool backingOff, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.SyncNowOffered(kind, backingOff));
    }

    // The button follows the sentences, plus one state: waiting for a character during a wait, where a
    // press restarts finding the character, which the wait does not hold back.
    [Theory]
    [InlineData(SyncStatusKind.SwitchedOffByUser, false, false)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true, false)]
    [InlineData(SyncStatusKind.PausedByServer, false, false)]
    [InlineData(SyncStatusKind.PausedByServer, true, false)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, true, true)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, false, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, false, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, false, true)]
    [InlineData(SyncStatusKind.WaitingForCharacter, true, true)]
    [InlineData(SyncStatusKind.LastUploadOutcome, false, true)]
    [InlineData(SyncStatusKind.LastUploadOutcome, true, false)]
    [InlineData(SyncStatusKind.NothingUploadedYet, false, true)]
    [InlineData(SyncStatusKind.NothingUploadedYet, true, false)]
    public void The_button_is_live_where_a_press_does_something(
        SyncStatusKind kind, bool backingOff, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.SyncNowEnabled(kind, backingOff));
    }

    // --- The wait line ---------------------------------------------------------------------------------

    // The refused upload's own outcome states the wait, so no second line is added under it.
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void A_status_line_that_states_the_wait_needs_no_second_one(ApiStatus lastStatus)
    {
        Assert.False(
            SyncStatusView.NeedsWaitLine(SyncStatusKind.LastUploadOutcome, lastStatus, backingOff: true));
    }

    // A wait outlives the outcome that started it: after a relog the card states "nothing uploaded
    // yet" or a wait for a character, and the wait line is what says uploads are held back.
    // Every row has no last outcome, so the null is written once, in the call.
    [Theory]
    [InlineData(SyncStatusKind.NothingUploadedYet)]
    [InlineData(SyncStatusKind.WaitingForCharacter)]
    public void A_wait_the_status_line_does_not_state_gets_its_own_line(SyncStatusKind kind)
    {
        Assert.True(SyncStatusView.NeedsWaitLine(kind, lastStatus: null, backingOff: true));
    }

    // An outcome other than the wait's, still on the line while a wait holds, does not state the wait.
    [Fact]
    public void A_different_outcome_on_the_line_still_gets_the_wait_line()
    {
        Assert.True(SyncStatusView.NeedsWaitLine(
            SyncStatusKind.LastUploadOutcome, ApiStatus.NetworkError, backingOff: true));
    }

    // No wait, no line; and the states with a sentence of their own (a press would do nothing, or a
    // halt) need none.
    [Theory]
    [InlineData(SyncStatusKind.LastUploadOutcome, false)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true)]
    [InlineData(SyncStatusKind.PausedByServer, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, true)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true)]
    public void No_wait_line_without_a_wait_or_where_another_sentence_covers_it(
        SyncStatusKind kind, bool backingOff)
    {
        Assert.False(SyncStatusView.NeedsWaitLine(kind, lastStatus: null, backingOff));
    }

    // --- The cadence sentence ------------------------------------------------------------------------

    /// <summary>A switched-on collection that does, or does not, upload as soon as it unlocks.</summary>
    // `with { ... }` copies the record with one property changed, like `{ ...row, uploadsOnUnlock }`.
    private static CategorySettingsRow Announced(bool uploadsOnUnlock) =>
        Row() with { UploadsOnUnlock = uploadsOnUnlock };

    /// <summary>How often the scheduled sweep runs in these tests.</summary>
    // `static readonly` builds the value once, before the class is first used.
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    // Collections the game announces upload within seconds, so the sentence may say so while one of
    // them is switched on, not skipped on the last pass, and not held back by a wait.
    [Fact]
    public void The_cadence_promises_seconds_while_an_announced_collection_is_on()
    {
        Assert.Equal(
            "Most new unlocks upload within seconds. Everything else syncs automatically every " +
            "30 minutes — press Sync now to update immediately.",
            SyncStatusView.CadenceSentence(
                new[] { Announced(true) }, HalfHour, syncNowOffered: true, backingOff: false));
    }

    // Without an announced collection that counts, only the schedule is promised. An announced one does
    // not count when the user switched it off, the server switched it off, or it was not read on the
    // last pass (it sends nothing on an unlock until it is).
    [Fact]
    public void The_cadence_promises_only_the_schedule_when_no_announced_collection_counts()
    {
        var rows = new[]
        {
            Announced(false),
            Row(userEnabled: false) with { UploadsOnUnlock = true },
            Row(serverEnabled: false) with { UploadsOnUnlock = true },
            Announced(true) with { SkipReason = CollectSkipReasons.AchievementListNotLoaded },
        };

        Assert.Equal(
            "Your collections sync automatically every 30 minutes — press Sync now to update immediately.",
            SyncStatusView.CadenceSentence(rows, HalfHour, syncNowOffered: true, backingOff: false));
    }

    // While "Sync now" is not on offer, the sentence does not invite a press, whichever opening it has.
    [Theory]
    [InlineData(
        true, "Most new unlocks upload within seconds. Everything else syncs automatically every 1 hour.")]
    [InlineData(false, "Your collections sync automatically every 1 hour.")]
    public void The_cadence_does_not_invite_a_press_that_is_not_offered(bool announced, string expected)
    {
        Assert.Equal(
            expected,
            SyncStatusView.CadenceSentence(
                new[] { Announced(announced) },
                TimeSpan.FromHours(1),
                syncNowOffered: false,
                backingOff: false));
    }

    // A wait holds back unlock uploads as well as sweeps, so "within seconds" is not promised during one.
    [Fact]
    public void The_cadence_promises_no_seconds_during_a_wait()
    {
        Assert.Equal(
            "Your collections sync automatically every 30 minutes.",
            SyncStatusView.CadenceSentence(
                new[] { Announced(true) }, HalfHour, syncNowOffered: false, backingOff: true));
    }

    // The line beneath the read-status panel tells the user what picks up a fix made in game. It
    // speaks only of the lines that name an action: some unread lines have none to name.
    [Theory]
    [InlineData(true, "Where a line above names an action, do it in game, then press Sync now.")]
    [InlineData(false, "Where a line above names an action, do it in game, and the next sync picks it up.")]
    public void The_follow_up_names_sync_now_only_while_it_is_offered(bool syncNowOffered, string expected)
    {
        Assert.Equal(expected, SyncStatusView.MissingFollowUp(syncNowOffered));
    }

    // --- The deferred upload's status line -----------------------------------------------------------

    // While the wait holds, the line says so; once it has ended, the same outcome no longer means
    // waiting, so the line says what happens next instead.
    [Theory]
    [InlineData(true, SyncStatusView.WaitLine)]
    [InlineData(
        false, "The last upload was held back at the server's request. The next sync uploads as usual.")]
    public void A_deferred_upload_says_whether_the_wait_still_holds(bool backingOff, string expected)
    {
        Assert.Equal(expected, SyncStatusView.DeferredUploadLine(backingOff));
    }
}
