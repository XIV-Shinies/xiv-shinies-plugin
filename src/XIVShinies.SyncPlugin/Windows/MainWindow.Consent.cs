using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Windows;

// The consent surfaces shared by the wizard's consent step and the settings: the collection rows
// with their group checkboxes and "New" badges, select-all, and the Crucible and tracker cards.
internal sealed partial class MainWindow
{
    /// <summary>This frame's category rows, which every consent and status surface reads.</summary>
    /// <remarks>It allocates, so build it once per frame and pass it to each surface.</remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    private IReadOnlyList<CategorySettingsRow> BuildCategoryRows(ConfigResponse? remoteConfig) =>
        CategorySettingsView.Build(
            collectors,
            configuration.Settings,
            remoteConfig,
            syncManager.LastSkipped,
            syncManager.LastPartialNotes,
            syncManager.LastCollectedDetails);

    /// <summary>
    /// The consent list: every section's rows in one card, under plain labels rather than folds, so
    /// every checkbox it collects consent for is on screen at once.
    /// </summary>
    /// <remarks>
    /// It names no category or section; all copy comes from the collectors' rows. It covers consent
    /// only: whether a collection could be read belongs in the sync card's read-status panel (see
    /// <see cref="DrawStatus"/>).
    /// </remarks>
    /// <param name="rows">This frame's category rows, from <see cref="BuildCategoryRows"/>.</param>
    /// <param name="showNewChips">
    /// Whether this surface announces new collections; the wizard shows every collection, so it badges
    /// nothing. <see cref="CategorySettingsView.ShowingRetiresTheBadge"/> decides which drawings count
    /// as seen.
    /// </param>
    private void DrawCategoryRows(IReadOnlyList<CategorySettingsRow> rows, bool showNewChips)
    {
        // A wider gap between each checkbox and its label.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            // Where a checkbox's label starts: the home column of each row's copy and the indent of
            // its groups. Measured inside the push, so it includes the wider gap.
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
            DrawSelectAll(rows);
            BrandSeparator();
            ImGui.Spacing();

            // Null until a category is new, so the usual frame allocates nothing.
            List<string>? newlySeenCategories = null;

            foreach (var section in CategorySettingsView.GroupBySection(rows))
            {
                DrawSectionLabel(section.Title);

                foreach (var row in section.Rows)
                {
                    if (DrawCategoryRow(row, showNewChips, checkboxColumn))
                        (newlySeenCategories ??= new List<string>()).Add(row.Key);
                }
            }

            // Once marked seen, rows stop reporting, so this saves once per batch, never every frame.
            if (newlySeenCategories is not null)
            {
                configuration.Settings.MarkCategoriesSeen(newlySeenCategories);
                configuration.Save();
            }

            // A wider gap than between rows, so the note reads as a closing remark.
            ImGui.Spacing();
            ImGui.Spacing();

            BrandSeparator();
            ImGui.Spacing();
            DrawCompletenessNote();
        }
    }

    /// <summary>
    /// One collection's consent row: the checkbox, its disclosure copy, and its group checkboxes.
    /// Both consent surfaces draw it, so neither can show less.
    /// </summary>
    /// <remarks>
    /// The copy says what a tick would send, so an unticked box keeps full contrast: the offer is
    /// open, and the copy is how the user decides. A row the server has switched off can send
    /// nothing, so it mutes, and its reason rides the "Off" chip's tooltip.
    /// </remarks>
    /// <param name="showNewChips">See <see cref="DrawCategoryRows"/>.</param>
    /// <param name="checkboxColumn">
    /// The width from the row's left edge to its checkbox label: the copy's home column and the
    /// indent of the groups beneath.
    /// </param>
    /// <returns>
    /// True when this drawing retires the row's announcement (see
    /// <see cref="CategorySettingsView.ShowingRetiresTheBadge"/>). The caller batches these into one save.
    /// </returns>
    private bool DrawCategoryRow(CategorySettingsRow row, bool showNewChips, float checkboxColumn)
    {
        // The effective state, not the stored choice: a tick under a collection the server has
        // switched off would claim the opposite of what happens.
        var enabled = row.IsEffectivelyOn;
        bool toggled;

        // Where the copy's wrapped lines return to, captured before the checkbox draws.
        var labelColumn = ImGui.GetCursorPosX() + checkboxColumn;

        // Disabled while the server has it off, with the stored choice kept for when it returns.
        using (ImRaii.Disabled(!row.ServerEnabled))
        {
            // `##key` keeps the ImGui ID unique, so two collections with one name cannot share clicks.
            toggled = ImGui.Checkbox($"{row.DisplayName}##{row.Key}", ref enabled);
        }

        if (toggled)
        {
            ManifestConsent.SetRowConsent(row, enabled, configuration.Settings);

            // A click proves the row was shown, so it is marked seen here: on a badging surface the
            // caller's batch waits for the server's answer.
            configuration.Settings.MarkCategoriesSeen(new[] { row.Key });
            configuration.Save();
        }

        // Keeps the chip up after the caller's save makes the row un-new.
        if (showNewChips && row.IsEffectivelyNew)
            categoriesBadgedThisSession.Add(row.Key);

        // CategorySettingsView.BadgeFor picks the mark; this picks its look. New is gold and
        // unfilled, since the color already carries it.
        (FontAwesomeIcon Icon, string Text, Vector4 Color, string? Tooltip, bool Filled)? badge =
            CategorySettingsView.BadgeFor(
                row, showNewChips, categoriesBadgedThisSession.Contains(row.Key)) switch
            {
                CategoryBadgeKind.Off => OffChip(row.ServerOffText(BackendHost())),
                CategoryBadgeKind.New =>
                    (FontAwesomeIcon.Star, "New", Brand.Gold, (string?)null, false),
                _ => null,
            };

        // "Name — what it sends", the hover mark, then the badge, wrapping home under the label.
        // HostPlaceholder fills in the configured website.
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        DrawWrappedWithTrailingHint(
            $"— {HostPlaceholder.Fill(row.WhatGetsSent, BackendHost())}",
            row.Details is { } details ? HostPlaceholder.Fill(details, BackendHost()) : null,
            labelColumn,
            badge,
            muted: !row.ServerEnabled);

        ImGui.Indent(checkboxColumn);

        // Disabled with the category: a group's tick would switch the category back on behind the
        // control that says it is off.
        using (ImRaii.Disabled(!row.ServerEnabled))
            DrawGroupCheckboxes(row, showNewChips);

        ImGui.Unindent(checkboxColumn);
        ImGui.Spacing();

        // Only a row with an announcement left reports, or the caller would save every frame.
        return CategorySettingsView.ShowingRetiresTheBadge(row, showNewChips);
    }

    /// <summary>The live tracker's disclosure: every kind of data it shares, and where it goes.</summary>
    /// <remarks>
    /// Every surface that discloses the tracker uses this sentence, so none can say less. The
    /// character identity is the privacy card's disclosure.
    /// </remarks>
    private string OccultWhatGetsSent() =>
        "While you are in the Occult Crescent, shares your instance's public encounter " +
        "status (critical encounters, FATEs, Forked Tower) and your current world, powering " +
        $"the live tracker on {BackendHost()}.";

    /// <summary>
    /// The tracker's hover text: what is not shared, since the natural worry is other players. It
    /// holds reassurance, never a kind of data (see <see cref="Collectors.CategoryInfo.Details"/>).
    /// </summary>
    private const string OccultTrackerDetails =
        "This is world state — nothing about you beyond your presence, and never anything " +
        "about other players.";

    /// <summary>
    /// The live tracker's consent card, on both consent surfaces: its toggle, disclosure and stopped
    /// line (see <see cref="DrawSharingCardLine"/>), then the standing choice for new collections.
    /// </summary>
    /// <remarks>
    /// A card of its own, out of select-all's reach, because the tracker is not a collection. It
    /// defaults on because it shares world state, not facts about the player (see
    /// <see cref="PluginSettings.ShareOccultInstanceState"/>), yet acts only after the wizard and with
    /// the master switch on. While the server has it off it draws unticked, grayed and chipped "Off",
    /// with the user's choice kept underneath.
    /// </remarks>
    /// <param name="remoteConfig">
    /// The snapshot to decide from: the one the caller used for any prose of its own about this row,
    /// so the two decide from the same answer.
    /// </param>
    private void DrawOccultConsentRow(ConfigResponse? remoteConfig)
    {
        // The gate that decides whether the tracker runs, so the control and the behavior agree.
        var serverOff = OccultGate.ServerHasSwitchedOff(remoteConfig);

        // For the wizard's consent settle: a user shown a disabled box made no choice about a
        // control stored as on by default.
        if (!serverOff)
            wizardCouldOfferOccultToggle = true;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            // Measured inside the same push as the collections card's, so the cards' text lines up.
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;

            // Unticked while the server has it off; the stored choice is untouched.
            var enabled = !serverOff && configuration.Settings.ShareOccultInstanceState;
            bool toggled;
            using (ImRaii.Disabled(serverOff))
                toggled = ImGui.Checkbox("Share live Occult instance state##occultTracker", ref enabled);

            DrawDetailsHint(OccultTrackerDetails);

            // The tracker's config block carries no note, so the gate picks the chip's sentence: the
            // feature's, the pause's, or "not offered".
            if (OccultGate.ServerOffText(remoteConfig, BackendHost()) is { } offText)
                DrawOffChip(offText);

            if (toggled)
            {
                configuration.Settings.ShareOccultInstanceState = enabled;
                configuration.Save();
            }

            ImGui.Indent(checkboxColumn);

            // Full contrast while the choice is open, muted once the server takes it away.
            DrawWrapped(OccultWhatGetsSent(), serverOff ? ImGuiCol.TextDisabled : ImGuiCol.Text);

            DrawSharingCardLine(
                OccultGate.StoppedBySyncSwitch(configuration.Settings, remoteConfig),
                OccultGate.CanTrack(configuration.Settings, remoteConfig));

            ImGui.Unindent(checkboxColumn);

            ImGui.Spacing();

            // The explicit consent PluginSettings.AutoEnableUnseenCategories acts on.
            // ManifestConsent.FixedScopeCategoryKeys says why collections with groups are left out.
            var autoEnable = configuration.Settings.AutoEnableNewFeatures;
            if (ImGui.Checkbox(
                    "Turn on new collections automatically##autoEnableNew",
                    ref autoEnable))
            {
                configuration.Settings.AutoEnableNewFeatures = autoEnable;
                configuration.Save();
            }

            ImGui.Indent(checkboxColumn);

            // The tick answers for disclosures not yet read, so the copy names where to read them: the
            // collection's row, marked New until seen.
            DrawWrapped(
                "Tick this and a new collection a later update adds starts switched on, whatever it " +
                "sends, instead of waiting for you to find it. It is marked New in the settings, " +
                "where you can read what it sends and switch it off. Collections whose groups you " +
                "choose separately, and kinds of sharing like the Crucible runs and the live " +
                "tracker above, are never switched on by this box.",
                ImGuiCol.Text);
            ImGui.Unindent(checkboxColumn);
        }
    }

    /// <summary>The Crucible sharing's disclosure: every kind of data it sends, and where it goes.</summary>
    /// <remarks>Every surface that discloses the sharing uses this, so none can say less.</remarks>
    private string CrucibleWhatGetsSent() =>
        "From the beasts you pick at the entrance until you leave a board, shares what the " +
        "Crucible's windows show between fights (the board, your beasts, bag, tokens, treasure, " +
        "loot, shop offers and results), your own HP, and the HP of each beast you call out (fights " +
        "included, sent once each fight is over), each stamped with when and where it was read or " +
        $"closed, to fill in your run on {BackendHost()}. On a board it also checks in regularly, " +
        "fights included, and says when you leave.";

    /// <summary>
    /// The sharing's hover text: what is read during a fight and what is never read or sent, since
    /// the natural worries are fights, enemies and other players.
    /// </summary>
    private const string CrucibleRunsDetails =
        "During a fight the only thing read about your run is which beast you have out and its HP, " +
        "sent once the fight is over: nothing is read from the Crucible's windows or your own HP " +
        "until then, and nothing about damage, actions or the battle log is ever read. Enemies are " +
        "skipped: nothing about them is sent beyond how many your run beat and the points they were " +
        "worth, as the results screen shows them. Nothing about other players is read or sent.";

    /// <summary>
    /// The Crucible sharing's consent card, on both consent surfaces: its toggle, disclosure and
    /// stopped line (see <see cref="DrawSharingCardLine"/>). A card of its own: it is not a collection.
    /// </summary>
    /// <remarks>
    /// The toggle starts off (see <see cref="PluginSettings.ShareCrucibleRuns"/>). While
    /// <see cref="CrucibleGate"/> says the server has it off, it draws unticked, grayed and chipped
    /// "Off", with the user's choice kept underneath. <see cref="CrucibleBadge"/> rules its "New" chip.
    /// </remarks>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    /// <param name="showNewChip">
    /// Whether this surface may chip the card "New"; see <see cref="CrucibleBadge.BadgeFor"/>.
    /// </param>
    private void DrawCrucibleConsentRow(ConfigResponse? remoteConfig, bool showNewChip)
    {
        // The gate that decides whether the sharing runs, so the control and the behavior agree.
        var serverOff = CrucibleGate.ServerHasSwitchedOff(remoteConfig);

        // Read before this frame records anything, so the first frame still wears the chip; the
        // session flag keeps it up after that.
        var seen = configuration.Settings.CrucibleSharingSeen;
        var badge = CrucibleBadge.BadgeFor(seen, remoteConfig, showNewChip, crucibleBadgedThisSession);
        if (badge == CategoryBadgeKind.New)
            crucibleBadgedThisSession = true;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;

            // The user's choice, unticked while the server has the sharing off.
            var enabled = !serverOff && configuration.Settings.ShareCrucibleRuns;
            bool toggled;
            using (ImRaii.Disabled(serverOff))
                toggled = ImGui.Checkbox("Share your Crucible runs##crucibleRuns", ref enabled);

            // False while the card is scrolled out of view, where the user has not seen it.
            var onScreen = ImGui.IsItemVisible();

            DrawDetailsHint(CrucibleRunsDetails);

            // ServerOffText is never null while the badge is Off.
            if (badge == CategoryBadgeKind.Off
                && CrucibleGate.ServerOffText(remoteConfig, BackendHost()) is { } offText)
            {
                DrawOffChip(offText);
            }
            else if (badge == CategoryBadgeKind.New)
            {
                ImGui.SameLine();
                DrawChip(FontAwesomeIcon.Star, "New", Brand.Gold);
            }

            // The drawn tick differs from the stored choice only while the box is disabled, so a
            // click never writes the server's state over the user's.
            if (toggled)
                configuration.Settings.ShareCrucibleRuns = enabled;

            // True only on the frame the card is first seen.
            var retires = CrucibleBadge.ShowingRetires(seen, remoteConfig, onScreen, clicked: toggled);
            if (retires)
                configuration.Settings.CrucibleSharingSeen = true;

            if (toggled || retires)
                configuration.Save();

            ImGui.Indent(checkboxColumn);

            // Full contrast while the choice is open, muted once the server takes it away.
            DrawWrapped(CrucibleWhatGetsSent(), serverOff ? ImGuiCol.TextDisabled : ImGuiCol.Text);

            DrawSharingCardLine(
                CrucibleGate.StoppedBySyncSwitch(configuration.Settings, remoteConfig),
                CrucibleGate.CanShare(configuration.Settings, remoteConfig));

            ImGui.Unindent(checkboxColumn);
        }
    }

    /// <summary>
    /// Draws why a ticked sharing is not running, when <see cref="SharingCardLine.For"/> has a line.
    /// </summary>
    /// <param name="stoppedBySyncSwitch">Whether the master switch is what stops the sharing.</param>
    /// <param name="canRun">Whether the settings and the server let the sharing run.</param>
    private void DrawSharingCardLine(bool stoppedBySyncSwitch, bool canRun)
    {
        var halted = syncManager.BlockedPendingUserAction;
        var line = SharingCardLine.For(
            stoppedBySyncSwitch,
            canRun,
            halted,
            syncManager.IdentityCaptureGaveUp,
            halted ? HaltSentence() : string.Empty);

        if (line is null)
            return;

        ImGui.Spacing();
        DrawWarning(line);
    }

    /// <summary>
    /// Discloses that a list the plugin reads in full is reported as complete, which lets the server
    /// question the user's own hand marks (see <see cref="Api.SyncRequest.CollectionScopes"/>).
    /// </summary>
    /// <remarks>
    /// A claim about the user's own entries is disclosed where consent is given, not only in the
    /// contract. Every consent surface draws it, so the pre-consent screen never discloses less than
    /// the one that collects the ticks, and it names no category, so it stays true whatever is read in
    /// full. The visible line carries the consequence and names the configured host; the hover holds
    /// only the reassurance.
    /// </remarks>
    private void DrawCompletenessNote()
    {
        DrawWrappedWithTrailingHint(
            $"Lists the plugin can read in full are reported as complete, which lets {BackendHost()} " +
            "point out anything you marked by hand that the plugin did not find.",
            "It is only ever pointed out for you to review — nothing is unmarked for you, and a " +
            "mark you make afterwards is never questioned.");
    }

    /// <summary>
    /// Draws a paragraph from the cursor, wrapping to the first word's column, then a muted hover
    /// mark when <paramref name="details"/> is given, then <paramref name="trailingChip"/>.
    /// </summary>
    /// <remarks>
    /// <c>SameLine</c> after a wrapped paragraph anchors at the end of its first line, so the words
    /// are flowed by hand and the mark and chip follow as more words. Lines break at the card's inner
    /// edge, or the content edge outside a card.
    /// </remarks>
    /// <param name="text">The paragraph.</param>
    /// <param name="details">The hover text behind the trailing mark, or null for no mark.</param>
    /// <param name="homeXOverride">
    /// Where wrapped lines return to, in cursor space, such as a checkbox label's column. Null
    /// returns them to the first word's column.
    /// </param>
    /// <param name="trailingChip">
    /// A badge flowed after the mark, or null. Only this method can fit it to the last line: a
    /// caller's <c>SameLine</c> afterwards would anchor on the row below.
    /// </param>
    /// <param name="muted">
    /// True to draw the words disabled-gray, for copy about something the plugin is not doing, which
    /// at full contrast would read as live behavior.
    /// </param>
    private void DrawWrappedWithTrailingHint(
        string text,
        string? details,
        float? homeXOverride = null,
        (FontAwesomeIcon Icon, string Text, Vector4 Color, string? Tooltip, bool Filled)? trailingChip = null,
        bool muted = false)
    {
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var innerRight = activeCardInnerRight
            ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        // Captured before the flow moves the cursor.
        var homeX = homeXOverride ?? ImGui.GetCursorPosX();
        var first = true;

        // Zero vertical spacing and ImGui's wrapping off (see Widgets.DrawWrappedSpans). The mark and badge
        // stay inside: outside, each would add an ItemSpacing.Y that the Spacing() below doubles.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemSpacing,
                   new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f)))
        using (ImRaii.TextWrapPos(-1f))
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!first)
                {
                    ImGui.SameLine(0f, spaceWidth);
                    if (innerRight - ImGui.GetCursorPosX() < ImGui.CalcTextSize(word).X)
                    {
                        ImGui.NewLine();
                        ImGui.SetCursorPosX(homeX);
                    }
                }
                else if (homeXOverride is not null
                    && innerRight - ImGui.GetCursorPosX() < ImGui.CalcTextSize(word).X)
                {
                    // Mid-line, even the first word may not fit, and wrapping to the caller's home
                    // column widens the line. Without one, home is here and wrapping gains nothing.
                    ImGui.NewLine();
                    ImGui.SetCursorPosX(homeX);
                }

                if (muted)
                    ImGui.TextDisabled(word);
                else
                    ImGui.TextUnformatted(word);

                first = false;
            }

            if (details is not null)
            {
                // A real item, so the ordinary hover test works.
                using (iconFont.Push())
                {
                    var glyph = FontAwesomeIcon.QuestionCircle.ToIconString();
                    if (!first)
                    {
                        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                        if (innerRight - ImGui.GetCursorPosX() < ImGui.CalcTextSize(glyph).X)
                        {
                            ImGui.NewLine();
                            ImGui.SetCursorPosX(homeX);
                        }
                    }

                    ImGui.TextDisabled(glyph);
                }

                if (ImGui.IsItemHovered())
                    Widgets.DrawTooltip(details);

                first = false;
            }

            // Last, so the disclosure and its hover stay adjacent: "name — what it sends (?) ★New".
            DrawTrailingChip(trailingChip, innerRight, homeX, homeXOverride, first);
        }

        // Restores the normal gap the zero spacing above left out.
        ImGui.Spacing();
    }

    /// <summary>
    /// Flows a badge as one more word of <see cref="DrawWrappedWithTrailingHint"/>'s paragraph,
    /// wrapping home when the line has no room left.
    /// </summary>
    /// <param name="innerRight">The edge the paragraph measured its words against.</param>
    /// <param name="homeX">The column wrapped lines return to.</param>
    /// <param name="homeXOverride">The caller's home column, or null when home is the first word's.</param>
    /// <param name="first">
    /// True when the paragraph has drawn nothing, so the cursor may still sit mid-line beside a label.
    /// </param>
    private void DrawTrailingChip(
        (FontAwesomeIcon Icon, string Text, Vector4 Color, string? Tooltip, bool Filled)? chip,
        float innerRight,
        float homeX,
        float? homeXOverride,
        bool first)
    {
        if (chip is not { } badge)
            return;

        // Exactly what DrawChip reserves, so the fit test and the draw agree.
        var width = ChipWidth(badge.Icon, badge.Text);

        // Wraps as the paragraph's words do (see DrawWrappedWithTrailingHint).
        if (!first)
        {
            ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
            if (innerRight - ImGui.GetCursorPosX() < width)
            {
                ImGui.NewLine();
                ImGui.SetCursorPosX(homeX);
            }
        }
        else if (homeXOverride is not null && innerRight - ImGui.GetCursorPosX() < width)
        {
            ImGui.NewLine();
            ImGui.SetCursorPosX(homeX);
        }

        DrawChip(badge.Icon, badge.Text, badge.Color, badge.Filled);

        // DrawChip's footprint is a real item, so the ordinary hover test works.
        if (badge.Tooltip is { } tooltip && ImGui.IsItemHovered())
            Widgets.DrawTooltip(tooltip);
    }

    /// <summary>
    /// The "Off" chip a switched-off collection or feature wears, in the shape
    /// <see cref="DrawWrappedWithTrailingHint"/> flows after a sentence.
    /// </summary>
    /// <param name="tooltip">The sentence its hover shows, saying why it is off.</param>
    /// <remarks>
    /// Gray, so it never competes with a badge that invites action, and filled, so it still registers
    /// on the grayed row.
    /// </remarks>
    private static (FontAwesomeIcon Icon, string Text, Vector4 Color, string? Tooltip, bool Filled) OffChip(
        string? tooltip) =>
        (FontAwesomeIcon.PowerOff, "Off", Brand.DisabledForeground, tooltip, true);

    /// <summary>Draws the <see cref="OffChip"/> after a sharing card's label, with its hover text.</summary>
    /// <param name="offText">The sentence its hover shows, saying why it is off.</param>
    private void DrawOffChip(string offText)
    {
        var chip = OffChip(offText);
        ImGui.SameLine();
        DrawChip(chip.Icon, chip.Text, chip.Color, chip.Filled);

        if (ImGui.IsItemHovered())
            Widgets.DrawTooltip(offText);
    }

    /// <summary>
    /// Draws a muted question mark after the caller's line that shows <paramref name="details"/> on
    /// hover.
    /// </summary>
    /// <remarks>
    /// The disclosure stays visible and only its elaboration hides here, which keeps the consent list
    /// both honest and scannable. A kind of data never moves behind this mark (see
    /// <see cref="Collectors.CategoryInfo.Details"/>).
    /// </remarks>
    private void DrawDetailsHint(string details)
    {
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);

        // The push covers the glyph alone: the icon font has no Latin letters, so text drawn inside
        // it comes out blank.
        using (iconFont.Push())
            ImGui.TextDisabled(FontAwesomeIcon.QuestionCircle.ToIconString());

        // Only the mark is the hover target, so reading down the list lights nothing up.
        if (ImGui.IsItemHovered())
            Widgets.DrawTooltip(details);
    }

    /// <summary>
    /// Whether anything in the collection rows still counts as "New", for the folded "Collections"
    /// header's chip. The rule is <see cref="CategorySettingsView.AnythingIsNew"/>.
    /// </summary>
    private bool AnythingIsNew(IReadOnlyList<CategorySettingsRow> rows) =>
        CategorySettingsView.AnythingIsNew(rows, categoriesBadgedThisSession, groupsBadgedThisSession);

    /// <summary>
    /// Draws a manifest-driven category's group consent checkboxes, saving the ticks and the seen
    /// flags, and badges a group never shown before "New".
    /// </summary>
    /// <remarks>
    /// Drawing a group on either surface marks it seen, saved once per batch, never per frame. A group
    /// under a server-disabled collection was drawn unusable, so it is not yet introduced (see
    /// <see cref="CategorySettingsRow.WasDrawnAsUsable"/>). Only the badging surface adds to
    /// <c>groupsBadgedThisSession</c>, so a user fresh from the wizard sees no badges in the settings.
    /// </remarks>
    /// <param name="showNewChips">
    /// Whether an unseen group wears a "New" badge. False in the wizard, where every group is new.
    /// </param>
    private void DrawGroupCheckboxes(CategorySettingsRow row, bool showNewChips)
    {
        if (row.Groups is not { Count: > 0 } groups)
            return;

        // Read by the wizard's Finish (see wizardShowedGroups).
        if (!configuration.Settings.OnboardingComplete)
            wizardShowedGroups = true;

        // Nests the groups under the category's copy, measured inside the same push.
        var groupIndent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.Indent(groupIndent);

        // Null until a group is new, so a row of seen groups writes nothing.
        List<string>? newlySeen = null;

        foreach (var group in groups)
        {
            // The effective state (see ItemGroupRow.IsEffectivelyOn). It differs from the stored
            // consent only while the parent's scope disables the box, so no click writes it back.
            var groupEnabled = group.IsEffectivelyOn;

            // `##` keys the ImGui ID on the unique group key, as for the category checkbox.
            if (ImGui.Checkbox($"{group.Label}##group-{group.Key}", ref groupEnabled))
            {
                // The category and its groups must agree, since neither sends without the other;
                // ManifestConsent holds that rule.
                ManifestConsent.SetGroupConsent(row, group.Key, groupEnabled, configuration.Settings);
                configuration.Save();
            }

            if (group.IsNew && row.WasDrawnAsUsable)
                (newlySeen ??= new List<string>()).Add(group.Key);

            if (!showNewChips || !row.ServerEnabled)
                continue;

            // Keeps the badge up after the save below makes the group un-new.
            if (group.IsNew)
                groupsBadgedThisSession.Add(group.Key);

            if (group.IsNew || groupsBadgedThisSession.Contains(group.Key))
            {
                ImGui.SameLine();
                DrawChip(FontAwesomeIcon.Star, "New", Brand.Gold);
            }
        }

        if (newlySeen is not null)
        {
            configuration.Settings.MarkItemGroupsSeen(newlySeen);
            configuration.Save();
        }

        ImGui.Unindent(groupIndent);
    }

    /// <summary>
    /// One checkbox that flips every collection at once, groups included (see
    /// <see cref="ManifestConsent.SetRowConsent"/>).
    /// </summary>
    /// <remarks>
    /// Ticked only when everything is on, so a click turns all off from "all on" and all on otherwise.
    /// It writes only the rows drawn beneath it, so it never grants consent for an unseen checkbox.
    /// Rows the server has switched off are neither read nor written, so their consent stays as the
    /// user last set it.
    /// </remarks>
    /// <param name="rows">The rows drawn beneath it, which this control reads and writes.</param>
    private void DrawSelectAll(IReadOnlyList<CategorySettingsRow> rows)
    {
        var allEnabled = ManifestConsent.AllConsentGiven(rows);

        // Disabled, not hidden, so the list keeps its shape whatever the server switches off.
        bool clicked;
        using (ImRaii.Disabled(!ManifestConsent.AnyServerEnabled(rows)))
            clicked = ImGui.Checkbox("All collections##selectAll", ref allEnabled);

        if (clicked)
        {
            // The click answers for every permitted row, so each is marked seen here, as a row's own
            // click is (see DrawCategoryRow).
            var answered = new List<string>(rows.Count);

            foreach (var row in rows)
            {
                if (!row.ServerEnabled)
                    continue;

                ManifestConsent.SetRowConsent(row, allEnabled, configuration.Settings);
                answered.Add(row.Key);
            }

            configuration.Settings.MarkCategoriesSeen(answered);
            configuration.Save();
        }

        ImGui.Spacing();
    }
}
