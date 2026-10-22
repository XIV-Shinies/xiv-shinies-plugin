using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Onboarding;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

// The account panel shared by the wizard's link step and the settings: the token box, its
// feedback, the claimed characters, and the token probe plumbing.
internal sealed partial class MainWindow
{
    /// <summary>The token box, its Verify button, and the resulting feedback.</summary>
    /// <remarks>
    /// The settings need it too: the wizard never returns after onboarding, so a revoked token would
    /// otherwise leave nowhere to paste a new one.
    /// </remarks>
    private void DrawTokenPanel()
    {
        using (BrandCard())
        {
            // The right-aligned button opens the website's profile settings, where tokens are made.
            DrawCardTitle(FontAwesomeIcon.Key, "Your account");

            const string openLabel = "Open profile settings";
            var openWidth = IconButtonWidth(FontAwesomeIcon.Globe, openLabel);
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            Widgets.AlignRight(openWidth, innerRight);
            var openButtonPos = ImGui.GetCursorPos();

            // The configured server, validated on the way out (see BackendUrl.ProfileUrl).
            if (BoldButton("###openProfile", new Vector2(openWidth, 0f)))
                Util.OpenLink(BackendUrl.ProfileUrl(configuration.Settings.BaseUrl));

            DrawButtonFeedback(
                openButtonPos, openWidth, FontAwesomeIcon.Globe, Brand.Teal,
                openLabel, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);

            CloseCardHeader();

            // Names the configured server, where the button goes.
            DrawWrapped(
                $"Create a plugin token in your profile settings on {BackendHost()}, then paste " +
                "it below. The token is shown once and can be revoked at any time.",
                ImGuiCol.Text);

            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));

            // Busy until the probe and, in the wizard, the config it fetches have both answered, so
            // an idle button never invites a second press.
            var checkInFlight = verifier.InFlight || onboarding.AwaitingConfig;

            // Three periods, not "…": the font centers that glyph vertically.
            var verifyLabel = checkInFlight ? "Checking..." : "Verify";

            // The widest label the button wears, so the box keeps its size while the label changes.
            var verifyWidth = Math.Max(PaddedButtonWidth("Verify"), PaddedButtonWidth("Checking..."));

            var rightEdge = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            var available =
                rightEdge - ImGui.GetCursorPosX() - verifyWidth - ImGui.GetStyle().ItemSpacing.X;

            // Clamped: a token is 47 characters, and ImGui reads a negative width as "from the right
            // edge".
            ImGui.SetNextItemWidth(Math.Clamp(
                available, 120f * ImGuiHelpers.GlobalScale, 420f * ImGuiHelpers.GlobalScale));

            // Masked: it is a credential, and plugin windows are often on screen while streaming.
            if (ImGui.InputText("##token", ref tokenInput, TokenInputCapacity, ImGuiInputTextFlags.Password))
            {
                // An edit voids the last check; Forget discards any answer still in flight for the
                // old token.
                onboarding.NotifyTokenEdited();
                verifier.Forget();
                account = null;
            }

            ImGui.SameLine();

            var canVerify = !checkInFlight && TokenFormat.IsWellFormed(tokenInput);

            bool verifyPressed;
            using (ImRaii.Disabled(!canVerify))
                verifyPressed = BoldButton(verifyLabel, new Vector2(verifyWidth, 0f));

            if (verifyPressed)
            {
                // Saved before the probe, since the API client reads it from the settings. Saving it
                // unverified is safe: nothing uploads before onboarding or with the master switch off.
                configuration.Settings.Token = tokenInput;
                configuration.Save();

                onboarding.BeginTokenCheck();
                verifier.Start();
            }

            DrawTokenCheckFeedback();
        }
    }

    /// <summary>
    /// Says what the token box should be telling the user, in a matching color.
    /// <see cref="TokenFeedback"/> picks the message.
    /// </summary>
    private void DrawTokenCheckFeedback()
    {
        ImGui.Spacing();

        // Continue stays disabled until the config arrives, so the wait says what it waits for.
        if (onboarding.AwaitingConfig)
        {
            DrawWrapped($"Token accepted. Asking {BackendHost()} what it collects...", ImGuiCol.Text);
            return;
        }

        // The neutral states are instructions or live status, so full contrast; only the outcomes
        // carry a color.
        switch (TokenFeedback.For(onboarding.TokenCheck, tokenInput))
        {
            case TokenFeedbackKind.Empty:
                ImGui.TextUnformatted("Paste your token to continue.");
                break;

            case TokenFeedbackKind.Malformed:
                ImGui.TextUnformatted(
                    $"That does not look like a token. They begin with {TokenFormat.Prefix}.");
                break;

            case TokenFeedbackKind.ReadyToVerify:
                ImGui.TextUnformatted("Select Verify to check this token.");
                break;

            case TokenFeedbackKind.Checking:
                DrawWrapped($"Checking with {BackendHost()}...", ImGuiCol.Text);
                break;

            case TokenFeedbackKind.Accepted:
                // Laid out like the rejection's warning, since the two replace each other in place.
                DrawIconedText(
                    FontAwesomeIcon.Check, Widgets.SuccessColor, "Token accepted", Widgets.InlineIconScale);
                ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
                DrawClaimedCharacters();
                break;

            case TokenFeedbackKind.Rejected:
                DrawWarning("That token was not recognized. Generate a new one and paste it here.");
                break;

            case TokenFeedbackKind.Unreachable:
                DrawWarning(
                    $"Could not reach {BackendHost()}. Your token may be fine — try again.");
                break;
        }
    }

    /// <summary>
    /// Lists the characters this account has claimed and marks the ones not yet verified, so the
    /// user can see whether the plugin will have somewhere to put the data before they finish setup.
    /// </summary>
    private void DrawClaimedCharacters()
    {
        // Also catches an explicit JSON null, which gets past `required` (see ApiJson). A throw on
        // the draw path takes the window down.
        if (account?.Characters is not { } characters)
            return;

        if (characters.Count == 0)
        {
            DrawWarning(
                "This account has not claimed any characters yet. Claim and verify your character " +
                $"on {BackendHost()} first, or uploads will be refused.");
            return;
        }

        ImGui.Spacing();

        ImGui.TextDisabled("Claimed characters:");

        // The server picks the count. No honest account has dozens, so this only cuts a hostile
        // backend.
        const int MaxDrawnCharacters = 32;
        var drawn = 0;

        // Whether any drawn character is verified. Counted over drawn rows only, so the ceiling bounds
        // it too.
        var anyVerified = false;

        foreach (var character in characters)
        {
            // A null element is server data gone wrong; skipped rather than thrown on.
            if (character is null)
                continue;

            if (drawn == MaxDrawnCharacters)
            {
                ImGui.TextDisabled($"…and {characters.Count - drawn} more.");
                break;
            }

            drawn++;

            // Not ImGui.Bullet, which reads as a radio button beside the checkboxes.
            DrawIcon(FontAwesomeIcon.User, Brand.Teal);
            ImGui.SameLine();

            // Folded and bounded like every server string: the backend is user-overridable.
            ImGui.TextUnformatted(
                $"{ServerText.SingleLine(character.Name)} ({ServerText.SingleLine(character.World)})");

            // Uploads for an unverified claim are refused. Muted, not red: another listed character
            // may be the one this player syncs.
            if (character.Verified)
            {
                anyVerified = true;
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextDisabled("not verified yet");
            }
        }

        // With no verified claim the first upload is certain to be refused, so this is red.
        if (drawn > 0 && !anyVerified)
        {
            ImGui.Spacing();
            DrawWarning(
                "None of these characters is verified yet. Add the verification code to your " +
                $"Lodestone bio on {BackendHost()}, or uploads will be refused.");
        }

        // The list is the last probe's snapshot, so say that Verify refreshes it. During setup the
        // probe has just run, so the list cannot be stale.
        if (!configuration.Settings.OnboardingComplete)
            return;

        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
        DrawWrapped(
            $"Claimed or verified a character on {BackendHost()}? Press Verify to refresh this list.",
            ImGuiCol.Text);
    }

    /// <summary>
    /// Probes a saved, usable token once per session when the Account panel first opens, so it
    /// shows the token's real state without a Verify press.
    /// </summary>
    /// <remarks>
    /// Unlike a Verify press, this is automatic, so it goes through the upload gate like every other
    /// unprompted request. A failure is not retried: the notice and the Verify button are enough.
    /// </remarks>
    private void TryAutoVerifyToken()
    {
        if (sessionTokenProbeRequested || verifier.InFlight)
            return;

        // A probe already answered this session, most likely the wizard's.
        if (account is not null)
        {
            sessionTokenProbeRequested = true;
            return;
        }

        if (!UploadGate.CanContactServer(configuration.Settings)
            || !TokenFormat.IsWellFormed(tokenInput))
        {
            return;
        }

        sessionTokenProbeRequested = true;
        onboarding.BeginTokenCheck();
        verifier.Start();
    }

    /// <summary>Folds a finished token probe into the wizard's state.</summary>
    /// <remarks>
    /// An accepted token during onboarding fetches <c>/config</c>, so the consent step can list the
    /// server's item groups (see <see cref="SyncManager.RequestOnboardingConfigPoll"/>). Called every
    /// frame, but <see cref="TokenVerifier.TakeResult"/> hands each answer out once.
    /// </remarks>
    private void ConsumeTokenProbe()
    {
        if (verifier.TakeResult() is not { } response)
            return;

        onboarding.RecordTokenCheck(response.Status);
        account = response.Value;

        // Only a recognized token: a rejected one would earn a second 401. After onboarding,
        // SyncManager's gated poll owns /config, so this gate bypass is for the wizard alone.
        if (onboarding.TokenCheck == TokenCheckState.Valid
            && !configuration.Settings.OnboardingComplete)
        {
            syncManager.RequestOnboardingConfigPoll();
        }
    }
}
