using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Occult;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Windows;

// The first-run wizard: its three steps and the Back and forward footer.
internal sealed partial class MainWindow
{
    private void DrawWizard()
    {
        // Reset each frame, so they describe this frame's rows.
        wizardShowedGroups = false;
        wizardCouldOfferOccultToggle = false;

        // "Step 1 of 3", numbered from the enum's positions, so a new step renumbers it.
        var stepCount = (int)OnboardingStep.Done;
        var stepNumber = (int)onboarding.Step + 1;
        DrawBrandTitle(stepNumber <= stepCount ? $"Step {stepNumber} of {stepCount}" : null);

        switch (onboarding.Step)
        {
            case OnboardingStep.Welcome:
                DrawWelcomeStep();
                break;

            case OnboardingStep.LinkAccount:
                DrawLinkAccountStep();
                break;

            case OnboardingStep.ChooseCategories:
                DrawChooseCategoriesStep();
                break;

            // Done: Finish has set OnboardingComplete, so the next frame draws the settings.
            default:
                break;
        }
    }

    private void DrawWelcomeStep()
    {
        // Names the configured server: this says where the collections go, before any consent.
        Widgets.DrawWrappedSpans(
            ($"{PluginMeta.DisplayName} reads what you have collected in game and uploads it to", null),
            ($"{BackendHost()},", Brand.Gold),
            ("so the website knows what you own without you ticking it off by hand.", null));

        Widgets.SectionGap();
        DrawSectionHeading("What it sends");

        // Opens the list, since it speaks for every collection below.
        DrawCompletenessNote();
        ImGui.Spacing();

        // Whether the Crescent section existed for the tracker's line to join.
        var trackerDrawn = false;

        // One snapshot, so the lines and the privacy card decide from the same answer.
        var remoteConfig = syncManager.RemoteConfig;
        var welcomeRows = BuildCategoryRows(remoteConfig);

        // Each collector's own consent copy, at full contrast: this screen never discloses less than the
        // settings (see DrawCompletenessNote), and a new collection needs no change here. A line the
        // server has off mutes and wears the "Off" chip, seen only on going Back after the server answers.
        foreach (var section in CategorySettingsView.GroupBySection(welcomeRows))
        {
            DrawSectionLabel(section.Title);

            foreach (var row in section.Rows)
            {
                // HostPlaceholder fills in the configured website.
                var off = !row.ServerEnabled;
                DrawIcon(FontAwesomeIcon.Gem, off ? Brand.DisabledForeground : Brand.Gold);
                ImGui.SameLine();
                DrawWrappedWithTrailingHint(
                    $"{row.DisplayName} — {HostPlaceholder.Fill(row.WhatGetsSent, BackendHost())}",
                    row.Details is { } details ? HostPlaceholder.Fill(details, BackendHost()) : null,
                    trailingChip: off ? OffChip(row.ServerOffText(BackendHost())) : null,
                    muted: off);
            }

            // The tracker's line joins the Crescent section, where a reader scanning by game area looks. The
            // one sanctioned title check on a consent surface: no collector gains a name branch by it.
            if (section.Title == CollectorRegistry.OccultSection)
            {
                DrawOccultTrackerDisclosureLine(remoteConfig);
                trackerDrawn = true;
            }
        }

        // The default-on tracker's disclosure is owed whatever sections the collectors declare.
        if (!trackerDrawn)
        {
            DrawSectionLabel(CollectorRegistry.OccultSection);
            DrawOccultTrackerDisclosureLine(remoteConfig);
        }

        // Under its own heading: no collector declares a Crucible section.
        DrawSectionLabel(CrucibleSectionTitle);
        DrawCrucibleDisclosureLine(remoteConfig);

        Widgets.SectionGap();

        // Names the server that receives the data. "You choose" only while the server leaves a
        // choice (see ConsentCopy.UserHasAChoice).
        DrawPrivacyCard(
            "Your character is identified by a one-way fingerprint computed on this machine. " +
            $"Your character's name and home world are sent so {BackendHost()} can match the " +
            "character you already claimed and verified. Nothing is uploaded until you finish " +
            (ConsentCopy.UserHasAChoice(welcomeRows, remoteConfig)
                ? "this setup, and you choose which of the above to include."
                : "this setup."));

        DrawWizardNav("Get started");
    }

    /// <summary>The welcome screen's heading for the Crucible run sharing.</summary>
    private const string CrucibleSectionTitle = "The Crucible of the Unbroken";

    /// <summary>
    /// The welcome screen's tracker line, under a broadcast tower since it shares live state rather
    /// than collecting. Muted and chipped "Off" while the server has the tracker off.
    /// </summary>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    private void DrawOccultTrackerDisclosureLine(ConfigResponse? remoteConfig)
    {
        // Null while the server permits the tracker.
        var offText = OccultGate.ServerOffText(remoteConfig, BackendHost());
        DrawIcon(FontAwesomeIcon.BroadcastTower, offText is null ? Brand.Gold : Brand.DisabledForeground);
        ImGui.SameLine();
        DrawWrappedWithTrailingHint(
            $"Live Occult instance state — {OccultWhatGetsSent()}",
            OccultTrackerDetails,
            trailingChip: offText is null ? null : OffChip(offText),
            muted: offText is not null);
    }

    /// <summary>
    /// The welcome screen's Crucible line, under a broadcast tower since it shares play as it happens
    /// rather than collecting. Muted and chipped "Off" while the server has the sharing off.
    /// </summary>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if none has arrived.</param>
    private void DrawCrucibleDisclosureLine(ConfigResponse? remoteConfig)
    {
        var offText = CrucibleGate.ServerOffText(remoteConfig, BackendHost());
        DrawIcon(FontAwesomeIcon.BroadcastTower, offText is null ? Brand.Gold : Brand.DisabledForeground);
        ImGui.SameLine();
        DrawWrappedWithTrailingHint(
            $"Crucible runs — {CrucibleWhatGetsSent()}",
            CrucibleRunsDetails,
            trailingChip: offText is null ? null : OffChip(offText),
            muted: offText is not null);
    }

    private void DrawLinkAccountStep()
    {
        // Waits here for the config, so the consent step never grants consent for a group checkbox
        // not yet on screen. A failed poll still answers, so this cannot trap the user.
        onboarding.NotifyAwaitingConfig(
            onboarding.TokenCheck == TokenCheckState.Valid && syncManager.OnboardingConfigPending);

        DrawTokenPanel();
        DrawWizardNav("Continue");
    }

    private void DrawChooseCategoriesStep()
    {
        // One snapshot, so the sentence, the rows and the sharing cards decide from the same answer.
        var remoteConfig = syncManager.RemoteConfig;

        // Every group checkbox is here from the first frame (see DrawLinkAccountStep), so ticking a
        // category can tick its groups.
        var wizardRows = BuildCategoryRows(remoteConfig);

        // States the defaults only while the boxes bear them out (see ConsentCopy.WizardIntro). The
        // user's choices are read as the step opens, so the text does not reflow mid-click.
        chooseStepOpening ??= (
            ConsentCopy.AnythingChosenOn(wizardRows, configuration.Settings.ShareCrucibleRuns),
            configuration.Settings.ShareOccultInstanceState);

        var opening = chooseStepOpening.Value;
        DrawWrapped(
            ConsentCopy.WizardIntro(
                opening.AnythingSwitchedOn,
                trackerOffered: !OccultGate.ServerHasSwitchedOff(remoteConfig),
                trackerOn: opening.TrackerOn,
                userHasAChoice: ConsentCopy.UserHasAChoice(wizardRows, remoteConfig),
                host: BackendHost()),
            ImGuiCol.Text);

        Widgets.SectionGap();

        // The wizard has no sync card, so the pause is stated here, above the rows it grays, rather
        // than left to a chip's hover.
        if (ManifestConsent.ServerHasPausedEverything(wizardRows))
        {
            DrawWarning(ServerOffCopy.Paused(BackendHost()));
            Widgets.SectionGap();
        }
        else if (ManifestConsent.ServerPermitsNoCollection(wizardRows))
        {
            // The same gap without a pause: every collection switched off one by one.
            DrawWarning(ServerOffCopy.EveryCollection(BackendHost()));
            Widgets.SectionGap();
        }

        // Grayed rows and a grayed Crucible card stay unseen and wear "New" in the settings later; a
        // grayed tracker is recorded as declined (see PluginSettings.SettleOccultConsent).
        DrawCategoryRows(wizardRows, showNewChips: false);

        ImGui.Spacing();
        DrawCrucibleConsentRow(remoteConfig, showNewChip: false);
        ImGui.Spacing();
        DrawOccultConsentRow(remoteConfig);

        ImGui.Spacing();
        DrawWizardNav("Finish");
    }

    /// <summary>Draws Back and the forward button, disabled while the step forbids advancing.</summary>
    private void DrawWizardNav(string forwardLabel)
    {
        Widgets.SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // A floor for short labels. Back shares the size, so the two read as a pair.
        var buttonSize = new Vector2(
            Math.Max(120f * ImGuiHelpers.GlobalScale,
                ImGui.CalcTextSize(forwardLabel).X + (40f * ImGuiHelpers.GlobalScale)),
            0f);

        if (onboarding.CanGoBack)
        {
            if (BoldButton("Back", buttonSize))
            {
                onboarding.Back();
                chooseStepOpening = null;
            }

            ImGui.SameLine();
        }

        Widgets.AlignRight(buttonSize.X);

        // It must look shut while a step holds it: an unverified token, or a config on its way.
        var forwardPressed = PrimaryButton(forwardLabel, buttonSize, onboarding.CanAdvance);

        if (forwardPressed)
        {
            onboarding.Advance();
            chooseStepOpening = null;

            // A no-op before the last step: the state machine, not this window, decides when consent
            // is given.
            onboarding.Finish(configuration.Settings);

            if (configuration.Settings.OnboardingComplete)
            {
                // Settled by what the wizard drew, never by what the server sent (see
                // PluginSettings.SettleItemGroupConsent).
                configuration.Settings.SettleItemGroupConsent(wizardShowedGroups);

                // The same for the tracker, which would otherwise stay on without having been offered
                // (see PluginSettings.SettleOccultConsent).
                configuration.Settings.SettleOccultConsent(wizardCouldOfferOccultToggle);

                // Always: OnboardingComplete must reach disk whatever the settles changed.
                configuration.Save();
            }
        }
    }
}
