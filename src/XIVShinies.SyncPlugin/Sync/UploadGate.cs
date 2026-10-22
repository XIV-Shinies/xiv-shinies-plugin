using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides whether the plugin may upload anything at all, right now.
/// </summary>
/// <remarks>
/// The gate that enforces <b>consent</b>, which Dalamud requires before non-essential data is
/// collected. The API client guards the token and the backend but knows nothing about consent.
/// Per-category consent is <see cref="Collectors.CollectorGate"/>'s question.
/// </remarks>
public static class UploadGate
{
    /// <summary>
    /// True when the plugin may make any request at all, including the <c>/config</c> poll.
    /// </summary>
    /// <remarks>
    /// Consent plus a credential, and nothing else. It ignores the server's kill switch, because the
    /// poll that reads the switch has to run, or the plugin could never learn it flipped back.
    /// </remarks>
    public static bool CanContactServer(PluginSettings settings)
    {
        // The user's own switches. A fresh install talks to nobody: both default to false.
        if (!settings.MasterEnabled || !settings.OnboardingComplete)
            return false;

        // Checking the token's shape locally avoids a request that could only earn a 401.
        return settings.HasUsableToken();
    }

    /// <summary>True when an upload may be attempted.</summary>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if it has not been fetched.</param>
    public static bool CanUpload(PluginSettings settings, ConfigResponse? remoteConfig)
    {
        if (!CanContactServer(settings))
            return false;

        // An unfetched config forbids nothing: refusing would strand the plugin whenever /config is
        // unreachable, and the server enforces its own switches anyway.
        return remoteConfig is null || remoteConfig.Enabled;
    }
}
