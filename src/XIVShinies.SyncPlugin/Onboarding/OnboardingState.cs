using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Onboarding;

/// <summary>The steps of the first-run wizard, in order.</summary>
// Advance and Back add or subtract one, so the declaration order is the wizard's order.
public enum OnboardingStep
{
    /// <summary>What this plugin does, and what it will send.</summary>
    Welcome,

    /// <summary>Paste an API token and prove it works.</summary>
    LinkAccount,

    /// <summary>Choose which collections to upload. Everything starts off.</summary>
    ChooseCategories,

    /// <summary>Consent is recorded and the plugin begins syncing.</summary>
    Done,
}

/// <summary>
/// The first-run wizard, as pure logic: which step, and whether the user may move on.
/// </summary>
/// <remarks>
/// This enforces Dalamud's explicit opt-in: nothing uploads until <see cref="Finish"/> writes the
/// user's consent, and it refuses before the last step. The window draws what this says and decides
/// nothing; it makes the token probe and reports back through <see cref="RecordTokenCheck"/>.
/// </remarks>
public sealed class OnboardingState
{
    /// <summary>The step being shown.</summary>
    public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;

    /// <summary>What is known about the pasted token.</summary>
    public TokenCheckState TokenCheck { get; private set; } = TokenCheckState.NotChecked;

    /// <summary>
    /// True while the wizard waits for the config fetched after a verified token.
    /// </summary>
    /// <remarks>
    /// The consent step draws its per-group checkboxes from that config, so the account step holds until
    /// it arrives, and no group appears under a category already ticked.
    /// </remarks>
    public bool AwaitingConfig { get; private set; }

    /// <summary>True once the wizard has reached its final step.</summary>
    public bool IsComplete => Step == OnboardingStep.Done;

    /// <summary>Whether the user may move forward from the current step.</summary>
    /// <remarks>
    /// Only the account step has preconditions: a verified token and the config fetched after it (see
    /// <see cref="AwaitingConfig"/>). An unverified token would finish a wizard that cannot sync,
    /// failing later as an opaque 401.
    /// </remarks>
    public bool CanAdvance => Step switch
    {
        OnboardingStep.LinkAccount => TokenCheck == TokenCheckState.Valid && !AwaitingConfig,

        // Every category starts off, so continuing with none ticked is a coherent choice; forcing a
        // selection would make consent a toll.
        OnboardingStep.ChooseCategories => true,

        OnboardingStep.Welcome => true,
        _ => false,
    };

    /// <summary>Whether there is an earlier step to return to.</summary>
    public bool CanGoBack => Step > OnboardingStep.Welcome && Step != OnboardingStep.Done;

    /// <summary>Moves to the next step, if the current one allows it.</summary>
    /// <remarks>Does nothing when it may not; the button that calls this is drawn disabled.</remarks>
    public void Advance()
    {
        if (CanAdvance)
            Step++;
    }

    /// <summary>Returns to the previous step.</summary>
    /// <remarks>A verified token survives the trip, so stepping back does not re-probe it.</remarks>
    public void Back()
    {
        if (CanGoBack)
            Step--;
    }

    /// <summary>Records that a token probe is in flight, so the UI can show it and block advancing.</summary>
    public void BeginTokenCheck() => TokenCheck = TokenCheckState.Checking;

    /// <summary>Records whether the server's config is still outstanding.</summary>
    public void NotifyAwaitingConfig(bool awaiting) => AwaitingConfig = awaiting;

    /// <summary>Records what the server said about the token.</summary>
    public void RecordTokenCheck(ApiStatus status) => TokenCheck = Onboarding.TokenCheck.FromApiStatus(status);

    /// <summary>
    /// Forgets any previous verification, because the token it verified is gone.
    /// </summary>
    /// <remarks>
    /// Otherwise a user could verify one token, paste another, and advance on the old answer.
    /// </remarks>
    public void NotifyTokenEdited() => TokenCheck = TokenCheckState.NotChecked;

    /// <summary>
    /// Writes the user's consent. This is the moment the plugin is allowed to upload anything.
    /// </summary>
    /// <param name="settings">The settings to record consent into. The caller saves them.</param>
    /// <remarks>
    /// It enables no categories: the user ticks those on the previous step. Opting someone in here,
    /// even to a sensible default, would be consent by omission, which the rule forbids.
    /// </remarks>
    public void Finish(PluginSettings settings)
    {
        // An interlock: this flips the plugin on, so it must be impossible from mid-wizard.
        if (!IsComplete)
            return;

        settings.OnboardingComplete = true;
        settings.MasterEnabled = true;
    }
}
