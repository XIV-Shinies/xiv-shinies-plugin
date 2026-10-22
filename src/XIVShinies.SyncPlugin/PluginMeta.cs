namespace XIVShinies.SyncPlugin;

/// <summary>
/// Plugin constants and helpers, free of Dalamud and game types.
/// </summary>
public static class PluginMeta
{
    /// <summary>The primary in-game slash command that opens the plugin UI.</summary>
    public const string CommandName = "/shinies";

    /// <summary>
    /// A longer alias for <see cref="CommandName"/>, hidden from <c>/xlhelp</c> so the help list
    /// shows one entry.
    /// </summary>
    public const string CommandAlias = "/xivshinies";

    /// <summary>Human-facing plugin name (mirrors the manifest <c>Name</c> field).</summary>
    public const string DisplayName = "XIV Shinies Sync";

    /// <summary>Where the plugin's source lives (mirrors the manifest <c>RepoUrl</c> field).</summary>
    public const string SourceUrl = "https://github.com/XIV-Shinies/xiv-shinies-plugin";

    /// <summary>The community Discord invite (the same one the website links to).</summary>
    public const string DiscordUrl = "https://discord.gg/UuNe5BwAGG";

    /// <summary>The maintainer's sponsor page (the same one the website links to).</summary>
    public const string SponsorUrl = "https://www.patreon.com/c/noranda/";

    /// <summary>
    /// Builds the <c>User-Agent</c> header the API client sends on every request, in the format
    /// <c>XIVShinies.SyncPlugin/&lt;version&gt;</c>.
    /// </summary>
    public static string UserAgent(string version) => $"XIVShinies.SyncPlugin/{version}";
}
