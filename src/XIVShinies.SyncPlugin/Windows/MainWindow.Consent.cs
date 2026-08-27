using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Windows;

// The consent surfaces: the per-category checkbox rows, their per-group checkboxes and "New"
// badges, and the select-all control — shared by the wizard's consent step and the settings
// screen's Collections section. One part of the MainWindow class — see MainWindow.cs for the
// class doc, the window state, and the shared card system and widget bindings.
internal sealed partial class MainWindow
{
    /// <summary>
    /// The settings window's category rows, from the pure builder every consent and status surface in
    /// this window reads.
    /// </summary>
    /// <remarks>
    /// A cheap list build with no game calls in it, but it still allocates — so each frame builds the
    /// list ONCE and hands it to whichever surfaces need it, rather than each surface rebuilding it
    /// for itself sixty times a second on an always-visible path.
    /// </remarks>
    private IReadOnlyList<CategorySettingsRow> BuildCategoryRows() =>
        CategorySettingsView.Build(
            collectors,
            configuration.Settings,
            syncManager.RemoteConfig,
            syncManager.LastSkipped,
            syncManager.LastPartialNotes,
            syncManager.LastCollectedDetails);

    /// <summary>
    /// The consent list, shared by the wizard's consent step and the settings: every section's
    /// rows in one card, each section under a plain label rather than a fold of its own, so
    /// opening the list puts every checkbox it collects consent for on screen at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contains no category or section names. Every label, heading, and description comes from
    /// the rows the collectors produced, which is what keeps "adding a collection is one new
    /// class" true.
    /// </para>
    /// <para>
    /// This card is about <b>consent alone</b> — what the user chooses to send. Whether a chosen
    /// collection could actually be READ is a live status, and it belongs with every other live
    /// status, in the sync card's read-status panel (see <see cref="DrawStatus"/>).
    /// </para>
    /// </remarks>
    /// <param name="rows">This frame's category rows, from <see cref="BuildCategoryRows"/>.</param>
    /// <param name="showNewChips">
    /// Whether a collection, or a manifest group inside one, that the user has not been shown
    /// before wears a "New" badge. The settings pass true; the wizard passes false, because a badge
    /// beside every row at once distinguishes nothing. Either way everything drawn is marked seen,
    /// so the wizard retires the badges of the collections it shows.
    /// </param>
    private void DrawCategoryRows(IReadOnlyList<CategorySettingsRow> rows, bool showNewChips)
    {
        // ItemInnerSpacing is the gap ImGui puts between a checkbox's box and its label — wider
        // here so the labels get some air. Pushed as a style variable scoped to this card.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            // A checkbox's label starts after the box itself plus the inner spacing. The
            // measure seeds each row's description home column and indents the notes and group
            // checkboxes beneath it; taken inside the push so it tracks the label's real
            // position.
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
            DrawSelectAll(rows);
            BrandSeparator();
            ImGui.Spacing();

            // Null until a category turns out to be new, so the usual case allocates nothing.
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

            // One save for the whole batch. Marking them seen makes the next rebuild report
            // IsNew=false, so this runs once per batch of newly-seen categories rather than every
            // frame — the same lifecycle DrawGroupCheckboxes uses for groups.
            if (newlySeenCategories is not null)
            {
                configuration.Settings.MarkCategoriesSeen(newlySeenCategories);
                configuration.Save();
            }

            // The category rows end with a single Spacing, which is the gap BETWEEN rows. This note
            // is not another row, so it gets a wider gap above its divider to read as its own
            // closing remark rather than a continuation of the last collection's copy.
            ImGui.Spacing();
            ImGui.Spacing();

            BrandSeparator();
            ImGui.Spacing();
            DrawCompletenessNote();
        }
    }

    /// <summary>
    /// One collection's consent row: the checkbox, its disclosure copy, and its group checkboxes.
    /// Shared verbatim by both consent surfaces, so neither can drift to showing less.
    /// </summary>
    /// <param name="row">The row to draw.</param>
    /// <param name="showNewChips">
    /// Whether this row, and the groups beneath it, may wear a "New" badge. See
    /// <see cref="DrawCategoryRows"/>; the row is reported as needing its seen flag persisted
    /// either way.
    /// </param>
    /// <param name="checkboxColumn">
    /// The width from a row's left edge to its checkbox label — the flowed description's home
    /// column, and the indent for the notes and group checkboxes beneath the row. Measured by
    /// the caller inside its ItemInnerSpacing push so it tracks the label's real position.
    /// </param>
    /// <returns>
    /// True when this row was drawn for the first time and its seen flag still needs persisting.
    /// The caller batches those into one save.
    /// </returns>
    private bool DrawCategoryRow(CategorySettingsRow row, bool showNewChips, float checkboxColumn)
    {
        var enabled = row.UserEnabled;
        bool toggled;

        // Captured before the checkbox draws: the column its label starts in is where the
        // flowed description's wrapped lines come home to.
        var labelColumn = ImGui.GetCursorPosX() + checkboxColumn;

        // The server switched this category off for everyone. Show it, disabled, with the
        // user's own preference intact underneath — flipping it back on later restores what
        // they chose.
        using (ImRaii.Disabled(!row.ServerEnabled))
        {
            // Everything after `##` is hidden from the label but forms part of the widget's
            // identity. ImGui derives a control's ID from its label text, so two collections
            // that happened to choose the same DisplayName would share an ID and cross-wire
            // their clicks. The category key is unique by construction, which makes this
            // collision impossible rather than merely unlikely.
            toggled = ImGui.Checkbox($"{row.DisplayName}##{row.Key}", ref enabled);
        }

        if (toggled)
        {
            ManifestConsent.SetRowConsent(row, enabled, configuration.Settings);
            configuration.Save();
        }

        if (showNewChips)
        {
            // Remembering the key keeps the chip drawn once the caller's batched save makes the
            // next rebuild report the row un-new — the same lifecycle DrawGroupCheckboxes uses.
            if (row.IsNew)
                categoriesBadgedThisSession.Add(row.Key);

            // Drawn between the name and its description, so the row reads
            // "Orchestrion rolls ★New — what it sends".
            if (categoriesBadgedThisSession.Contains(row.Key))
            {
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                DrawChip(FontAwesomeIcon.Star, "New", Brand.Gold);
            }
        }

        // The consent copy flows on the label's own line — "Name — what it sends" — with the
        // collector's hover elaboration trailing the sentence when it offered one, and wrapped
        // lines coming home under the label. It is what the plugin will send if the box is
        // ticked, so it draws at full contrast.
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        DrawWrappedWithTrailingHint($"— {row.WhatGetsSent}", row.Details, labelColumn);

        ImGui.Indent(checkboxColumn);

        // Muted: it only restates why the checkbox above it is grayed out, which the
        // disabled control already conveys on its own.
        if (!row.ServerEnabled)
            ImGui.TextDisabled("Temporarily switched off by XIV Shinies.");

        // Disabled along with the category above them. A group belongs to its category and is
        // only ever scanned as part of that category's pass, so leaving the groups live under a
        // greyed-out parent would offer the user a consent choice that cannot mean anything —
        // and ticking one would switch its category back on behind the very control that says it
        // is off.
        using (ImRaii.Disabled(!row.ServerEnabled))
            DrawGroupCheckboxes(row, showNewChips);

        ImGui.Unindent(checkboxColumn);
        ImGui.Spacing();

        // Drawing a category IS showing it to the user, so it is reported seen regardless of which
        // surface drew it — the wizard shows a collection just as plainly as the settings screen.
        return row.IsNew;
    }

    /// <summary>
    /// The live Occult tracker's plain-language disclosure — every kind of data the tracker
    /// itself shares (the character identity beside it is <see cref="DrawPrivacyCard"/>'s
    /// disclosure, as for every category). One string, used verbatim by every surface that
    /// discloses the tracker (the wizard's "What it sends" screen and the consent card), so no
    /// surface can drift to saying less than another.
    /// </summary>
    private const string OccultWhatGetsSent =
        "While you are in the Occult Crescent, shares your instance's public encounter " +
        "status (critical encounters, FATEs, Forked Tower) and your current world, powering " +
        "XIV Shinies' live tracker.";

    /// <summary>
    /// The tracker's hover elaboration: because the natural worry is other players, it says
    /// what is NOT shared. Reassurance rather than a kind of data, so it follows the same split
    /// as <see cref="Collectors.CategoryInfo.Details"/>.
    /// </summary>
    private const string OccultTrackerDetails =
        "This is world state — nothing about you beyond your presence, and never anything " +
        "about other players.";

    /// <summary>
    /// The live Occult tracker's consent card: its toggle and its disclosure copy. Drawn as its
    /// own card on both consent surfaces (the wizard's consent step and the settings), separate
    /// from the collections list — the tracker is not a collection and must not read as one,
    /// which is also why the collections select-all does not touch it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike every collection category, this defaults ON — it shares <b>world</b> state (which
    /// encounters are up inside a public instance), not facts about the player, the same stance
    /// Universalis takes on market data. It still only ever acts once the user has finished the
    /// wizard and the master switch is on, and this card makes the sharing visible and
    /// revocable on both consent surfaces.
    /// </para>
    /// <para>
    /// Drawn greyed out when the server's <c>occultTracker</c> switch is off, with the user's
    /// own preference intact underneath — the same treatment a server-disabled category gets.
    /// A config with no <c>occultTracker</c> block (or none fetched yet) draws normally: the
    /// toggle records the user's choice either way, and the manager independently refuses to
    /// upload to a server that never advertised the feature.
    /// </para>
    /// </remarks>
    private void DrawOccultConsentRow()
    {
        var occultConfig = syncManager.RemoteConfig?.OccultTracker;
        var serverOff = occultConfig is { Enabled: false };

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            // Measured the same way as the collections card's label column, inside the same
            // style push, so the two cards' text edges line up when stacked.
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;

            var enabled = configuration.Settings.ShareOccultInstanceState;
            bool toggled;
            using (ImRaii.Disabled(serverOff))
                toggled = ImGui.Checkbox("Share live Occult instance state##occultTracker", ref enabled);

            // The what-is-NOT-shared reassurance, one hover away like every category's.
            DrawDetailsHint(OccultTrackerDetails);

            if (toggled)
            {
                configuration.Settings.ShareOccultInstanceState = enabled;
                configuration.Save();
            }

            ImGui.Indent(checkboxColumn);

            // Full contrast, like every category's WhatGetsSent line: this is consent copy.
            DrawWrapped(OccultWhatGetsSent, ImGuiCol.Text);

            // Muted: it only restates why the toggle above is greyed out.
            if (serverOff)
                ImGui.TextDisabled("Temporarily switched off by XIV Shinies.");

            ImGui.Unindent(checkboxColumn);

            ImGui.Spacing();

            // The standing choice for features that do not exist yet — see
            // PluginSettings.AutoEnableNewFeatures for what a future feature's migration does
            // with it. Ticking it here is the explicit consent that answer rests on, which is
            // why the copy promises anything enabled this way shows up on this screen.
            var autoEnable = configuration.Settings.AutoEnableNewFeatures;
            if (ImGui.Checkbox("Turn on future sharing features automatically##autoEnableNew", ref autoEnable))
            {
                configuration.Settings.AutoEnableNewFeatures = autoEnable;
                configuration.Save();
            }

            ImGui.Indent(checkboxColumn);
            DrawWrapped(
                "When an update adds a new kind of sharing (like the live tracker above), start " +
                "it switched on. Anything added this way always appears on this screen, where " +
                "you can turn it off.",
                ImGuiCol.Text);
            ImGui.Unindent(checkboxColumn);
        }
    }

    /// <summary>
    /// Discloses that a collection the plugin can read end to end is reported as complete, and what
    /// XIV Shinies is then entitled to do with that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every category's own copy says what the plugin FINDS. A snapshot upload additionally declares
    /// which of those lists it read completely (see <see cref="Api.SyncRequest.CollectionScopes"/>
    /// for what that licenses). That is a claim made about the user's OWN entries — it can put a
    /// question mark against something they marked by hand — so it is disclosed on the consent
    /// surface rather than living only in the contract.
    /// </para>
    /// <para>
    /// Drawn by the wizard's "What it sends" step and at the foot of the shared consent card,
    /// which the wizard's consent step and the settings both draw — so every consent surface
    /// carries it and none can drift: the pre-consent screen must never disclose less than
    /// the screen that collects the ticks. Phrased conditionally and naming no
    /// category, so it stays true however many collections can be read completely.
    /// </para>
    /// <para>
    /// The visible line carries the consequence, not just the mechanism. "Reported as complete" on
    /// its own reads as inert bookkeeping; what a user needs to know is that it can surface one of
    /// their own marks for review. The reassurance that nothing is ever undone stays in the hover —
    /// that is comfort, not disclosure.
    /// </para>
    /// </remarks>
    private void DrawCompletenessNote()
    {
        DrawWrappedWithTrailingHint(
            "Lists the plugin can read in full are reported as complete, which lets XIV Shinies " +
            "point out anything you marked by hand that the plugin did not find.",
            "It is only ever pointed out for you to review — nothing is unmarked for you, and a " +
            "mark you make afterwards is never questioned.");
    }

    /// <summary>
    /// Draws a wrapped paragraph from wherever the cursor stands — continuation lines come back
    /// to the first word's own column by default, so a paragraph started beside an icon stays
    /// aligned under itself — with a muted question mark flowing as its final word when
    /// <paramref name="details"/> offers hover text.
    /// </summary>
    /// <remarks>
    /// The paragraph sibling of <see cref="DrawDetailsHint"/>, which trails a one-line label:
    /// <c>SameLine</c> after a WRAPPED paragraph anchors at the end of its first line, leaving
    /// the mark floating mid-paragraph. So the paragraph is flowed here word by word (the same
    /// flow as <see cref="Widgets.DrawWrappedSpans"/>) and the mark is appended as one more
    /// word, wrapping to the next line only when it genuinely does not fit. Fit is measured
    /// against the enclosing card's inner edge when there is one, so the lines break where the
    /// card's own wrapped text does; drawn outside a card, it measures to the window's content
    /// edge instead.
    /// </remarks>
    /// <param name="text">The paragraph.</param>
    /// <param name="details">The hover text behind the trailing mark, or null for no mark.</param>
    /// <param name="homeXOverride">
    /// Where wrapped lines return to, in cursor space — for a paragraph whose continuation
    /// belongs under an earlier column (a checkbox label) rather than under its own first word.
    /// Null returns them to the first word's column.
    /// </param>
    private void DrawWrappedWithTrailingHint(
        string text, string? details, float? homeXOverride = null)
    {
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var innerRight = activeCardInnerRight
            ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        // Read before any word draws: the flow moves the cursor, so the first word's own
        // column can only be captured now.
        var homeX = homeXOverride ?? ImGui.GetCursorPosX();
        var first = true;

        // The word flow is the only wrapping authority (see DrawWrappedSpans for why the
        // surrounding wrap scope is switched off), and the zero vertical item spacing keeps the
        // flowed lines as tight as a single wrapped text call's. Both pushes end before the
        // mark below, so whatever the caller draws after this paragraph gets its normal gap.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemSpacing,
                   new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f)))
        using (ImRaii.TextWrapPos(-1f))
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!first)
                {
                    // Tentatively continue the line, then wrap if the word will not fit before
                    // the right edge measured above.
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
                    // Only when the caller chose a home column: mid-line placement can leave no
                    // room even for the FIRST word, and wrapping home genuinely widens the
                    // line. Without an override, home IS the current column, so wrapping would
                    // just repeat the same failed fit.
                    ImGui.NewLine();
                    ImGui.SetCursorPosX(homeX);
                }

                ImGui.TextUnformatted(word);
                first = false;
            }
        }

        if (details is null)
        {
            // The words above were submitted with zero vertical spacing; a zero-size advance
            // outside the push restores the normal gap before whatever the caller draws next.
            ImGui.Spacing();
            return;
        }

        // The mark, flowed as the sentence's last word. Drawn as a real item (unlike a
        // draw-list glyph), so the hover test below is the ordinary one.
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
    }

    /// <summary>
    /// Draws a muted question mark on the current line that reveals <paramref name="details"/> on
    /// hover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The consent list is a compliance surface AND a list people have to be able to scan. Those
    /// pull in opposite directions: every caveat worth stating makes the wall of text a reader is
    /// less likely to finish. Splitting it — the disclosure always visible, the elaboration one
    /// hover away — keeps the surface honest without making it unreadable. What may move behind
    /// this mark is bounded, and the rule lives on
    /// <see cref="Collectors.CategoryInfo.Details"/>: never a kind of data, only detail that makes
    /// the visible line trustworthy.
    /// </para>
    /// <para>
    /// <c>SameLine</c> puts the glyph on the line the caller just drew, so it trails the label or
    /// sentence it belongs to instead of starting a row of its own. The icon font is pushed for the
    /// glyph alone: FontAwesome has no Latin letters, so text drawn while it is active renders as
    /// blanks.
    /// </para>
    /// </remarks>
    /// <param name="details">The hover text.</param>
    private void DrawDetailsHint(string details)
    {
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);

        using (iconFont.Push())
            ImGui.TextDisabled(FontAwesomeIcon.QuestionCircle.ToIconString());

        // Answers for the glyph just drawn — the hover target is the mark itself, which is why it
        // is small and deliberate rather than the whole row lighting up as a person reads down it.
        if (ImGui.IsItemHovered())
            Widgets.DrawTooltip(details);
    }

    /// <summary>
    /// Whether the folded "Collections" header (see <see cref="DrawSettings"/>) wears a "New" chip.
    /// </summary>
    /// <remarks>
    /// With the header folded, none of the badges beneath it are visible, so something added since
    /// the last session would go unnoticed until the user happened to expand it. The rule is
    /// <see cref="CategorySettingsView.AnythingIsNew"/>; this hands it the two session sets the
    /// draw loop maintains.
    /// </remarks>
    /// <param name="rows">The category rows to scan, from <see cref="CategorySettingsView.Build"/>.</param>
    private bool AnythingIsNew(IReadOnlyList<CategorySettingsRow> rows) =>
        CategorySettingsView.AnythingIsNew(rows, categoriesBadgedThisSession, groupsBadgedThisSession);

    /// <summary>
    /// Draws the per-group consent checkboxes beneath a manifest-driven category and persists both
    /// the toggles and the seen-once flags, optionally badging a group the user has not been shown
    /// before with a "New" chip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seen-marking is the subtle part. A group arrives from <see cref="CategorySettingsView.Build"/>
    /// with <c>IsNew = true</c> until its persisted "seen" flag is set. The first frame we draw it we set
    /// that flag (one write), so every later frame's rebuild reports <c>IsNew = false</c> and this method
    /// stops writing for that group — the config is saved once per batch of newly-seen groups, never per
    /// frame (a per-frame save would be a real bug). Marking seen happens on <b>whichever surface drew
    /// the group</b>, wizard or settings: it records that the user has been shown it, and the wizard's
    /// consent step shows it just as plainly as the settings do.
    /// </para>
    /// <para>
    /// <c>groupsBadgedThisSession</c> is a separate question — "is this group's badge currently on screen?" — and
    /// only the badge-drawing surface adds to it. The badge would otherwise blink out one frame after it
    /// appeared, since the persisted flag we just set makes the very next rebuild report the group as no
    /// longer new; remembering the key keeps it drawn for the rest of the session, while the persisted
    /// flag guarantees it is gone on the next load. A group first drawn by the WIZARD never enters that
    /// set, which is what leaves the settings screen badge-free for a user who has just finished setup:
    /// they have already seen every group there is.
    /// </para>
    /// </remarks>
    /// <param name="row">The category row whose groups are being drawn.</param>
    /// <param name="showNewChips">
    /// Whether an unseen group wears a "New" badge. False in the wizard, where every group is new by
    /// definition and a chip beside each of them says nothing.
    /// </param>
    private void DrawGroupCheckboxes(CategorySettingsRow row, bool showNewChips)
    {
        // Nothing to draw unless the server sent consent groups for this manifest-driven category.
        if (row.Groups is not { Count: > 0 } groups)
            return;

        // Past this point at least one group checkbox is going on screen — the fact the wizard's Finish
        // handler settles its consent on. See PluginSettings.SettleItemGroupConsent for what rides on it.
        if (!configuration.Settings.OnboardingComplete)
            wizardShowedGroups = true;

        // A further indent nests the group checkboxes beneath their category's description. Measured
        // the same way as the category column, inside the ItemInnerSpacing push the consent surface
        // opened, so it tracks the same spacing.
        var groupIndent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.Indent(groupIndent);

        // Collected while drawing, then persisted once after the loop. Null until the first genuinely
        // new group is seen, so a row whose groups are all already-seen writes nothing.
        List<string>? newlySeen = null;

        foreach (var group in groups)
        {
            var groupEnabled = group.Enabled;

            // Same `##key` identity trick as the category checkboxes above: the visible label is the
            // group's, but the widget's ImGui id comes from the unique group key, so two groups that
            // chose the same label never cross-wire their clicks.
            if (ImGui.Checkbox($"{group.Label}##group-{group.Key}", ref groupEnabled))
            {
                // The category and its groups have to agree, because neither can send anything without
                // the other. That rule lives in ManifestConsent, where it is unit-tested and names no
                // category; this window only reports the click.
                ManifestConsent.SetGroupConsent(row, group.Key, groupEnabled, configuration.Settings);
                configuration.Save();
            }

            // Drawing a group IS showing it to the user, so it is marked seen regardless of which
            // surface drew it — the wizard's consent step shows a group just as plainly as the
            // settings screen does.
            if (group.IsNew)
                (newlySeen ??= new List<string>()).Add(group.Key);

            if (!showNewChips)
                continue;

            // Remember that this group's badge went up, so it keeps drawing for the rest of the session
            // even though the seen-marking persisted after this loop makes the next rebuild report it
            // as un-new.
            if (group.IsNew)
                groupsBadgedThisSession.Add(group.Key);

            // The badge shows for a group this install has never displayed, and for one whose badge went
            // up earlier this session. It is a small outlined chip with a leading star (see DrawChip),
            // so "New" reads as a compact badge beside the checkbox rather than another line of body
            // copy; Brand.Gold is the "shiny" accent used for highlights elsewhere.
            if (group.IsNew || groupsBadgedThisSession.Contains(group.Key))
            {
                ImGui.SameLine();
                DrawChip(FontAwesomeIcon.Star, "New", Brand.Gold);
            }
        }

        // Persist the seen-once flags for every group that was new this frame, in a single save. Because
        // marking them seen makes the next rebuild report IsNew=false, this runs once per batch of
        // newly-seen groups rather than every frame.
        if (newlySeen is not null)
        {
            configuration.Settings.MarkItemGroupsSeen(newlySeen);
            configuration.Save();
        }

        ImGui.Unindent(groupIndent);
    }

    /// <summary>One checkbox that flips every collection at once.</summary>
    /// <remarks>
    /// <para>
    /// Shown checked only when everything is on, so clicking it always does the obvious thing:
    /// from "all on" it turns everything off, from anything else it turns everything on. It
    /// never names a category — it iterates whatever rows it is handed, and its one caller
    /// (<see cref="DrawCategoryRows"/>) hands it the very list drawn beneath it, so a bulk write
    /// can never grant consent for a checkbox the user cannot see. A row the server has switched
    /// off is left out of both the reading and the writing, itself and its groups alike: that
    /// category uploads nothing whatever the boxes say, so this control leaves its consent
    /// exactly as the user last set it, ready to mean something again if the server switches it
    /// back on.
    /// </para>
    /// <para>
    /// "Everything" includes the per-group consent checkboxes nested under a manifest-driven row (see
    /// <see cref="DrawGroupCheckboxes"/>), both in what it writes and in whether it reads as checked.
    /// A category whose groups are all off uploads nothing at all, so a select-all that ticked the
    /// category and left its groups off would switch on a collection that still sends nothing —
    /// and would then keep showing itself unchecked, because a group somewhere is still off. The
    /// groups stay individually toggleable afterwards; this only sets a starting point.
    /// </para>
    /// <para>
    /// This says nothing about a group that arrives LATER. A group the server adds after this click
    /// has never appeared in any list the user has looked at, so it starts off and stays off until
    /// they tick it — <see cref="PluginSettings.IsItemGroupEnabled"/> is an allowlist, and only the
    /// groups on screen are ever written here.
    /// </para>
    /// </remarks>
    /// <param name="rows">The rows this control reads and writes — the ones drawn beneath it.</param>
    private void DrawSelectAll(IReadOnlyList<CategorySettingsRow> rows)
    {
        // Whether the box reads as ticked is a rule about consent, not about drawing, so it lives in
        // ManifestConsent with the rest of them and is unit-tested there.
        var allEnabled = ManifestConsent.AllConsentGiven(rows);

        if (ImGui.Checkbox("All collections##selectAll", ref allEnabled))
        {
            foreach (var row in rows)
            {
                if (row.ServerEnabled)
                    ManifestConsent.SetRowConsent(row, allEnabled, configuration.Settings);
            }

            configuration.Save();
        }

        ImGui.Spacing();
    }
}
