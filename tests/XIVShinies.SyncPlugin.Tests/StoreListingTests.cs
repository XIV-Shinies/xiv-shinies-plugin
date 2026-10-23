using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests;

// The installer listing: the manifest's fields, which repo.json repeats for the custom repository.
// The build copies both beside the test assembly (see the test project), so these read what ships.
public class StoreListingTests
{
    // The installer shows the whole description under the tagline, so a fixed ceiling makes a new
    // sentence displace an old one rather than pile on.
    private const int DescriptionMaxLength = 400;

    [Fact]
    public void The_description_fits_its_length_cap()
    {
        var description = ManifestText("Description");

        Assert.False(string.IsNullOrWhiteSpace(description), "The manifest Description is empty.");
        Assert.True(
            description.Length <= DescriptionMaxLength,
            $"The manifest Description is {description.Length} characters; " +
            $"the cap is {DescriptionMaxLength}.");
    }

    // The two files are edited separately, so this catches a listing changed in only one. It walks the
    // manifest's fields, so repo.json's build-stamped fields and download links are left alone.
    [Fact]
    public void Repo_json_repeats_every_manifest_field()
    {
        var manifest = Manifest().AsObject();
        var entry = RepoEntry().AsObject();

        // DeepEquals compares whole values, so a list must hold the same items in the same order.
        foreach (var (field, value) in manifest)
        {
            Assert.True(
                JsonNode.DeepEquals(value, entry[field]),
                $"repo.json's {field} differs from the manifest's.");
        }
    }

    // The three pieces as the settings masthead draws them (see PluginMeta.TaglineLead).
    [Fact]
    public void The_masthead_quotes_the_manifest_tagline()
    {
        Assert.Equal(
            $"{PluginMeta.TaglineLead} {PluginMeta.TaglineSite} {PluginMeta.TaglineTail}",
            ManifestText("Punchline"));
    }

    // DisplayName titles the window and the masthead, and SourceUrl is what the Source code button opens.
    [Fact]
    public void PluginMeta_quotes_the_manifest_name_and_source()
    {
        Assert.Equal(PluginMeta.DisplayName, ManifestText("Name"));
        Assert.Equal(PluginMeta.SourceUrl, ManifestText("RepoUrl"));
    }

    private static JsonNode Manifest() => Load("XIVShinies.SyncPlugin.json");

    // repo.json is an array with one entry per plugin the repository serves, and it serves one. A null
    // from Parse or from [0] makes the next call throw, which fails the test as a missing entry does.
    private static JsonNode RepoEntry() => Load("repo.json")[0]!;

    private static JsonNode Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "StoreListing", fileName);
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    // A missing field, or one that is not text, throws here and fails the test.
    private static string ManifestText(string field) => Manifest()[field]!.GetValue<string>();
}
