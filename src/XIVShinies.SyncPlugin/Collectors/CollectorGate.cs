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
    /// <summary>True when this collector's category may be collected right now.</summary>
    /// <param name="collector">
    /// The collector asking. Its key names the switches, and its own
    /// <see cref="ICollector.RequiresServerSupport"/> decides how the server's switch is read.
    /// </param>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">
    /// The most recent <c>/config</c> response, or null when it has not been fetched yet.
    /// </param>
    /// <remarks>
    /// The user's half is <see cref="IsCapturePermitted"/>, the same question a window's capture asks,
    /// and the server's half is <see cref="ServerPermits"/>.
    /// </remarks>
    public static bool IsEnabled(
        ICollector collector, PluginSettings settings, ConfigResponse? remoteConfig) =>
        IsCapturePermitted(collector.CategoryKey, settings) && ServerPermits(collector, remoteConfig);

    /// <summary>Whether the server's half of the gate permits this collector's category.</summary>
    /// <param name="collector">
    /// The collector asking. Its key names the server's switch, and its own
    /// <see cref="ICollector.RequiresServerSupport"/> declaration decides whether a switch the
    /// server never sent counts as on (false) or off (true).
    /// </param>
    /// <param name="remoteConfig">
    /// The most recent <c>/config</c> response, or null when it has not been fetched yet.
    /// </param>
    /// <remarks>
    /// Takes the collector rather than its key, so no caller can ask about a category without its
    /// declaration. Public because the settings window draws the server's answer beside the user's own
    /// choice on every row.
    /// </remarks>
    public static bool ServerPermits(ICollector collector, ConfigResponse? remoteConfig)
    {
        var categoryKey = collector.CategoryKey;
        var requiresServerSupport = collector.RequiresServerSupport;

        // Without /config an ordinary collection proceeds: the server still strips or refuses what it has
        // off, and refusing would strand an offline plugin. One needing server support waits to be named.
        if (remoteConfig is null)
            return !requiresServerSupport;

        if (!remoteConfig.Enabled)
            return false;

        // See ConfigResponse for why the two read a missing key differently.
        return requiresServerSupport
            ? remoteConfig.IsCategoryExplicitlyEnabled(categoryKey)
            : remoteConfig.IsCategoryEnabled(categoryKey);
    }

    /// <summary>
    /// Whether the user's own switches let a collection capture facts, into memory or an upload.
    /// </summary>
    /// <param name="categoryKey">
    /// The collection asking. A key is enough: the server-support declaration plays no part here.
    /// </param>
    /// <param name="settings">The plugin's own switches.</param>
    /// <remarks>
    /// <see cref="IsEnabled"/> asks it every pass, and a collection whose source is a window the player
    /// opens asks it alone, when the game fills that window. A collection switched off in the plugin
    /// captures nothing, not even into memory. The <c>/config</c> switches, the account's "Sync from the
    /// plugin" switch among them, are left out: consent given in the plugin is the answer that counts
    /// this early, and <see cref="IsEnabled"/> and the server both keep what they switch off out of the
    /// payload.
    /// </remarks>
    // Nothing is collected before the user has seen what gets sent and opted in (a Dalamud rule).
    public static bool IsCapturePermitted(string categoryKey, PluginSettings settings) =>
        settings.MasterEnabled
        && settings.OnboardingComplete
        && settings.IsCategoryEnabled(categoryKey);
}
