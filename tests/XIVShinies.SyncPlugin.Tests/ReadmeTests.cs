using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using XIVShinies.SyncPlugin.Api;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests;

// The README's header repeats the masthead's site, Discord and sponsor links. The two are edited
// separately, so this catches a link changed in only one. The test project copies the README in.
public class ReadmeTests
{
    [Fact]
    public void The_readme_header_links_where_the_masthead_does()
    {
        var readme = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Readme", "README.md"));

        // The header is the centered block the README opens with.
        var headerEnd = readme.IndexOf("</div>", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0, "The README has no header block.");

        // Compared as sets, so a link the header repeats counts once and an edit to one copy of it
        // shows as an extra link.
        var headerLinks = Regex.Matches(readme[..headerEnd], "href=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .Order();
        var mastheadLinks = new[] { BackendUrl.Default, PluginMeta.DiscordUrl, PluginMeta.SponsorUrl }
            .Order();

        Assert.Equal(mastheadLinks, headerLinks);
    }
}
