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

    /// <summary>The manifest <c>Punchline</c>'s words before the site's name.</summary>
    /// <remarks>
    /// The settings masthead draws the tagline in three pieces so it can color the name. This,
    /// <see cref="TaglineSite"/> and <see cref="TaglineTail"/>, joined by single spaces, are the
    /// punchline word for word.
    /// </remarks>
    public const string TaglineLead = "Your collections, on";

    /// <summary>The site's name within the tagline, with its comma (see <see cref="TaglineLead"/>).</summary>
    public const string TaglineSite = "xiv-shinies.com,";

    /// <summary>The tagline's words after the site's name (see <see cref="TaglineLead"/>).</summary>
    public const string TaglineTail = "as you earn them.";

    /// <summary>The community Discord invite (the same one the website and the README link to).</summary>
    public const string DiscordUrl = "https://discord.gg/trPRw5KyDt";

    /// <summary>The maintainer's sponsor page (the same one the website and the README link to).</summary>
    public const string SponsorUrl = "https://www.patreon.com/c/noranda/";

    /// <summary>The masthead mascot's name among the plugin assembly's built-in resources.</summary>
    /// <remarks>
    /// Built in because the release zip carries only the DLL and its manifests, never the images
    /// folder a dev plugin has beside the DLL.
    /// </remarks>
    public const string MascotResource = "XIVShinies.SyncPlugin.Mascot.png";

    /// <summary>
    /// Builds the <c>User-Agent</c> header the API client sends on every request, in the format
    /// <c>XIVShinies.SyncPlugin/&lt;version&gt;</c>.
    /// </summary>
    public static string UserAgent(string version) => $"XIVShinies.SyncPlugin/{version}";
}
