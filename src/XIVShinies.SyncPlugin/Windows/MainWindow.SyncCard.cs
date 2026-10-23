using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Windows;

// The sync card: the master switch, the status line, the Sync now button, and the
// "Reading from:" panel.
internal sealed partial class MainWindow
{
    /// <summary>Everything sync in one card: the master switch, current status, manual trigger.</summary>
    /// <param name="rows">This frame's category rows (see <see cref="BuildCategoryRows"/>).</param>
    /// <param name="remoteConfig">The screen's <c>/config</c> snapshot, or null if none has arrived.</param>
    private void DrawSyncCard(IReadOnlyList<CategorySettingsRow> rows, ConfigResponse? remoteConfig)
    {
        using (BrandCard())
        {
            DrawCardTitle(FontAwesomeIcon.Sync, "Sync my collections");

            // A toggle, not a checkbox: it is a power switch, and the checkboxes below choose what
            // to include.
            var masterEnabled = configuration.Settings.MasterEnabled;
            var stateLabel = masterEnabled ? "ON" : "OFF";

            // The state label and the toggle align right as one cluster.
            var gap = 8f * ImGuiHelpers.GlobalScale;
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            Widgets.AlignRight(
                Widgets.ToggleWidth + gap + ImGui.CalcTextSize(stateLabel).X, innerRight);

            if (masterEnabled)
                ImGui.TextColored(Brand.Teal, stateLabel);
            else
                ImGui.TextDisabled(stateLabel);

            ImGui.SameLine(0f, gap);
            if (Widgets.BrandToggle("##masterToggle", ref masterEnabled))
            {
                configuration.Settings.MasterEnabled = masterEnabled;
                configuration.Save();
            }

            CloseCardHeader();
            DrawStatus(rows, remoteConfig);
        }
    }

    /// <summary>
    /// The sync card's live status: what the last upload did, and what this session can currently read.
    /// </summary>
    /// <param name="rows">
    /// This frame's category rows, which the "Reading from:" panel turns into its collection lines.
    /// </param>
    /// <param name="remoteConfig">
    /// The screen's one <c>/config</c> snapshot, which says whether a sharing feature may run.
    /// </param>
    private void DrawStatus(IReadOnlyList<CategorySettingsRow> rows, ConfigResponse? remoteConfig)
    {
        // The tracker and the Crucible sharing wait on the character too, so they count toward the
        // warning that it could not be read.
        var sharingCanRun = OccultGate.CanTrack(configuration.Settings, remoteConfig)
            || CrucibleGate.CanShare(configuration.Settings, remoteConfig);

        // SyncStatusView.Select picks the state and its precedence; this only draws it.
        var status = SyncStatusView.Select(
            configuration.Settings.MasterEnabled,
            rows,
            syncManager.BlockedPendingUserAction,
            syncManager.HasCharacter,
            syncManager.LastStatus is not null,
            syncManager.IdentityCaptureGaveUp,
            sharingCanRun,
            syncManager.IsLoggedIn);

        // Read once, so the status line, the wait line, the button and the sentences below all
        // describe the same wait.
        var backingOff = syncManager.IsBackingOff;

        if (status == SyncStatusKind.SwitchedOffByUser)
        {
            // Red: nothing runs while the switch is off, and gray would read as resting.
            DrawWarning("Syncing is switched off.");
        }
        else if (status == SyncStatusKind.PausedByServer)
        {
            // The toggle still reads ON, the plugin's own setting; this line says the switch that is
            // off is on the website, either its pause or the user's profile switch.
            DrawWarning(ServerOffCopy.Paused(BackendHost()));
        }
        else if (status == SyncStatusKind.BlockedPendingUserAction)
        {
            // Names the fix for the status that raised the halt (SyncManager.HaltStatus), which can
            // differ from the last upload's outcome.
            DrawWarning($"Syncing has stopped. {HaltSentence()}");
        }
        else if (status == SyncStatusKind.CharacterNotRead)
        {
            // Red, like the halt: nothing uploads until the player presses Sync now or logs in again.
            DrawWarning(SyncStatusView.CharacterNotReadSentence);
        }
        else if (status == SyncStatusKind.NothingSwitchedOnByUser)
        {
            // Not a warning: nothing is broken. Stated because the collections can be folded away,
            // and scoped to collections because the sharing features may still be uploading.
            ImGui.TextUnformatted(
                "No collections are switched on, so none of your collection progress is being " +
                "uploaded.");
        }
        else if (status == SyncStatusKind.NothingPermittedByServer)
        {
            // A warning: it is not the user's doing, and nothing in the plugin can change it. The rows'
            // "Off" chips are a fold away.
            DrawWarning(ServerOffCopy.EveryCollection(BackendHost()));
        }
        else if (status == SyncStatusKind.WaitingForCharacter)
        {
            // Full contrast, as the status. It names no duration, since the wait has no fixed length
            // (see SyncManager.CaptureIdentityIfSettled).
            ImGui.TextUnformatted(SyncStatusView.WaitingForCharacterSentence);
        }
        else if (status == SyncStatusKind.LoggedOut)
        {
            // Not a warning: logging in is all it takes.
            ImGui.TextUnformatted(SyncStatusView.LoggedOutSentence);
        }
        else if (status == SyncStatusKind.LastUploadOutcome && syncManager.LastStatus is { } lastStatus)
        {
            DrawLastStatus(lastStatus, backingOff);
        }
        else
        {
            ImGui.TextUnformatted("No collections have been uploaded yet this session.");
        }

        if (SyncStatusView.NeedsWaitLine(status, syncManager.LastStatus, backingOff))
            DrawWrapped(SyncStatusView.WaitLine(BackendHost()), ImGuiCol.Text);

        // Without a time, a quiet stretch (item acquisitions fire no event) looks like a hang. Muted:
        // it is a footnote to the status.
        if (syncManager.LastSyncedAt is { } syncedAt)
            ImGui.TextDisabled($"Last synced {TimeText.Ago(DateTimeOffset.UtcNow - syncedAt)}.");

        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));

        // "Syncing" shows while the upload is on the wire, with a short minimum. `###` keeps the ID
        // stable while the label changes.
        var syncWidth = PaddedButtonWidth("Sync now");
        var showSyncing = DateTime.UtcNow < syncFeedbackUntil || syncManager.UploadInFlight;
        var syncButtonPos = ImGui.GetCursorPos();

        // From the stated status and the server's wait, so no sentence invites a press the button
        // refuses (see SyncStatusView.SyncNowOffered and SyncNowEnabled).
        var syncNowOffered = SyncStatusView.SyncNowOffered(status, backingOff);
        if (PrimaryButton(
                showSyncing ? "###syncNow" : "Sync now###syncNow",
                new Vector2(syncWidth, 0f),
                enabled: SyncStatusView.SyncNowEnabled(status, backingOff))
            && !showSyncing)
        {
            syncManager.RequestManualSync();
            syncFeedbackUntil = DateTime.UtcNow.AddSeconds(1.5);
        }

        if (showSyncing)
        {
            DrawButtonFeedback(
                syncButtonPos, syncWidth, FontAwesomeIcon.Sync, Brand.TealForeground,
                "Syncing", Brand.TealForeground);
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Both blocks below describe a running pipeline. Derived from the stated status, so the
        // status and the promise cannot disagree.
        var pipelineRunning = SyncStatusView.CadenceHolds(status);

        // One expectation for every collection, phrased by mechanism, never by category (see
        // SyncStatusView.CadenceSentence).
        if (pipelineRunning)
        {
            DrawWrapped(
                SyncStatusView.CadenceSentence(
                    rows, syncManager.FullSyncInterval, syncNowOffered, backingOff),
                ImGuiCol.Text);
        }

        // The panel lists switched-on collections and the item containers, which no one category owns, so it
        // sits here. It waits for a pass (SyncManager.HasCollected), or every enabled row would show as read.
        if (pipelineRunning && syncManager.HasCollected)
        {
            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

            // Full contrast: the heading is what gives the notes beneath it their meaning.
            ImGui.TextUnformatted("Reading from:");

            // ReadStatusView.Build writes the notes; this only draws them, never asking their source.
            var readStatus = ReadStatusView.Build(rows, syncManager.LastSourceNotes, BackendHost());

            // Gates the follow-up hint on exactly the notes shown.
            var hasMissingNote = DrawReadStatusGroup("Collections", readStatus.Collections);
            hasMissingNote |= DrawReadStatusGroup("Containers", readStatus.Containers);

            // A Missing line names an action in game; this says what then picks the change up. A
            // Cached note leaves the user nothing to do, so it does not raise this.
            if (hasMissingNote)
            {
                ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
                DrawWrapped(SyncStatusView.MissingFollowUp(syncNowOffered), ImGuiCol.Text);
            }
        }
    }

    /// <summary>
    /// Draws one labeled group of the "Reading from:" panel, healthy sources as chips and then
    /// full-line notes, and reports whether any note was <see cref="SourceTone.Missing"/>.
    /// </summary>
    /// <remarks>
    /// It switches on the tone alone, never a source or category, so a new source needs no change
    /// here. A healthy source (Live or Cached) asks nothing of the user, so it shrinks to a chip; a
    /// Missing note keeps a full line, since hover must never be the only way to read it (see
    /// <see cref="SourceNote"/>).
    /// </remarks>
    private bool DrawReadStatusGroup(string label, IReadOnlyList<SourceNote> notes)
    {
        // An empty group skips its heading too.
        if (notes.Count == 0)
            return false;

        ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
        DrawSectionLabel(label);

        // The builders always give a Missing note its text (see SourceNote); one without degrades to
        // a chip rather than being dropped.
        static bool IsLineForm(SourceNote note) =>
            note.Tone is SourceTone.Missing && note.Text is not null;

        // The chip row, wrapped from measured widths in cursor space before each chip draws.
        var innerRight = activeCardInnerRight
            ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        var chipGap = 6f * ImGuiHelpers.GlobalScale;

        var anyChipDrawn = false;
        var rowRight = 0f; // cursor-space right edge of the last chip drawn

        foreach (var note in notes)
        {
            if (IsLineForm(note))
                continue;

            var icon = ToneIcon(note.Tone);
            var width = ChipWidth(icon, note.Label);

            // DrawChip leaves the cursor on a new line, so SameLine only while the chip fits. A
            // wrapped row gets a little air, or outlined chips sit flush.
            if (anyChipDrawn && rowRight + chipGap + width <= innerRight)
            {
                ImGui.SameLine(0f, chipGap);
            }
            else if (anyChipDrawn)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (4f * ImGuiHelpers.GlobalScale));
            }

            var chipStart = ImGui.GetCursorPosX();
            DrawChip(icon, note.Label, ToneColor(note.Tone));
            rowRight = chipStart + width;
            anyChipDrawn = true;

            // The chip's footprint is the item IsItemHovered answers for.
            if (note.Detail is { } detail && ImGui.IsItemHovered())
                Widgets.DrawTooltip(detail);
        }

        var hasMissingNote = false;

        foreach (var note in notes)
        {
            // Only Missing counts: Cached is a healthy resting state (see SourceNoteText), and would
            // keep the follow-up hint up forever.
            if (note.Tone is SourceTone.Missing)
                hasMissingNote = true;

            if (!IsLineForm(note) || note.Text is not { } text)
                continue;

            DrawIconedText(ToneIcon(note.Tone), ToneColor(note.Tone), text, Widgets.InlineIconScale);
        }

        return hasMissingNote;
    }

    /// <summary>The color a source note's tone draws in.</summary>
    private static Vector4 ToneColor(SourceTone tone) =>
        tone switch
        {
            SourceTone.Live => Widgets.SuccessColor,
            SourceTone.Cached => Widgets.CautionColor,
            SourceTone.Missing => Widgets.ErrorColor,

            // Muted: an unreadable source is information, not a problem to solve.
            SourceTone.Unreadable => ImGuiColors.DalamudGrey,

            // A future tone draws gray rather than throwing.
            _ => ImGuiColors.DalamudGrey,
        };

    /// <summary>The icon drawn beside a source note's tone.</summary>
    private static FontAwesomeIcon ToneIcon(SourceTone tone) =>
        tone switch
        {
            SourceTone.Live => FontAwesomeIcon.CheckCircle,
            SourceTone.Cached => FontAwesomeIcon.Clock,
            SourceTone.Missing => FontAwesomeIcon.ExclamationCircle,

            // The game itself cannot see this source; the chip's hover says why.
            SourceTone.Unreadable => FontAwesomeIcon.EyeSlash,
            _ => FontAwesomeIcon.QuestionCircle,
        };

    /// <summary>
    /// The sentence for a character refusal, naming the loaded character and the configured host,
    /// or null when <paramref name="status"/> is not one of the four 403 refusals.
    /// </summary>
    private string? CharacterRefusalSentence(ApiStatus status)
    {
        // One read, so the name and the home world come from the same character even if a logout
        // lands mid-frame.
        var character = syncManager.Identity;

        return CharacterRefusalCopy.For(status, character?.Name, character?.HomeWorld, BackendHost());
    }

    /// <summary>
    /// The current halt's sentence (see <see cref="HaltCopy"/>). Every card that shows a halt uses it,
    /// so they agree.
    /// </summary>
    private string HaltSentence()
    {
        // One read, so the name and the home world come from the same character.
        var character = syncManager.Identity;

        return HaltCopy.For(
            syncManager.HaltStatus,
            character?.Name,
            character?.HomeWorld,
            BackendHost(),
            configuration.Settings.BaseUrl,
            configuration.Settings.CustomBackendAcknowledged);
    }

    /// <summary>Renders the last upload's outcome. Switches on a status, never on a category.</summary>
    /// <param name="status">The last upload's outcome.</param>
    /// <param name="backingOff">Whether a server-requested wait is still in force.</param>
    private void DrawLastStatus(ApiStatus status, bool backingOff)
    {
        switch (status)
        {
            case ApiStatus.Ok:
                ImGui.TextColored(Widgets.SuccessColor, "Your collections are up to date.");
                break;

            // The four character refusals: any status CharacterRefusalCopy has a sentence for.
            // CharacterRefusalCopyTests pins that set to exactly ApiStatusMap.IsCharacterRefusal.
            case var refused when CharacterRefusalSentence(refused) is { } sentence:
                DrawWarning(sentence);
                break;

            case ApiStatus.InvalidToken:
                DrawWarning($"Your token was rejected. Generate a new one on {BackendHost()}.");
                break;

            // Nothing to fix in the plugin, so not red, but the status line, so full contrast. A 503 from
            // the profile switch is named by ServerOffCopy.Paused once the next /config poll reports it.
            case ApiStatus.RateLimited:
            case ApiStatus.SyncDisabled:
                DrawWrapped(SyncStatusView.DeferredUploadLine(backingOff, BackendHost()), ImGuiCol.Text);
                break;

            // Not red either: the plugin tries again on its own.
            case ApiStatus.NetworkError:
                DrawWrapped($"Could not reach {BackendHost()}. Will try again.", ImGuiCol.Text);
                break;

            // The request never left, so "will try again" would be false: nothing changes until the
            // configuration does. BackendUrl words which setting is at fault.
            case ApiStatus.NotConfigured:
                DrawWarning(BackendUrl.DescribeUnusableSetting(
                    configuration.Settings.BaseUrl,
                    configuration.Settings.CustomBackendAcknowledged));
                break;

            default:
                ImGui.TextUnformatted("The last upload did not succeed. Will try again.");
                break;
        }
    }
}
