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
/// Most cases pin an override rather than a lone state: a test per state in isolation would pass
/// against almost any ordering. The two healthy-pipeline cases and the empty-list case pin the
/// ladder's floor and its one boundary.
/// </remarks>
public class SyncStatusViewTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

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

    private static SyncStatusKind Select(
        bool masterEnabled,
        IReadOnlyList<CategorySettingsRow> rows,
        bool blockedPendingUserAction,
        bool hasCharacter,
        bool hasUploaded,
        bool characterNotRead = false,
        bool sharingCanRun = false,
        bool loggedIn = true) =>
        SyncStatusView.Select(
            masterEnabled, rows, blockedPendingUserAction, hasCharacter, hasUploaded,
            characterNotRead, sharingCanRun, loggedIn);

    [Fact]
    public void A_healthy_pipeline_that_has_uploaded_states_the_last_outcome()
    {
        Assert.Equal(
            SyncStatusKind.LastUploadOutcome,
            Select(true, Running(), false, hasCharacter: true, hasUploaded: true));
    }

    [Fact]
    public void A_healthy_pipeline_that_has_not_uploaded_says_so()
    {
        Assert.Equal(
            SyncStatusKind.NothingUploadedYet,
            Select(true, Running(), false, hasCharacter: true, hasUploaded: false));
    }

    // The last upload's "will try again" would be false: nothing tries until the user switches it back
    // on.
    [Fact]
    public void The_users_own_switch_outranks_every_other_state()
    {
        Assert.Equal(
            SyncStatusKind.SwitchedOffByUser,
            Select(false, Paused(), true, hasCharacter: false, hasUploaded: true));
    }

    // The fixes the blocked state asks for would restart nothing while the pause holds.
    [Fact]
    public void A_paused_server_outranks_a_block_the_user_could_otherwise_fix()
    {
        Assert.Equal(
            SyncStatusKind.PausedByServer,
            Select(true, Paused(), true, hasCharacter: true, hasUploaded: true));
    }

    // A pause also turns every row off, and "nothing is switched on" would send the user to the
    // plugin's checkboxes when the switch that is off is on the website.
    [Fact]
    public void A_paused_server_is_not_reported_as_the_user_having_switched_everything_off()
    {
        Assert.Equal(
            SyncStatusKind.PausedByServer,
            Select(true, Paused(), false, hasCharacter: true, hasUploaded: true));
    }

    // The user can fix the token, and talk of empty checkboxes would hide what needs doing.
    [Fact]
    public void A_block_outranks_nothing_being_switched_on()
    {
        var nothingOn = new[] {Row(userEnabled: false)};

        Assert.Equal(
            SyncStatusKind.BlockedPendingUserAction,
            Select(true, nothingOn, true, hasCharacter: true, hasUploaded: true));
    }

    // A character or upload state would imply work is pending.
    [Fact]
    public void Nothing_switched_on_outranks_waiting_for_a_character()
    {
        var nothingOn = new[] {Row(userEnabled: false)};

        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            Select(true, nothingOn, false, hasCharacter: false, hasUploaded: false));
    }

    [Fact]
    public void A_missing_character_outranks_an_upload_outcome()
    {
        Assert.Equal(
            SyncStatusKind.WaitingForCharacter,
            Select(true, Running(), false, hasCharacter: false, hasUploaded: true));
    }

    // Logging in is what starts syncing. The last outcome is cleared at logout; it is set here to pin
    // the order.
    [Fact]
    public void Nobody_logged_in_is_not_a_wait_for_a_character()
    {
        Assert.Equal(
            SyncStatusKind.LoggedOut,
            Select(true, Running(), false, hasCharacter: false, hasUploaded: true, loggedIn: false));
    }

    // The line saying why nothing is collected stays the truer one while logged out.
    [Fact]
    public void Nothing_switched_on_outranks_nobody_being_logged_in()
    {
        var nothingOn = new[] {Row(userEnabled: false)};
        var serverOff = new[] {Row(userEnabled: true, serverEnabled: false)};

        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            Select(true, nothingOn, false, hasCharacter: false, hasUploaded: false, loggedIn: false));
        Assert.Equal(
            SyncStatusKind.NothingPermittedByServer,
            Select(true, serverOff, false, hasCharacter: false, hasUploaded: false, loggedIn: false));
    }

    // All three hold while logged out: the switch is a setting, and /config is polled at the title
    // screen too. The halt matters most: its fix is a press, which LoggedOut would refuse.
    [Fact]
    public void The_switch_a_pause_and_a_halt_outrank_nobody_being_logged_in()
    {
        Assert.Equal(
            SyncStatusKind.SwitchedOffByUser,
            Select(false, Running(), false, hasCharacter: false, hasUploaded: false, loggedIn: false));
        Assert.Equal(
            SyncStatusKind.PausedByServer,
            Select(true, Paused(), false, hasCharacter: false, hasUploaded: false, loggedIn: false));
        Assert.Equal(
            SyncStatusKind.BlockedPendingUserAction,
            Select(true, Running(), true, hasCharacter: false, hasUploaded: false, loggedIn: false));
    }

    // Logging in is asked only while no character has been identified.
    [Fact]
    public void An_identified_character_is_never_reported_as_logged_out()
    {
        Assert.Equal(
            SyncStatusKind.NothingUploadedYet,
            Select(true, Running(), false, hasCharacter: true, hasUploaded: false, loggedIn: false));
    }

    [Fact]
    public void The_character_wait_sentences_are_pinned()
    {
        Assert.Equal(
            "Reading your character — syncing starts once it has been read.",
            SyncStatusView.WaitingForCharacterSentence);
        Assert.Equal("Log in to a character to start syncing.", SyncStatusView.LoggedOutSentence);
    }

    // An empty list is nobody's decision: there is no collection for the server to have refused.
    [Fact]
    public void No_rows_at_all_is_not_blamed_on_the_server()
    {
        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            Select(
                true, Array.Empty<CategorySettingsRow>(), false, hasCharacter: true,
                hasUploaded: true));
    }

    // The server can switch every collection off without pausing, and the per-row "Off" chips that
    // show it sit in a card the user can collapse.
    [Fact]
    public void Every_collection_switched_off_by_the_server_is_not_blamed_on_the_user()
    {
        var serverOff = new[] {Row(userEnabled: true, serverEnabled: false)};

        Assert.Equal(
            SyncStatusKind.NothingPermittedByServer,
            Select(true, serverOff, false, hasCharacter: true, hasUploaded: true));
    }

    [Fact]
    public void Every_collection_switched_off_by_the_user_is_not_blamed_on_the_server()
    {
        var userOff = new[] {Row(userEnabled: false, serverEnabled: true)};

        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            Select(true, userOff, false, hasCharacter: true, hasUploaded: true));
    }

    // --- A character the plugin stopped trying to read ---

    // Nothing uploads until Sync now or the next login reads the character again, so the ordinary
    // wait's promise of a start would be false.
    [Fact]
    public void A_character_that_could_not_be_read_outranks_waiting_for_one()
    {
        Assert.Equal(
            SyncStatusKind.CharacterNotRead,
            Select(true, Running(), false, hasCharacter: false, hasUploaded: false, characterNotRead: true));
    }

    // A player who only shares reads this card too, and the section holding the sharing cards can be
    // collapsed.
    [Fact]
    public void A_character_a_sharing_waits_on_outranks_nothing_switched_on()
    {
        var nothingOn = new[] {Row(userEnabled: false)};
        var serverOff = new[] {Row(userEnabled: true, serverEnabled: false)};

        Assert.Equal(
            SyncStatusKind.CharacterNotRead,
            Select(true, nothingOn, false, false, false, characterNotRead: true, sharingCanRun: true));
        Assert.Equal(
            SyncStatusKind.CharacterNotRead,
            Select(true, serverOff, false, false, false, characterNotRead: true, sharingCanRun: true));
    }

    // With nothing collected or shared, the character holds nothing back.
    [Fact]
    public void A_character_nothing_waits_on_is_not_named()
    {
        var nothingOn = new[] {Row(userEnabled: false)};

        Assert.Equal(
            SyncStatusKind.NothingSwitchedOnByUser,
            Select(true, nothingOn, false, false, false, characterNotRead: true, sharingCanRun: false));
    }

    // A halt's press also clears a character that could not be read.
    [Fact]
    public void The_switch_a_pause_and_a_halt_outrank_a_character_not_read()
    {
        Assert.Equal(
            SyncStatusKind.SwitchedOffByUser,
            Select(false, Running(), false, false, false, characterNotRead: true, sharingCanRun: true));
        Assert.Equal(
            SyncStatusKind.PausedByServer,
            Select(true, Paused(), false, false, false, characterNotRead: true, sharingCanRun: true));
        Assert.Equal(
            SyncStatusKind.BlockedPendingUserAction,
            Select(true, Running(), true, false, false, characterNotRead: true, sharingCanRun: true));
    }

    [Fact]
    public void The_character_line_is_pinned()
    {
        Assert.Equal(
            "Your character could not be read, so nothing is uploaded. Press Sync now to try again.",
            SyncStatusView.CharacterNotReadSentence);
    }

    // --- What the card may promise, and what the button may offer ---

    // Derived from the status, not the facts, so a card cannot state a pause and promise a cadence
    // under it.
    [Theory]
    [InlineData(SyncStatusKind.WaitingForCharacter, true)]
    [InlineData(SyncStatusKind.LastUploadOutcome, true)]
    [InlineData(SyncStatusKind.NothingUploadedYet, true)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, false)]
    [InlineData(SyncStatusKind.PausedByServer, false)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false)]
    [InlineData(SyncStatusKind.CharacterNotRead, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, false)]
    [InlineData(SyncStatusKind.LoggedOut, false)]
    public void The_cadence_promise_holds_only_where_nothing_stops_the_pipeline(
        SyncStatusKind kind, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.CadenceHolds(kind));
    }

    // Refused where a press would flash "Syncing" and add no upload. WaitingForCharacter stays out to keep a
    // wait's line; the halt, CharacterNotRead and a pause stay live, since a press does something there.
    [Theory]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true)]
    [InlineData(SyncStatusKind.PausedByServer, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true)]
    [InlineData(SyncStatusKind.LoggedOut, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false)]
    [InlineData(SyncStatusKind.CharacterNotRead, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, false)]
    [InlineData(SyncStatusKind.LastUploadOutcome, false)]
    [InlineData(SyncStatusKind.NothingUploadedYet, false)]
    public void Sync_now_is_refused_only_where_pressing_it_would_do_nothing(
        SyncStatusKind kind, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.ManualSyncWouldDoNothing(kind));
    }

    // --- Whether the card offers "Sync now" ---

    // During a server-requested wait only the halt invites a press, since the press lifts it. A press
    // while the character is being read brings nothing sooner, and in a pause only asks if syncing is back.
    [Theory]
    [InlineData(SyncStatusKind.SwitchedOffByUser, false, false)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true, false)]
    [InlineData(SyncStatusKind.PausedByServer, false, false)]
    [InlineData(SyncStatusKind.PausedByServer, true, false)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, true, true)]
    [InlineData(SyncStatusKind.CharacterNotRead, false, true)]
    [InlineData(SyncStatusKind.CharacterNotRead, true, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, false, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, false, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true, false)]
    [InlineData(SyncStatusKind.LoggedOut, false, false)]
    [InlineData(SyncStatusKind.LoggedOut, true, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, false, false)]
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

    // The button follows the sentences, plus two states a wait does not hold back: CharacterNotRead,
    // where a press restarts the read, and a pause, where a press asks whether syncing is back on.
    [Theory]
    [InlineData(SyncStatusKind.SwitchedOffByUser, false, false)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true, false)]
    [InlineData(SyncStatusKind.PausedByServer, false, true)]
    [InlineData(SyncStatusKind.PausedByServer, true, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, false, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, true, true)]
    [InlineData(SyncStatusKind.CharacterNotRead, false, true)]
    [InlineData(SyncStatusKind.CharacterNotRead, true, true)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, false, false)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, false, false)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true, false)]
    [InlineData(SyncStatusKind.LoggedOut, false, false)]
    [InlineData(SyncStatusKind.LoggedOut, true, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, false, false)]
    [InlineData(SyncStatusKind.WaitingForCharacter, true, false)]
    [InlineData(SyncStatusKind.LastUploadOutcome, false, true)]
    [InlineData(SyncStatusKind.LastUploadOutcome, true, false)]
    [InlineData(SyncStatusKind.NothingUploadedYet, false, true)]
    [InlineData(SyncStatusKind.NothingUploadedYet, true, false)]
    public void The_button_is_live_where_a_press_does_something(
        SyncStatusKind kind, bool backingOff, bool expected)
    {
        Assert.Equal(expected, SyncStatusView.SyncNowEnabled(kind, backingOff));
    }

    // --- The wait line ---

    // The refused upload's own outcome states the wait.
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void A_status_line_that_states_the_wait_needs_no_second_one(ApiStatus lastStatus)
    {
        Assert.False(
            SyncStatusView.NeedsWaitLine(SyncStatusKind.LastUploadOutcome, lastStatus, backingOff: true));
    }

    // A wait outlives the outcome that started it: after a relog, only the wait line says uploads are
    // held back.
    [Theory]
    [InlineData(SyncStatusKind.NothingUploadedYet)]
    [InlineData(SyncStatusKind.WaitingForCharacter)]
    [InlineData(SyncStatusKind.CharacterNotRead)]
    public void A_wait_the_status_line_does_not_state_gets_its_own_line(SyncStatusKind kind)
    {
        Assert.True(SyncStatusView.NeedsWaitLine(kind, lastStatus: null, backingOff: true));
    }

    [Fact]
    public void A_different_outcome_on_the_line_still_gets_the_wait_line()
    {
        Assert.True(SyncStatusView.NeedsWaitLine(
            SyncStatusKind.LastUploadOutcome, ApiStatus.NetworkError, backingOff: true));
    }

    // Without a wait there is no line. A halt, a pause and each state where a press does nothing
    // already say why nothing uploads (see NeedsWaitLine).
    [Theory]
    [InlineData(SyncStatusKind.LastUploadOutcome, false)]
    [InlineData(SyncStatusKind.SwitchedOffByUser, true)]
    [InlineData(SyncStatusKind.PausedByServer, true)]
    [InlineData(SyncStatusKind.BlockedPendingUserAction, true)]
    [InlineData(SyncStatusKind.NothingSwitchedOnByUser, true)]
    [InlineData(SyncStatusKind.NothingPermittedByServer, true)]
    [InlineData(SyncStatusKind.LoggedOut, true)]
    public void No_wait_line_without_a_wait_or_where_another_sentence_covers_it(
        SyncStatusKind kind, bool backingOff)
    {
        Assert.False(SyncStatusView.NeedsWaitLine(kind, lastStatus: null, backingOff));
    }

    // --- The cadence sentence ---

    /// <summary>A switched-on collection that does, or does not, upload as soon as it unlocks.</summary>
    private static CategorySettingsRow Announced(bool uploadsOnUnlock) =>
        Row() with { UploadsOnUnlock = uploadsOnUnlock };

    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    // Collections the game announces upload within seconds of an unlock.
    [Fact]
    public void The_cadence_promises_seconds_while_an_announced_collection_is_on()
    {
        Assert.Equal(
            "Most new unlocks upload within seconds. Everything else syncs automatically every " +
            "30 minutes — press Sync now to update immediately.",
            SyncStatusView.CadenceSentence(
                new[] { Announced(true) }, HalfHour, syncNowOffered: true, backingOff: false));
    }

    // An announced collection does not count when the user or the server switched it off, or the last
    // pass did not read it, since it sends nothing on an unlock until read.
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

    // A wait holds back unlock uploads as well as sweeps.
    [Fact]
    public void The_cadence_promises_no_seconds_during_a_wait()
    {
        Assert.Equal(
            "Your collections sync automatically every 30 minutes.",
            SyncStatusView.CadenceSentence(
                new[] { Announced(true) }, HalfHour, syncNowOffered: false, backingOff: true));
    }

    // The line under the read-status panel says what picks up a fix made in game. It speaks only of
    // lines that name an action: some unread lines have none.
    [Theory]
    [InlineData(true, "Where a line above names an action, do it in game, then press Sync now.")]
    [InlineData(false, "Where a line above names an action, do it in game, and the next sync picks it up.")]
    public void The_follow_up_names_sync_now_only_while_it_is_offered(bool syncNowOffered, string expected)
    {
        Assert.Equal(expected, SyncStatusView.MissingFollowUp(syncNowOffered));
    }

    // --- The deferred upload's status line ---

    // Once the wait ends, the same outcome no longer means waiting. Both lines name the website by the
    // address passed in.
    [Theory]
    [InlineData(true, "Waiting before the next upload, as " + Host + " asked.")]
    [InlineData(
        false, "The last upload was held back at " + Host + "'s request. The next sync uploads as usual.")]
    public void A_deferred_upload_says_whether_the_wait_still_holds(bool backingOff, string expected)
    {
        Assert.Equal(expected, SyncStatusView.DeferredUploadLine(backingOff, Host));
    }
}
