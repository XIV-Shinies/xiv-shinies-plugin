using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides whether a category may be collected and uploaded at all.
/// </summary>
/// <remarks>
/// <para>
/// Four switches must all be on, and any one of them off wins. Two belong to the user (the master
/// switch and their per-category opt-in), and neither counts until the user has completed
/// onboarding; two belong to the server (the global kill switch and its per-category switch).
/// Keeping this in one place — rather than a method on each collector — means every category is
/// gated identically and no collector can forget a check.
/// </para>
/// <para>
/// The server's per-category switch is read one of two ways, chosen by the collection's own
/// <see cref="CategoryInfo.RequiresServerSupport"/> declaration. An ordinary collection treats a
/// switch the server never sent as on; a collection that needs the server to know it treats the
/// same silence as off. <see cref="ServerPermits"/> holds both readings side by side.
/// </para>
/// <para>
/// <see cref="IsEnabled"/> and <see cref="ServerPermits"/> take the collector itself rather than its
/// key, so the declaration travels with the question and no caller can ask the server's half about
/// a category while leaving its declaration behind. <see cref="IsCapturePermitted"/> takes a key
/// because it asks only the user's half, where the declaration plays no part.
/// </para>
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
    /// The two halves of the gate, each asked once: the user's switches through
    /// <see cref="IsCapturePermitted"/>, and the server's through <see cref="ServerPermits"/>. The
    /// user's half is the same question whether a pass is about to read the category or a window
    /// is about to hand over a fact, so it is written once and both callers share it.
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
    /// Public, rather than folded into <see cref="IsEnabled"/>, because the settings window asks
    /// exactly this question for every row: it draws the user's own choice beside the server's
    /// answer, so it needs the server's half on its own.
    /// </remarks>
    public static bool ServerPermits(ICollector collector, ConfigResponse? remoteConfig)
    {
        var categoryKey = collector.CategoryKey;
        var requiresServerSupport = collector.RequiresServerSupport;

        // Without a fetched config we cannot know the server's switches, and the two kinds of
        // collection answer that differently.
        //
        // An ordinary one proceeds. That is safe: the server enforces its switches regardless,
        // stripping disabled categories and answering 503 when its global switch is off. Refusing to
        // collect would instead strand the plugin whenever /config is unreachable.
        //
        // One that needs server support waits. The only thing that would justify reading and
        // sending it is the server saying it knows the collection, and with no config nothing says
        // so — sending it anyway is exactly the cost the declaration exists to avoid.
        if (remoteConfig is null)
            return !requiresServerSupport;

        // The global kill switch outranks anything the per-category map says.
        if (!remoteConfig.Enabled)
            return false;

        // The per-category switch, read the way the collection asked: IsCategoryExplicitlyEnabled
        // demands the key be present and on; IsCategoryEnabled lets a missing key through.
        // ConfigResponse holds the reasoning for each. `condition ? a : b` is the same ternary as in
        // TypeScript.
        return requiresServerSupport
            ? remoteConfig.IsCategoryExplicitlyEnabled(categoryKey)
            : remoteConfig.IsCategoryEnabled(categoryKey);
    }

    /// <summary>
    /// Whether the user's own switches let a collection capture facts at all, into memory as much
    /// as into an upload.
    /// </summary>
    /// <param name="categoryKey">The collection asking.</param>
    /// <param name="settings">The user's own switches.</param>
    /// <remarks>
    /// <para>
    /// This is the user's half of the gate. <see cref="IsEnabled"/> asks it for every pass, beside
    /// the server's half. A collection whose source is a window the player opens also asks it on
    /// its own: the read happens on the game's schedule rather than the pass's, so something has to
    /// decide whether to take it long before a pass would ask. Both hold the user's switches to the
    /// same meaning — switched off collects nothing, not even into memory.
    /// </para>
    /// <para>
    /// The server's switches are deliberately absent. A category the server has turned off never
    /// reaches a payload anyway — <see cref="IsEnabled"/> refuses to collect it, and the server
    /// strips it on arrival regardless. Consent is the user's answer, and only the user's answer
    /// belongs in a decision made this early.
    /// </para>
    /// </remarks>
    // The user's master switch and per-category opt-in must both be on, and onboarding must be
    // complete. Nothing is ever collected before the user has been shown what gets sent and has
    // explicitly opted in (OnboardingComplete) — a Dalamud compliance rule.
    public static bool IsCapturePermitted(string categoryKey, PluginSettings settings) =>
        settings.MasterEnabled
        && settings.OnboardingComplete
        && settings.IsCategoryEnabled(categoryKey);
}
