using System;
using Dalamud.Configuration;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The object Dalamud serializes to disk and hands back on the next launch.
/// </summary>
/// <remarks>
/// A thin shell: implementing <c>IPluginConfiguration</c> ties this type to <c>Dalamud.dll</c>, which
/// exists only inside the running game. The settings and their rules live in
/// <see cref="PluginSettings"/>, which touches no Dalamud types.
/// </remarks>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>The config schema version a freshly written config carries.</summary>
    /// <remarks>
    /// A loaded config with a lower number is from an older install and runs through
    /// <see cref="PluginSettings.ApplyUpgradeMigrations"/> before anything reads it.
    /// </remarks>
    public const int CurrentVersion = 3;

    // IPluginConfiguration requires this, for migrating saved configs across schema changes.
    // PluginSettings.ApplyUpgradeMigrations says what each version added.
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Everything the user can configure. Persisted as a nested object.</summary>
    public PluginSettings Settings { get; set; } = new();

    /// <summary>Persists the settings. Call it after changing any setting.</summary>
    /// <remarks>
    /// Serializing walks every collection in <see cref="PluginSettings"/>, so it runs under the same
    /// lock as the writers: a save on the draw thread could otherwise walk a list the framework thread
    /// is adding to, which throws.
    /// </remarks>
    public void Save() => Settings.RunLocked(() => Plugin.PluginInterface.SavePluginConfig(this));
}
