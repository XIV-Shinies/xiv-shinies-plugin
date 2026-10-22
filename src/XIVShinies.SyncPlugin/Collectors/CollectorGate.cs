using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides whether a category may be collected and uploaded at all.
/// </summary>
/// <remarks>
/// Onboarding must be complete and four switches on: the plugin's master switch and per-category
/// opt-in, and the <c>/config</c> global kill switch and per-category switch. The global switch folds
/// in the account's "Sync from the plugin" switch. One function gates every category, so no collector
/// can forget a check.
/// </remarks>
public static class CollectorGate
{
    /// <summary>True when this category may be collected right now.</summary>
    /// <param name="categoryKey">The collector's category key.</param>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">
    /// The most recent <c>/config</c> response, or null when it has not been fetched yet.
    /// </param>
    public static bool IsEnabled(string categoryKey, PluginSettings settings, ConfigResponse? remoteConfig)
    {
        // Nothing is collected before the user has seen what gets sent and opted in (a Dalamud rule).
        if (!settings.MasterEnabled || !settings.OnboardingComplete)
            return false;

        if (!settings.IsCategoryEnabled(categoryKey))
            return false;

        // Proceed without /config: the server strips disabled categories and answers 503 while syncing
        // is off anyway, and refusing would strand the plugin whenever /config is unreachable.
        if (remoteConfig is null)
            return true;

        if (!remoteConfig.Enabled)
            return false;

        return remoteConfig.IsCategoryEnabled(categoryKey);
    }

    /// <summary>
    /// Whether a collection may capture a fact into memory, before any upload is in sight.
    /// </summary>
    /// <param name="categoryKey">The collection asking.</param>
    /// <param name="settings">The plugin's own switches.</param>
    /// <remarks>
    /// For a collection whose source is a window the player opens: the read happens when the game fills
    /// that window, not during a pass. A collection switched off in the plugin captures nothing, not even
    /// into memory. The <c>/config</c> switches, the account's "Sync from the plugin" switch among them,
    /// are left out: consent given in the plugin is the answer that counts this early, and
    /// <see cref="IsEnabled"/> and the server both keep what they switch off out of the payload.
    /// </remarks>
    public static bool IsCapturePermitted(string categoryKey, PluginSettings settings) =>
        settings.MasterEnabled
        && settings.OnboardingComplete
        && settings.IsCategoryEnabled(categoryKey);
}
