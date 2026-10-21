using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides whether a category may be collected and uploaded at all.
/// </summary>
/// <remarks>
/// Four switches must all be on, and any one of them off wins. Two are the plugin's own (the
/// master switch and the per-category opt-in) and two come from the server's <c>/config</c> (the
/// global kill switch, which folds in the account's "Sync from the plugin" switch, and the
/// per-category switch). Keeping this in one pure function — rather than a method on each
/// collector — means every category is gated identically and no collector can forget a check.
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
        // The plugin's own switches come first. Nothing is ever collected before the user has been
        // shown what gets sent and has explicitly opted in — a Dalamud compliance rule.
        if (!settings.MasterEnabled || !settings.OnboardingComplete)
            return false;

        if (!settings.IsCategoryEnabled(categoryKey))
            return false;

        // Without a fetched config we cannot know the server's switches. Proceeding is safe: the
        // server enforces them regardless, stripping disabled categories and answering 503 when
        // syncing is switched off. Refusing to collect would instead strand the plugin whenever
        // /config is unreachable.
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
    /// <para>
    /// Most collections read the game when a pass runs, so <see cref="IsEnabled"/> is the only gate
    /// they need. A collection whose source is a window the player opens is different: the read
    /// happens on the game's schedule rather than the pass's, so something has to decide whether to
    /// take it long before a pass would ask. This is that decision, and it holds the plugin's own
    /// switches to the same meaning — switched off collects nothing, not even into memory.
    /// </para>
    /// <para>
    /// The <c>/config</c> switches are deliberately absent, the account's "Sync from the plugin"
    /// switch among them. Whatever they switch off never reaches a payload anyway —
    /// <see cref="IsEnabled"/> refuses to collect it, and the server strips a disabled category on
    /// arrival or answers 503 while syncing is off.
    /// Consent given in the plugin is the only answer that belongs in a decision made this early.
    /// </para>
    /// </remarks>
    public static bool IsCapturePermitted(string categoryKey, PluginSettings settings) =>
        settings.MasterEnabled
        && settings.OnboardingComplete
        && settings.IsCategoryEnabled(categoryKey);
}
