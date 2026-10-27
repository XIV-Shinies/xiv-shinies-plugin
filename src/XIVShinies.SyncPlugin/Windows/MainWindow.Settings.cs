using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

// The settings screen: the masthead, the collapsible sections, and the upload-log card.
internal sealed partial class MainWindow
{
    private void DrawSettings()
    {
        // One snapshot for the whole screen, so every surface decides from the same answer.
        var remoteConfig = syncManager.RemoteConfig;

        // Built once, for the sync card, the header's chip and the consent card.
        var rows = BuildCategoryRows(remoteConfig);

        DrawSettingsHeader();

        Widgets.SectionGap();
        DrawSyncCard(rows, remoteConfig);

        Widgets.SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // A new collection, group, or Crucible card anywhere under the header.
        var hasNewCollection = AnythingIsNew(rows)
            || CrucibleBadge.IsNew(
                configuration.Settings.CrucibleSharingSeen, remoteConfig, crucibleBadgedThisSession);

        // Captured before the header, so its chip can sit on the header's row: at the card's inner
        // edge, or the content edge outside a card.
        var headerRowCursorX = ImGui.GetCursorPosX();
        var headerRowScreenX = ImGui.GetCursorScreenPos().X;
        var headerRowInnerRight =
            activeCardInnerRight ?? (headerRowCursorX + ImGui.GetContentRegionAvail().X);

        // The only section open by default, since consent may change at any moment. ImGui remembers
        // the user's fold after that.
        var collectionsOpen =
            ImGui.CollapsingHeader("Collections", ImGuiTreeNodeFlags.DefaultOpen);

        // Drawn open or folded. The header spans the row, so SameLine cannot place the chip (see
        // DrawHeaderRightChip).
        if (hasNewCollection)
        {
            DrawHeaderRightChip(
                FontAwesomeIcon.Star, "New", Brand.Gold,
                headerRowScreenX + (headerRowInnerRight - headerRowCursorX));
        }

        if (collectionsOpen)
        {
            ImGui.Spacing();
            DrawCategoryRows(rows, showNewChips: true);

            ImGui.Spacing();
            DrawCrucibleConsentRow(remoteConfig, showNewChip: true);
            ImGui.Spacing();
            DrawOccultConsentRow(remoteConfig);
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Folded by default, since replacing a token is rare, but it must exist (see DrawTokenPanel).
        if (ImGui.CollapsingHeader("Account"))
        {
            TryAutoVerifyToken();

            ImGui.Spacing();
            DrawTokenPanel();
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // The wizard's privacy disclosure (see DrawWelcomeStep) stays reachable after onboarding, its
        // last sentence phrased for a configured plugin.
        if (ImGui.CollapsingHeader("Privacy"))
        {
            ImGui.Spacing();

            DrawPrivacyCard(
                "Your character is identified by a one-way fingerprint computed on this machine. " +
                $"Your character's name and home world are sent so {BackendHost()} can match the " +
                "character you already claimed and verified. Nothing is uploaded unless syncing " +
                (ConsentCopy.UserHasAChoice(rows, remoteConfig)
                    ? "is switched on, and you choose what to include."
                    : "is switched on."));
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // What actually went out, per upload. Last, since it is a reference, not a control.
        if (ImGui.CollapsingHeader("Recent uploads"))
        {
            ImGui.Spacing();
            DrawUploadLog();
        }
    }

    /// <summary>
    /// The upload-log card: Clear and Copy log, the in-memory disclosure, and the table (see
    /// <see cref="UploadLogTable"/>).
    /// </summary>
    private void DrawUploadLog()
    {
        var history = syncManager.UploadHistory;

        using (BrandCard())
        {
            // "Recent": the log is in memory and bounded, never a full history.
            DrawCardTitle(FontAwesomeIcon.History, "Recent uploads");

            // Buttons first, faces after: SameLine must anchor on a button, not a face's text item.
            const string clearLabel = "Clear";
            const string copyLabel = "Copy log";
            var clearWidth = IconButtonWidth(FontAwesomeIcon.Trash, clearLabel);
            var copyWidth = IconButtonWidth(FontAwesomeIcon.Copy, copyLabel);
            var gap = 8f * ImGuiHelpers.GlobalScale;
            var showCopied = DateTime.UtcNow < logCopyFeedbackUntil;
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            Widgets.AlignRight(clearWidth + gap + copyWidth, innerRight);

            bool clearPressed;
            bool copyPressed;
            using (ImRaii.Disabled(history.Count == 0))
            {
                var clearButtonPos = ImGui.GetCursorPos();
                clearPressed = BoldButton("###clearLog", new Vector2(clearWidth, 0f));

                ImGui.SameLine(0f, gap);
                var copyButtonPos = ImGui.GetCursorPos();
                copyPressed = BoldButton("###copyLog", new Vector2(copyWidth, 0f));

                // The red trash marks Clear as the destructive one.
                var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
                DrawButtonFeedback(
                    clearButtonPos, clearWidth, FontAwesomeIcon.Trash, Widgets.ErrorColor,
                    clearLabel, textColor);

                if (showCopied)
                {
                    DrawButtonFeedback(
                        copyButtonPos, copyWidth, FontAwesomeIcon.Check, Widgets.SuccessColor,
                        "Copied", textColor);
                }
                else
                {
                    DrawButtonFeedback(
                        copyButtonPos, copyWidth, FontAwesomeIcon.Copy, Brand.Teal,
                        copyLabel, textColor);
                }
            }

            // No confirmation: the log is an in-memory convenience that refills with the next upload.
            if (clearPressed)
                syncManager.ClearUploadHistory();

            if (copyPressed && !showCopied)
            {
                // The configured backend, not the default, since a wrong server is a common support
                // case; and the real version, not the masthead's.
                ImGui.SetClipboardText(UploadLogText.ClipboardText(
                    diagnosticVersion, configuration.Settings.BaseUrl, history));
                logCopyFeedbackUntil = DateTime.UtcNow.AddSeconds(1.5);
            }

            CloseCardHeader();

            // Full contrast: the user needs to know the log is memory-only and leaves out routine
            // sharing uploads.
            DrawWrapped(
                "What this plugin sent recently. The live Occult tracker and the Crucible run sharing " +
                "appear only when an upload is refused. Kept in memory only: the log clears on " +
                "logout and when the plugin unloads.",
                ImGuiCol.Text);
            ImGui.Spacing();

            if (history.Count == 0)
            {
                // Muted filler. It speaks about the log, not the sync: Clear empties it after real
                // uploads.
                ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
                ImGui.TextDisabled("No uploads are recorded here yet.");
                return;
            }

            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
            uploadLogTable.Draw(history, innerRight, BackendHost());
        }
    }

    /// <summary>The settings masthead: the mascot beside the plugin's name and one-line pitch.</summary>
    private void DrawSettingsHeader()
    {
        // Fetched only here: Dalamud frees a cache entry, which holds this plugin's assembly, about two
        // seconds after its last draw, but never one fetched and not drawn. Blank until loaded; never blocks.
        var mascot = textureProvider
            .GetFromManifestResource(typeof(MainWindow).Assembly, PluginMeta.MascotResource)
            .GetWrapOrEmpty();

        // About the height of the header block beside it.
        var mascotSize = 72f * ImGuiHelpers.GlobalScale;

        ImGui.Image(mascot.Handle, new Vector2(mascotSize));
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);

        // Grouped, so wrapped lines return to the block's edge, clear of the mascot.
        using (ImRaii.Group())
        {
            using (headingFont.Available ? headingFont.Push() : null)
            {
                ImGui.TextColored(Brand.Teal, PluginMeta.DisplayName);
            }

            // Right-aligned on the title line, where the wizard puts its step counter. The version is the
            // first thing a bug report needs.
            var versionLabel = $"v{pluginVersion}";
            ImGui.SameLine();
            Widgets.AlignRight(ImGui.CalcTextSize(versionLabel).X);
            ImGui.TextDisabled(versionLabel);

            // The manifest's punchline, at full contrast. It names the official site, not the
            // configured one, because it quotes the tagline rather than saying where data goes.
            Widgets.DrawWrappedSpans(
                (PluginMeta.TaglineLead, null),
                (PluginMeta.TaglineSite, Brand.Gold),
                (PluginMeta.TaglineTail, null));

            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
            DrawLinkButtons();
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
        BrandSeparator();
    }

    /// <summary>The community links under the masthead description.</summary>
    /// <remarks>
    /// Dalamud's icon font has no brand logos, so each link gets a generic glyph. A link whose URL in
    /// <see cref="PluginMeta"/> is empty draws no button.
    /// </remarks>
    private void DrawLinkButtons()
    {
        // Buttons first, faces second: SameLine anchors on the last item, which must be a button.
        var overlays =
            new List<(Vector2 Position, float Width, FontAwesomeIcon Icon, Vector4 IconColor, string Label)>();
        var first = true;

        // More vertical spacing, so wrapped rows of buttons do not touch.
        using var rowSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));

        foreach (var (icon, iconColor, label, id, url) in LinkButtons)
        {
            if (url.Length == 0)
                continue;

            // A blank button sized for its face. Buttons flow like words, wrapping when one does not
            // fit.
            var width = IconButtonWidth(icon, label);

            if (!first)
            {
                ImGui.SameLine(0f, 10f * ImGuiHelpers.GlobalScale);
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
            }

            var position = ImGui.GetCursorPos();
            if (BoldButton(id, new Vector2(width, 0f)))
                Util.OpenLink(url);

            overlays.Add((position, width, icon, iconColor, label));
            first = false;
        }

        foreach (var (position, width, icon, iconColor, label) in overlays)
        {
            DrawButtonFeedback(
                position, width, icon, iconColor,
                label, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        }
    }
}
