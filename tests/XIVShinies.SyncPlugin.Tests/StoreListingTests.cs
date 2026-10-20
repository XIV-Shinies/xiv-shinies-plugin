using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests;

// The plugin's installer listing: the plugin manifest's fields (name, tagline, description, tags,
// gallery images…), which repo.json repeats for players who install from the custom repository.
// The build copies both files into a StoreListing folder beside the test assembly (see the linked
// `<None>` items in the test project), so these tests read the exact text that ships. Each `[Fact]`
// is one test, like `it(...)` in Jest.
public class StoreListingTests
{
    // The installer shows the whole description under the tagline, so every sentence in it is one
    // more a player reads before the install button. A fixed ceiling makes a new sentence displace
    // an old one rather than pile on.
    private const int DescriptionMaxLength = 400;

    [Fact]
    public void The_description_fits_its_length_cap()
    {
        var description = ManifestText("Description");

        // The second argument is the message xUnit prints when the check fails, so the failure
        // says what is wrong and by how much.
        Assert.False(string.IsNullOrWhiteSpace(description), "The manifest Description is empty.");
        Assert.True(
            description.Length <= DescriptionMaxLength,
            $"The manifest Description is {description.Length} characters; " +
            $"the cap is {DescriptionMaxLength}.");
    }

    // The two files are edited separately, so this catches a listing changed in one and not the
    // other. It walks the manifest's own fields, so whatever the manifest carries is checked. The
    // fields only repo.json has are the ones the build stamps into the packaged manifest (the
    // internal name, version and API level) and the download links; they are left alone.
    [Fact]
    public void Repo_json_repeats_every_manifest_field()
    {
        var manifest = Manifest().AsObject();
        var entry = RepoEntry().AsObject();

        // `var (field, value)` unpacks each name/value pair, like
        // `for (const [field, value] of Object.entries(manifest))` in TypeScript. DeepEquals
        // compares whole values, so a list must hold the same items in the same order.
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

    // The plugin's name (its window title and masthead heading) and the address the masthead's
    // Source code button opens are the manifest's Name and RepoUrl.
    [Fact]
    public void PluginMeta_quotes_the_manifest_name_and_source()
    {
        Assert.Equal(PluginMeta.DisplayName, ManifestText("Name"));
        Assert.Equal(PluginMeta.SourceUrl, ManifestText("RepoUrl"));
    }

    // The listing in each file: the manifest is a single JSON object, and repo.json is an array with
    // one entry per plugin the repository serves, of which it serves one. A JsonNode is a parsed JSON
    // value that can be indexed by name or position, much like the object JSON.parse returns. The
    // `!` tells the compiler the value is not null and adds no runtime check. Parse returns null only
    // for the text `null`, and `[0]` only when repo.json's first entry is `null`; either way the next
    // call on it throws and fails the test, as indexing past the end of the array does.
    private static JsonNode Manifest() => Load("XIVShinies.SyncPlugin.json");

    private static JsonNode RepoEntry() => Load("repo.json")[0]!;

    private static JsonNode Load(string fileName)
    {
        // AppContext.BaseDirectory is the folder the test assembly runs from.
        var path = Path.Combine(AppContext.BaseDirectory, "StoreListing", fileName);
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    // One text field of the manifest. A missing field reads as null and fails the test at GetValue,
    // which also throws when the value is not text.
    private static string ManifestText(string field) => Manifest()[field]!.GetValue<string>();
}
