using Xunit;
using XIVShinies.SyncPlugin;

namespace XIVShinies.SyncPlugin.Tests;

// The plugin's fixed names, its built-in mascot, and the User-Agent format the server contract requires.
public class PluginMetaTests
{
    [Fact]
    public void CommandName_is_the_slash_command()
    {
        Assert.Equal("/shinies", PluginMeta.CommandName);
    }

    [Fact]
    public void CommandAlias_is_the_longer_form()
    {
        Assert.Equal("/xivshinies", PluginMeta.CommandAlias);
    }

    [Fact]
    public void DisplayName_is_the_plugin_name()
    {
        // Shown in the window title and /xlhelp, and must match the manifest's "Name".
        Assert.Equal("XIV Shinies Sync", PluginMeta.DisplayName);
    }

    [Fact]
    public void UserAgent_embeds_the_version_in_the_contract_format()
    {
        Assert.Equal("XIVShinies.SyncPlugin/1.2.3", PluginMeta.UserAgent("1.2.3"));
    }

    [Fact]
    public void The_mascot_is_built_into_the_plugin_as_a_png()
    {
        using var stream = typeof(PluginMeta).Assembly.GetManifestResourceStream(PluginMeta.MascotResource);

        Assert.NotNull(stream);
        var signature = new byte[8];
        stream.ReadExactly(signature);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, signature);
    }
}
