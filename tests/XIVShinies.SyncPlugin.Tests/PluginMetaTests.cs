using Xunit;
using XIVShinies.SyncPlugin;

namespace XIVShinies.SyncPlugin.Tests;

// The plugin's fixed names and the User-Agent format the server contract requires.
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
}
