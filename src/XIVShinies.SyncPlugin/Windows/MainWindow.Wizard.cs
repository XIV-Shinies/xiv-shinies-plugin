using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Windows;

// The first-run wizard: its three steps and the Back/forward footer. One part of the
// MainWindow class — see MainWindow.cs for the class doc, the window state, and the shared
// card system and widget bindings every part draws with.
internal sealed partial class MainWindow
{
    private void DrawWizard()
    {
        // Frame-scoped: the answer must describe THIS frame's rows, not a frame whose rows are gone.
        wizardShowedGroups = false;

        // The branded header carries "Step 1 of 3" — without it the wizard's length is unknowable.
        // The numbers come from the enum's positions, so a new step renumbers this automatically.
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

            // Reaching Done means Finish already ran and flipped OnboardingComplete, so the next
            // frame draws the settings instead. Nothing to render.
            default:
                break;
        }
    }

    private void DrawWelcomeStep()
    {
        // The website's name is picked out in the brand gold mid-sentence, which TextWrapped cannot
        // do — hence the span helper.
        //
        // The configured server rather than the official name: this sentence states where the
        // user's collections are sent, on the first screen they see and before they have consented
        // to anything, so it has to name the server that will actually receive them.
        Widgets.DrawWrappedSpans(
            ($"{PluginMeta.DisplayName} reads what you have collected in game and uploads it to", null),
            ($"{BackendHost()},", Brand.Gold),
            ("so the website knows what you own without you ticking it off by hand.", null));

        Widgets.SectionGap();
        DrawSectionHeading("What it sends");

        // The disclosure every consent surface carries, opening the list: a statement about
        // every collection below, not a caption for any one item.
        DrawCompletenessNote();
        ImGui.Spacing();

        // Whether the Crescent section existed for the tracker's disclosure to hang on.
        var trackerDrawn = false;

        // Each collector describes itself. Adding a collection makes it appear here with no
        // change to this window: a gold gem, then one flowed line — "Name — what it sends" —
        // under the section heading its collector declared, with the hover elaboration trailing
        // the sentence when the collector offered one (a category whose one-liner says
        // everything carries no mark to wonder about). The line is the consent copy telling the
        // user what leaves their machine, so it draws at full contrast. (See
        // DrawCompletenessNote for why this pre-consent screen may never disclose less than the
        // settings.)
        foreach (var section in CategorySettingsView.GroupBySection(BuildCategoryRows()))
        {
            DrawSectionLabel(section.Title);

            foreach (var row in section.Rows)
            {
                DrawIcon(FontAwesomeIcon.Gem, Brand.Gold);
                ImGui.SameLine();
                DrawWrappedWithTrailingHint(
                    $"{row.DisplayName} — {row.WhatGetsSent}", row.Details);
            }

            // The live Occult tracker's disclosure joins the Crescent section: it is occult
            // data a reader scanning by game area expects to find here, even though it is not a
            // collection. The one sanctioned title comparison on a consent surface — the title
            // is the registry's own constant, and the tracker is a bespoke feature, not a
            // registered collector, so no collector gains a name branch by it.
            if (section.Title == CollectorRegistry.OccultSection)
            {
                DrawOccultTrackerDisclosureLine();
                trackerDrawn = true;
            }
        }

        // The heading above only exists while a registered collector declares it, but the
        // tracker's disclosure is owed regardless — the feature is live and default-on whatever
        // the collectors do — so a list without the heading gets it created here.
        if (!trackerDrawn)
        {
            DrawSectionLabel(CollectorRegistry.OccultSection);
            DrawOccultTrackerDisclosureLine();
        }

        Widgets.SectionGap();
        // Names the server the data is actually sent to — see MainWindow.BackendHost.
        DrawPrivacyCard(
            "Your character is identified by a one-way fingerprint computed on this machine. " +
            $"Your character's name and home world are sent so {BackendHost()} can match the " +
            "character you already claimed. Nothing is uploaded until you finish this setup, " +
            "and you choose which of the above to include.");

        DrawWizardNav("Get started");
    }

    /// <summary>
    /// The welcome screen's tracker disclosure, flowed as one sentence: a broadcast tower — it
    /// shares live world state rather than adding anything to your collection — then the name
    /// and copy flowed together, with the what-is-NOT-shared reassurance hover trailing.
    /// </summary>
    private void DrawOccultTrackerDisclosureLine()
    {
        DrawIcon(FontAwesomeIcon.BroadcastTower, Brand.Gold);
        ImGui.SameLine();
        DrawWrappedWithTrailingHint(
            $"Live Occult instance state — {OccultWhatGetsSent}", OccultTrackerDetails);
    }

    private void DrawLinkAccountStep()
    {
        // A verified token is the first moment the server will answer this plugin at all, so it is when
        // the config — and with it the list of item groups the consent step must offer — is fetched.
        // Holding the user here until that answer lands is what makes the next step whole: it sees the
        // group checkboxes from its very first frame, so ticking a category can tick the groups that
        // belong to it, and no consent can be granted for a checkbox that was not on screen at the time.
        // A failed poll still answers, so this can never become a trap; it just leaves the next step
        // with no groups to show.
        onboarding.NotifyAwaitingConfig(
            onboarding.TokenCheck == TokenCheckState.Valid && syncManager.OnboardingConfigPending);

        DrawTokenPanel();
        DrawWizardNav("Continue");
    }

    private void DrawChooseCategoriesStep()
    {
        // Two consent regimes, stated plainly: collections start OFF (they describe the
        // player's own progress), while the live tracker's box starts ticked because it shares
        // world state — and it is on this very screen, so unticking it is one click before
        // anything can send.
        // Scoped to the collections on this screen, because the last box below offers to start
        // collections added by later updates switched on.
        ImGui.TextWrapped(
            "Choose what to upload. The collections below all start switched off — nothing about " +
            "your progress is sent unless you turn it on here. Sharing live Occult instance state " +
            "starts on; untick it below if you would rather not. You can change any of this later.");

        Widgets.SectionGap();

        // Everything this step will ever show is on screen from its first frame: the account step holds
        // the user until the server's config has answered (see DrawLinkAccountStep), so a category's
        // group checkboxes exist by the time its own checkbox can be ticked. That is what makes ticking
        // a category able to tick the groups it means, and it is why no consent here can ever be granted
        // for a checkbox the user was not looking at.
        // See DrawCategoryRows's showNewChips for why the wizard badges nothing.
        DrawCategoryRows(BuildCategoryRows(), showNewChips: false);

        // The live tracker's own consent card, right below the collections it is not part of.
        ImGui.Spacing();
        DrawOccultConsentRow();

        ImGui.Spacing();
        DrawWizardNav("Finish");
    }

    /// <summary>Draws Back and the step's forward button, disabling the latter when the step forbids it.</summary>
    /// <remarks>
    /// Classic wizard footer: Back sits quietly on the left in the default style; the forward button
    /// is the branded primary, right-aligned — the strongest visual weight on the one action that
    /// moves the user forward.
    /// </remarks>
    private void DrawWizardNav(string forwardLabel)
    {
        // The footer gets more air than the sections above it: the primary action should sit
        // apart from the content, not crowd the last paragraph.
        Widgets.SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Wide enough to feel like a primary action even for short labels, and grows with long ones.
        // Back uses the same size, so the footer's two buttons read as a matched pair.
        var buttonSize = new Vector2(
            Math.Max(120f * ImGuiHelpers.GlobalScale,
                ImGui.CalcTextSize(forwardLabel).X + (40f * ImGuiHelpers.GlobalScale)),
            0f);

        if (onboarding.CanGoBack)
        {
            if (BoldButton("Back", buttonSize))
                onboarding.Back();

            ImGui.SameLine();
        }

        // Right-align: the forward button's right edge meets the content region's.
        Widgets.AlignRight(buttonSize.X);

        // The forward button is the wizard's one live action, and the steps that hold it shut — an
        // unverified token, a config still being fetched — are the whole point of it being shut. It has
        // to LOOK shut, which is what PrimaryButton's own disabled treatment is for.
        var forwardPressed = PrimaryButton(forwardLabel, buttonSize, onboarding.CanAdvance);

        if (forwardPressed)
        {
            onboarding.Advance();

            // Finish is a no-op until the last step, so calling it unconditionally is safe: it is the
            // state machine, not this window, that decides when consent has been given.
            onboarding.Finish(configuration.Settings);

            if (configuration.Settings.OnboardingComplete)
            {
                // Settles the one-time migration flag for a user who chose their groups by hand, so that
                // migration can never later re-enable a group they deliberately left off. What settles it
                // is what the wizard DREW, tracked in wizardShowedGroups, never what the server sent: a
                // user shown no checkbox chose nothing, and the migration must stay free to speak for
                // them. See PluginSettings.SettleItemGroupConsent.
                configuration.Settings.SettleItemGroupConsent(wizardShowedGroups);

                // Unconditional: Finish has just written OnboardingComplete, and that has to reach disk
                // whether or not there was any group consent to settle alongside it.
                configuration.Save();
            }
        }
    }
}
