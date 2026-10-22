using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// ManifestCatalog is the one place that knows a server manifest exists. These tests guard what a
// table cannot enforce itself: every descriptor names a real category, and each bounds its read at
// the ceiling.
public class ManifestCatalogTests
{
    private static ConfigResponse Config(
        IReadOnlyList<uint>? items = null, IReadOnlyList<uint>? questSequences = null) =>
        new()
        {
            Categories = new Dictionary<string, bool>(),
            Enabled = true,
            Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
            ItemManifest = items ?? [],
            ManifestVersion = "abc123",
            QuestSequenceManifest = questSequences,
        };

    private static IReadOnlyList<uint> Ids(int count) =>
        Enumerable.Range(1, count).Select(i => (uint)i).ToList();

    // Every key a collector could pass to ManifestFor.
    private static readonly HashSet<string> KnownCategoryKeys =
        CategoryKeyReflection.All().ToHashSet();

    // A descriptor keyed to no real category is unreachable: ManifestFor would answer empty for the
    // real one, and nothing would read the manifest.
    [Fact]
    public void Every_descriptor_names_a_real_category()
    {
        Assert.NotEmpty(ManifestCatalog.All);
        Assert.All(ManifestCatalog.All, d => Assert.Contains(d.CategoryKey, KnownCategoryKeys));

        // Duplicate keys would make For() and the truncation set silently ambiguous.
        Assert.Equal(
            ManifestCatalog.All.Count,
            ManifestCatalog.All.Select(d => d.CategoryKey).Distinct().Count());
    }

    // ManifestFor answers empty for anything unregistered, so without this every empty-manifest
    // assertion elsewhere would still pass with a descriptor dropped.
    [Fact]
    public void The_manifest_driven_categories_are_registered()
    {
        Assert.NotNull(ManifestCatalog.For(CategoryKeys.Items));
        Assert.NotNull(ManifestCatalog.For(CategoryKeys.QuestSequences));
    }

    // Walks the table, so a descriptor added later is covered too.
    [Fact]
    public void Every_descriptor_bounds_its_read_at_the_ceiling()
    {
        var oversized = Ids(CollectContext.MaxManifestItems + 1);
        var context = new CollectContext
        {
            RemoteConfig = Config(items: oversized, questSequences: oversized),
        };

        Assert.All(ManifestCatalog.All, descriptor =>
        {
            Assert.True(descriptor.Truncated(context));
            Assert.Equal(CollectContext.MaxManifestItems, descriptor.Read(context).Count);
        });
    }

    // Most collectors read the game directly, so asking for a missing manifest must answer empty,
    // not throw.
    [Fact]
    public void A_category_with_no_manifest_reads_as_empty()
    {
        var context = new CollectContext { RemoteConfig = Config(items: [1, 2, 3]) };

        Assert.Null(ManifestCatalog.For(CategoryKeys.Quests));
        Assert.Empty(context.ManifestFor(CategoryKeys.Quests));
        Assert.Null(ManifestCatalog.TruncationWarning(CategoryKeys.Quests));
    }

    [Fact]
    public void An_unknown_category_key_reads_as_empty()
    {
        var context = new CollectContext { RemoteConfig = Config(items: [1, 2, 3]) };

        Assert.Empty(context.ManifestFor("notACategory"));
        Assert.Null(ManifestCatalog.TruncationWarning("notACategory"));
    }

    [Fact]
    public void A_manifest_within_the_ceiling_truncates_nothing()
    {
        var context = new CollectContext
        {
            RemoteConfig = Config(items: Ids(10), questSequences: Ids(10)),
        };

        Assert.Empty(context.TruncatedManifests);
    }

    [Fact]
    public void Only_the_oversized_manifest_is_reported_as_truncated()
    {
        var context = new CollectContext
        {
            RemoteConfig = Config(
                items: Ids(CollectContext.MaxManifestItems + 1),
                questSequences: Ids(10)),
        };

        Assert.Contains(CategoryKeys.Items, context.TruncatedManifests);
        Assert.DoesNotContain(CategoryKeys.QuestSequences, context.TruncatedManifests);
    }

    [Fact]
    public void Both_manifests_can_be_reported_truncated_at_once()
    {
        var context = new CollectContext
        {
            RemoteConfig = Config(
                items: Ids(CollectContext.MaxManifestItems + 1),
                questSequences: Ids(CollectContext.MaxManifestItems + 1)),
        };

        Assert.Equal(
            new HashSet<string> { CategoryKeys.Items, CategoryKeys.QuestSequences },
            context.TruncatedManifests);
    }

    // The orchestrator knows a manifest only by its key, so the catalog supplies wording specific
    // enough to act on.
    [Theory]
    [InlineData(CategoryKeys.Items, "item manifest", "scanned")]
    [InlineData(CategoryKeys.QuestSequences, "quest-sequence manifest", "looked up")]
    public void A_truncation_warning_names_the_manifest_and_what_was_skipped(
        string categoryKey, string noun, string verb)
    {
        var warning = ManifestCatalog.TruncationWarning(categoryKey);

        Assert.NotNull(warning);
        Assert.Contains(noun, warning);
        Assert.Contains(verb, warning);
        Assert.Contains(CollectContext.MaxManifestItems.ToString(), warning);
    }
}
