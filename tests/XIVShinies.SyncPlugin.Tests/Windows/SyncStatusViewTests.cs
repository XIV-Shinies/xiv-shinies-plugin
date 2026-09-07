using System;
using System.Collections.Generic;
using Xunit;
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
}
