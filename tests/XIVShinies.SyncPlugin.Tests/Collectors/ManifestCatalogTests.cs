using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The catalog is the single place that knows a server manifest exists: what its ids are, whether
// the server sent more than the ceiling allows, and the words to warn in. These tests guard the
// two things a table cannot enforce about itself — that every descriptor names a category a
// collector could actually pass, and that each one bounds its own read at the ceiling.
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

    // Every key a collector could pass to ManifestFor, as a set for lookup.
    private static readonly HashSet<string> KnownCategoryKeys =
        CategoryKeyReflection.All().ToHashSet();

    // A descriptor keyed to something no collector can name is unreachable: ManifestFor would
    // answer empty for the real category and nothing would ever read the manifest.
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

    // Without this, every "empty manifest" assertion elsewhere would still pass if a descriptor
    // were dropped from the table — ManifestFor answers empty for anything unregistered.
    [Fact]
    public void The_manifest_driven_categories_are_registered()
    {
        Assert.NotNull(ManifestCatalog.For(CategoryKeys.Items));
        Assert.NotNull(ManifestCatalog.For(CategoryKeys.QuestSequences));
    }

    // Walks the table rather than naming manifests, so a descriptor added later inherits the
    // ceiling coverage instead of quietly having none.
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

    // A category with no manifest is the ordinary case — most collectors read the game directly —
    // so asking for one must answer "nothing to look up" rather than throwing.
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

    // Each manifest is measured against the ceiling on its own, so one oversized list never
    // implicates another.
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

    // The orchestrator holds the logger but knows nothing about a manifest beyond its key, so the
    // catalog has to hand back wording specific enough to act on.
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
