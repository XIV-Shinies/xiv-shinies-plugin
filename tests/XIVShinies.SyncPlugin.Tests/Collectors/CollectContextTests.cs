using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The item manifest comes from the server and is read on the framework thread, where the item
// collector scans several containers per id. Every consumer reads it through CollectContext, so the
// size bound lives there.
public class CollectContextTests
{
    private static ConfigResponse ConfigWith(IReadOnlyList<uint> manifest) => new()
    {
        Categories = new Dictionary<string, bool>(),
        Enabled = true,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = manifest,
        ManifestVersion = "abc123",
    };

    private static ConfigResponse ConfigWithGroups(
        params (string key, uint[] ids, bool legacy)[] groups)
    {
        var itemManifestGroups = groups.Select(g => new ItemManifestGroup
        {
            Key = g.key,
            Label = $"Label for {g.key}",
            Ids = g.ids,
            Legacy = g.legacy,
        }).ToList();

        return new()
        {
            Categories = new Dictionary<string, bool>(),
            Enabled = true,
            Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
            ItemManifest = Array.Empty<uint>(),
            ItemManifestGroups = itemManifestGroups,
            ManifestVersion = "abc123",
        };
    }

    [Fact]
    public void The_manifest_is_empty_when_no_config_has_been_fetched()
    {
        var context = new CollectContext { RemoteConfig = null };

        Assert.Empty(context.ManifestFor(CategoryKeys.Items));
    }

    // An older server that does not send the field is a supported peer; like no config, it reads as
    // "omit nothing".
    [Fact]
    public void The_omit_when_unseen_set_is_empty_without_a_config_or_the_field()
    {
        Assert.Empty(new CollectContext { RemoteConfig = null }.ItemOmitWhenUnseenIds);
        Assert.Empty(
            new CollectContext { RemoteConfig = ConfigWith(new uint[] { 42 }) }
                .ItemOmitWhenUnseenIds);
    }

    [Fact]
    public void The_omit_when_unseen_ids_surface_as_a_probeable_set()
    {
        var config = ConfigWith(new uint[] { 45043, 42 }) with
        {
            ItemOmitWhenUnseenIds = new uint[] { 45043, 45044 },
        };

        var set = new CollectContext { RemoteConfig = config }.ItemOmitWhenUnseenIds;

        Assert.Contains(45043u, set);
        Assert.Contains(45044u, set);
        Assert.DoesNotContain(42u, set);
    }

    [Fact]
    public void A_sane_manifest_passes_through_untouched()
    {
        var manifest = new uint[] { 10, 20, 30 };
        var context = new CollectContext { RemoteConfig = ConfigWith(manifest) };

        Assert.Same(manifest, context.ManifestFor(CategoryKeys.Items));
    }

    // A hostile or broken backend could send millions of ids, each costing several container scans on
    // the framework thread. The cap ignores the tail instead of freezing the game.
    [Fact]
    public void An_oversized_manifest_is_truncated_to_the_cap()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 500)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext { RemoteConfig = ConfigWith(oversized) };

        var bounded = context.ManifestFor(CategoryKeys.Items);

        Assert.Equal(CollectContext.MaxManifestItems, bounded.Count);
        Assert.Equal(1u, bounded[0]);
        Assert.Equal((uint)CollectContext.MaxManifestItems, bounded[^1]);
    }

    [Fact]
    public void The_manifest_unions_only_enabled_groups()
    {
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("relic-proofs", new uint[] { 1, 2 }, true),
                ("relic-materials", new uint[] { 2, 3 }, false)),  // 2 overlaps on purpose
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs", "relic-materials" },
        };

        Assert.Equal(new uint[] { 1, 2, 3 }, context.ManifestFor(CategoryKeys.Items));  // deduped, first-seen order
    }

    [Fact]
    public void The_manifest_excludes_disabled_groups()
    {
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("relic-proofs", new uint[] { 1, 2 }, true),
                ("relic-materials", new uint[] { 3 }, false)),
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs" },
        };

        Assert.Equal(new uint[] { 1, 2 }, context.ManifestFor(CategoryKeys.Items));
    }

    [Fact]
    public void The_manifest_is_empty_when_no_group_is_enabled()
    {
        // Once groups exist, consent is per group.
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(("relic-proofs", new uint[] { 1 }, true)),
            EnabledItemGroupKeys = new HashSet<string>(),
        };

        Assert.Empty(context.ManifestFor(CategoryKeys.Items));
    }

    [Fact]
    public void The_manifest_falls_back_to_the_flat_manifest_without_groups()
    {
        // Without groups, only the category toggle gates the flat manifest.
        var context = new CollectContext
        {
            RemoteConfig = ConfigWith(new uint[] { 1, 2 }),
            EnabledItemGroupKeys = new HashSet<string>(),
        };

        Assert.Equal(new uint[] { 1, 2 }, context.ManifestFor(CategoryKeys.Items));
    }

    // An empty groups array means no groups, so the flat manifest is what the server wants scanned.
    // Reading it as "groups, none enabled" would scan nothing.
    [Fact]
    public void An_empty_group_list_falls_back_to_the_flat_manifest_too()
    {
        var config = ConfigWith(new uint[] { 1, 2 }) with
        {
            ItemManifestGroups = Array.Empty<ItemManifestGroup>(),
        };

        var context = new CollectContext
        {
            RemoteConfig = config,
            EnabledItemGroupKeys = new HashSet<string>(),
        };

        Assert.Equal(new uint[] { 1, 2 }, context.ManifestFor(CategoryKeys.Items));
    }

    // The grouped path has its own cap, since a single group could hold millions of ids.
    [Fact]
    public void An_oversized_group_union_is_truncated_to_the_cap()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(("relic-proofs", oversized, true)),
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs" },
        };

        var bounded = context.ManifestFor(CategoryKeys.Items);

        Assert.Equal(CollectContext.MaxManifestItems, bounded.Count);
        Assert.Equal(1u, bounded[0]);
        // The cut lands exactly at the cap, not one before or after it.
        Assert.Equal(oversized[CollectContext.MaxManifestItems - 1], bounded[^1]);
    }

    // --- Truncation reporting ------------------------------------------------------------------
    // The cap clips silently where the manifest is read; this set is how the orchestrator learns of a
    // clip and reports it. Otherwise an oversized manifest from a server bug would look like counts
    // gone missing.

    [Fact]
    public void Truncation_is_off_without_a_config()
    {
        var context = new CollectContext { RemoteConfig = null };

        Assert.Empty(context.TruncatedManifests);
    }

    [Fact]
    public void Truncation_is_off_for_a_sane_flat_manifest()
    {
        var context = new CollectContext { RemoteConfig = ConfigWith(new uint[] { 1, 2, 3 }) };

        Assert.Empty(context.TruncatedManifests);
    }

    [Fact]
    public void Truncation_is_on_for_an_oversized_flat_manifest()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext { RemoteConfig = ConfigWith(oversized) };

        Assert.True(context.TruncatedManifests.Contains(CategoryKeys.Items));
    }

    [Fact]
    public void Truncation_is_off_at_exactly_the_cap()
    {
        var exact = Enumerable.Range(1, CollectContext.MaxManifestItems)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext { RemoteConfig = ConfigWith(exact) };

        Assert.Empty(context.TruncatedManifests);
    }

    [Fact]
    public void Truncation_is_on_for_an_oversized_enabled_group_union()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(("relic-proofs", oversized, true)),
            EnabledItemGroupKeys = new HashSet<string> { "relic-proofs" },
        };

        Assert.True(context.TruncatedManifests.Contains(CategoryKeys.Items));
    }

    // The union dedupes before it truncates, so the verdict counts the same way: two groups sharing
    // most ids can sum past the cap while their union fits.
    [Fact]
    public void Truncation_counts_deduped_ids_not_the_raw_group_sum()
    {
        var shared = Enumerable.Range(1, CollectContext.MaxManifestItems - 1)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext
        {
            // The raw sum is nearly twice the cap; the deduped union is exactly the cap.
            RemoteConfig = ConfigWithGroups(
                ("a", shared, true),
                ("b", shared.Append(900_000u).ToArray(), false)),
            EnabledItemGroupKeys = new HashSet<string> { "a", "b" },
        };

        Assert.Empty(context.TruncatedManifests);
    }

    // An empty groups array means no groups here too, so an oversized flat manifest is still reported
    // rather than masked by an empty group loop.
    [Fact]
    public void Truncation_falls_back_to_the_flat_manifest_for_an_empty_group_list()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var config = ConfigWith(oversized) with
        {
            ItemManifestGroups = Array.Empty<ItemManifestGroup>(),
        };

        var context = new CollectContext { RemoteConfig = config };

        Assert.True(context.TruncatedManifests.Contains(CategoryKeys.Items));
    }

    [Fact]
    public void Truncation_ignores_disabled_groups()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("small", new uint[] { 1 }, true),
                ("huge", oversized, false)),
            EnabledItemGroupKeys = new HashSet<string> { "small" },
        };

        Assert.Empty(context.TruncatedManifests);
    }

    // --- Quest sequence manifest ---------------------------------------------------------------
    // Also server input read on the framework thread, so it is bounded here too.

    [Fact]
    public void The_quest_sequence_manifest_is_empty_without_a_config_or_the_field()
    {
        Assert.Empty(new CollectContext { RemoteConfig = null }.ManifestFor(CategoryKeys.QuestSequences));

        // An older server that never sends the field reads as nothing to check.
        Assert.Empty(
            new CollectContext { RemoteConfig = ConfigWith(Array.Empty<uint>()) }
                .ManifestFor(CategoryKeys.QuestSequences));
    }

    [Fact]
    public void A_sane_quest_sequence_manifest_passes_through_untouched()
    {
        var config = ConfigWith(Array.Empty<uint>()) with
        {
            QuestSequenceManifest = new uint[] { 70562, 69208 },
        };

        var context = new CollectContext { RemoteConfig = config };

        Assert.Equal(new uint[] { 70562, 69208 }, context.ManifestFor(CategoryKeys.QuestSequences));
    }

    // Each id walks the game's active-quest array on the framework thread: cheap, but not a million
    // times over.
    [Fact]
    public void An_oversized_quest_sequence_manifest_is_truncated_to_the_cap()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 500)
            .Select(id => (uint)id)
            .ToArray();
        var config = ConfigWith(Array.Empty<uint>()) with { QuestSequenceManifest = oversized };

        var bounded = new CollectContext { RemoteConfig = config }.ManifestFor(CategoryKeys.QuestSequences);

        Assert.Equal(CollectContext.MaxManifestItems, bounded.Count);
        Assert.Equal(1u, bounded[0]);
        Assert.Equal((uint)CollectContext.MaxManifestItems, bounded[^1]);
    }

    // Reported under its own key in the truncated set, like the item clip.
    [Fact]
    public void Quest_sequence_truncation_is_off_without_a_config_or_at_the_cap()
    {
        Assert.Empty(new CollectContext { RemoteConfig = null }.TruncatedManifests);

        var exact = Enumerable.Range(1, CollectContext.MaxManifestItems)
            .Select(id => (uint)id)
            .ToArray();
        var config = ConfigWith(Array.Empty<uint>()) with { QuestSequenceManifest = exact };

        Assert.Empty(new CollectContext { RemoteConfig = config }.TruncatedManifests);
    }

    [Fact]
    public void Quest_sequence_truncation_is_on_for_an_oversized_manifest()
    {
        var oversized = Enumerable.Range(1, CollectContext.MaxManifestItems + 1)
            .Select(id => (uint)id)
            .ToArray();
        var config = ConfigWith(Array.Empty<uint>()) with { QuestSequenceManifest = oversized };

        Assert.True(new CollectContext { RemoteConfig = config }.TruncatedManifests.Contains(CategoryKeys.QuestSequences));
    }

    [Fact]
    public void The_enabled_group_list_is_empty_without_a_config()
    {
        var context = new CollectContext();

        Assert.Empty(context.EnabledGroups);
    }

    [Fact]
    public void The_enabled_group_list_contains_only_opted_in_groups()
    {
        // The collector needs the groups, not just the union, to report honestly what it scanned; the
        // settings view needs them for checkboxes.
        var context = new CollectContext
        {
            RemoteConfig = ConfigWithGroups(
                ("a", new uint[] { 1 }, false), ("b", new uint[] { 2 }, false)),
            EnabledItemGroupKeys = new HashSet<string> { "b" },
        };

        Assert.Equal(new[] { "b" }, context.EnabledGroups.Select(g => g.Key));
    }
}
