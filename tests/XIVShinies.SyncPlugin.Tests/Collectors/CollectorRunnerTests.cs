using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The payload half of the extensibility gate: every test uses a fake collector for "facewear", a
// category the plugin has never heard of. A test that must change to add a real collection means the
// runner has grown a category-name branch.
public class CollectorRunnerTests
{
    private const string UnknownCategory = "facewear";

    private sealed class FakeCollector : ICollector
    {
        private readonly Func<CollectResult> collect;

        public FakeCollector(string categoryKey, Func<CollectResult> collect)
        {
            CategoryKey = categoryKey;
            this.collect = collect;
        }

        public string CategoryKey { get; }

        // The runner never reads the user-facing copy.
        public string DisplayName => CategoryKey;

        public string Section => "Fakes";

        public string WhatGetsSent => $"Facts about {CategoryKey}.";

        public string? Details { get; init; }

        public bool UsesItemManifest { get; init; }

        public int CollectCallCount { get; private set; }

        public CollectContext? LastContext { get; private set; }

        public CollectResult Collect(CollectContext context)
        {
            CollectCallCount++;
            LastContext = context;
            return collect();
        }
    }

    private static FakeCollector Collecting(string key, params uint[] ids) =>
        new(key, () => CollectResult.Ids(ids));

    private static PluginSettings OptedIn(params string[] categoryKeys)
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };
        foreach (var key in categoryKeys)
            settings.SetCategoryEnabled(key, true);

        return settings;
    }

    private static ConfigResponse RemoteConfig(bool enabled = true, Dictionary<string, bool>? categories = null) => new()
    {
        Categories = categories ?? new Dictionary<string, bool>(),
        Enabled = enabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
    };

    // The gate itself.
    [Fact]
    public void A_collector_for_an_unknown_category_flows_through_untouched()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal(42u, snapshot.Collections[UnknownCategory].AsArray()[0]!.GetValue<uint>());
        Assert.Empty(snapshot.Skipped);
    }

    // The upload log picks each category's change signal from this set, never from a hardcoded name.
    [Fact]
    public void A_manifest_driven_collectors_key_is_marked_in_the_snapshot()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Ids(new[] { 42u }))
        {
            UsesItemManifest = true,
        };

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Contains(UnknownCategory, snapshot.ManifestDrivenKeys);
    }

    [Fact]
    public void A_fixed_scope_collectors_key_is_not_marked_manifest_driven()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.DoesNotContain(UnknownCategory, snapshot.ManifestDrivenKeys);
    }

    // The payload builder turns this set into `collectionScopes` "full" declarations.
    [Fact]
    public void A_complete_enumerations_key_is_marked_in_the_snapshot()
    {
        var collector = new FakeCollector(
            UnknownCategory, () => CollectResult.Ids(new[] { 42u }, completeEnumeration: true));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Contains(UnknownCategory, snapshot.CompleteKeys);
    }

    [Fact]
    public void A_partial_reads_key_is_not_marked_complete()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.DoesNotContain(UnknownCategory, snapshot.CompleteKeys);
    }

    // A skip carries no facts, so it can never carry a completeness claim.
    [Fact]
    public void A_skipped_collectors_key_is_not_marked_complete()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Skipped("because"));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.DoesNotContain(UnknownCategory, snapshot.CompleteKeys);
    }

    // The orchestrator, which holds the logger, sees only the snapshot, so the verdict must reach it.
    [Fact]
    public void The_snapshot_carries_the_manifest_truncation_verdict()
    {
        var oversized = new uint[CollectContext.MaxManifestItems + 1];
        for (var i = 0; i < oversized.Length; i++)
            oversized[i] = (uint)(i + 1);

        var config = RemoteConfig() with { ItemManifest = oversized };

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), config);

        Assert.True(snapshot.TruncatedManifests.Contains(CategoryKeys.Items));
    }

    [Fact]
    public void The_snapshot_reports_no_truncation_for_a_sane_manifest()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Empty(snapshot.TruncatedManifests);
    }

    [Fact]
    public void The_snapshot_carries_the_quest_sequence_truncation_verdict()
    {
        var oversized = new uint[CollectContext.MaxManifestItems + 1];
        for (var i = 0; i < oversized.Length; i++)
            oversized[i] = (uint)(i + 1);

        var config = RemoteConfig() with { QuestSequenceManifest = oversized };

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), config);

        Assert.True(snapshot.TruncatedManifests.Contains(CategoryKeys.QuestSequences));
        Assert.False(snapshot.TruncatedManifests.Contains(CategoryKeys.Items)); // the two clips report independently
    }

    // The shape guard: a runner that assumed AsArray() would pass every array test here and still
    // crash on the first object-shaped category in game.
    [Fact]
    public void Object_shaped_facts_flow_through_untouched()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Sequences(
            new Dictionary<uint, byte> { [70562] = 3 }));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Equal(
            3, snapshot.Collections[UnknownCategory].AsObject()["70562"]!.GetValue<int>());
    }

    // The gate for partial notes: the runner files the phrase by key without knowing which collection
    // wrote it.
    [Fact]
    public void A_collectors_partial_note_lands_in_the_snapshot_under_its_key()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Progression(
            new Dictionary<byte, OccultJobProgress>(),
            knowledge: null,
            partialNote: "half read — do the in-game thing."));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Equal("half read — do the in-game thing.", snapshot.PartialNotes[UnknownCategory]);
        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
    }

    // No entry is what clears a remembered note.
    [Fact]
    public void A_collector_without_a_partial_note_leaves_none_in_the_snapshot()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 1) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Empty(snapshot.PartialNotes);
    }

    // Filed by key like the partial note; the upload log reads the set.
    [Fact]
    public void A_collector_that_read_nothing_lands_in_the_snapshots_nothing_read_keys()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Progression(
            new Dictionary<byte, OccultJobProgress>(), knowledge: null));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Contains(UnknownCategory, snapshot.NothingReadKeys);

        // Reading nothing is not skipping: the facts still went out, so the category is carried.
        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.DoesNotContain(UnknownCategory, snapshot.FactCounts.Keys);
    }

    // A category is never both measured and unmeasured.
    [Fact]
    public void A_collector_that_read_its_collection_leaves_no_unmeasured_key()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Progression(
            new Dictionary<byte, OccultJobProgress>
            {
                [3] = new OccultJobProgress { Exp = 10, Level = 2 },
            },
            knowledge: null));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Empty(snapshot.NothingReadKeys);
        Assert.Equal(1, snapshot.FactCounts[UnknownCategory]);
    }

    // The healthy chip's hover copy, filed like the partial note.
    [Fact]
    public void A_collectors_chip_detail_lands_in_the_snapshot_under_its_key()
    {
        var collector = new FakeCollector(UnknownCategory, () => CollectResult.Progression(
            new Dictionary<byte, OccultJobProgress>(),
            knowledge: null,
            collectedDetail: "Optional hover copy."));

        var snapshot = CollectorRunner.Run(
            new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Equal("Optional hover copy.", snapshot.CollectedDetails[UnknownCategory]);
    }

    // No entry is what clears remembered hover copy.
    [Fact]
    public void A_collector_without_a_chip_detail_leaves_none_in_the_snapshot()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 1) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Empty(snapshot.CollectedDetails);
    }

    [Fact]
    public void A_category_the_user_never_opted_into_is_omitted()
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, settings, RemoteConfig());

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal(CollectSkipReasons.Disabled, snapshot.Skipped[UnknownCategory]);
    }

    [Fact]
    public void The_master_switch_off_collects_nothing()
    {
        var settings = OptedIn(UnknownCategory);
        settings.MasterEnabled = false;

        var collector = Collecting(UnknownCategory, 42);
        var snapshot = CollectorRunner.Run(new[] { collector }, settings, RemoteConfig());

        Assert.Empty(snapshot.Collections);
        Assert.Equal(0, collector.CollectCallCount);
    }

    [Fact]
    public void Nothing_is_collected_before_onboarding_completes()
    {
        var settings = OptedIn(UnknownCategory);
        settings.OnboardingComplete = false;

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, settings, RemoteConfig());

        Assert.Empty(snapshot.Collections);
    }

    [Fact]
    public void The_remote_global_kill_switch_collects_nothing()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) },
            OptedIn(UnknownCategory),
            RemoteConfig(enabled: false));

        Assert.Empty(snapshot.Collections);
    }

    [Fact]
    public void A_remote_per_category_kill_switch_omits_only_that_category()
    {
        var categories = new Dictionary<string, bool> { [UnknownCategory] = false, ["quests"] = true };

        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42), Collecting("quests", 7) },
            OptedIn(UnknownCategory, "quests"),
            RemoteConfig(categories: categories));

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.True(snapshot.Collections.ContainsKey("quests"));
    }

    // Proceeding without /config is safe (see CollectorGate.IsEnabled).
    [Fact]
    public void A_missing_remote_config_falls_back_to_the_local_toggles()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 42) }, OptedIn(UnknownCategory), remoteConfig: null);

        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
    }

    // The UI surfaces the reason; the runner never interprets it.
    [Fact]
    public void A_collector_that_skips_omits_its_key_and_reports_the_reason()
    {
        var collector = new FakeCollector(
            UnknownCategory, () => CollectResult.Skipped("list_not_loaded"));

        var snapshot = CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal("list_not_loaded", snapshot.Skipped[UnknownCategory]);
    }

    // The monotonic-write rule at the source: "read, and empty" goes out as [], while "could not read"
    // is absence.
    [Fact]
    public void An_empty_but_readable_category_is_sent_as_an_empty_array()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory) }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Empty(snapshot.Collections[UnknownCategory].AsArray());
    }

    // A misbehaving collector must never abort the snapshot or reach the game as an exception.
    [Fact]
    public void A_collector_that_throws_is_isolated_and_the_others_still_run()
    {
        var exploding = new FakeCollector(UnknownCategory, () => throw new InvalidOperationException("boom"));
        var healthy = Collecting("quests", 7);

        var snapshot = CollectorRunner.Run(
            new ICollector[] { exploding, healthy }, OptedIn(UnknownCategory, "quests"), RemoteConfig());

        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
        Assert.Equal(CollectSkipReasons.CollectorError, snapshot.Skipped[UnknownCategory]);
        Assert.True(snapshot.Collections.ContainsKey("quests"));
    }

    // Outside data such as the item manifest comes through the context, so the runner never asks which
    // collector it is running.
    [Fact]
    public void The_context_carries_the_remote_config_and_item_manifest_to_every_collector()
    {
        var remote = RemoteConfig();
        var collector = Collecting(UnknownCategory, 1);

        CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), remote);

        Assert.Same(remote, collector.LastContext!.RemoteConfig);
        Assert.Empty(collector.LastContext.ManifestFor(CategoryKeys.Items));
    }

    [Fact]
    public void The_item_manifest_is_empty_when_no_config_has_been_fetched()
    {
        var collector = Collecting(UnknownCategory, 1);

        CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), remoteConfig: null);

        Assert.Null(collector.LastContext!.RemoteConfig);
        Assert.Empty(collector.LastContext.ManifestFor(CategoryKeys.Items));
    }

    [Fact]
    public void Items_facts_ride_alongside_id_lists_with_no_special_casing()
    {
        var items = new FakeCollector("items", () => CollectResult.Items(
            new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } }));

        var snapshot = CollectorRunner.Run(
            new ICollector[] { items, Collecting("quests", 7) },
            OptedIn("items", "quests"),
            RemoteConfig());

        Assert.Equal(7851u, snapshot.Collections["items"].AsArray()[0]!["id"]!.GetValue<uint>());
        Assert.Equal(7u, snapshot.Collections["quests"].AsArray()[0]!.GetValue<uint>());
    }

    // Collection runs on the framework thread, which has about 16 ms per frame. An unmeasured cost goes
    // unnoticed until the game stutters.
    [Fact]
    public void Every_collector_that_runs_is_timed()
    {
        var snapshot = CollectorRunner.Run(
            new ICollector[] { Collecting("quests", 1), Collecting("mounts", 2) },
            OptedIn("quests", "mounts"),
            RemoteConfig());

        Assert.Equal(2, snapshot.Durations.Count);
        Assert.True(snapshot.Durations.ContainsKey("quests"));
        Assert.True(snapshot.Durations.ContainsKey("mounts"));
    }

    // Catches a runner that records zero for every collector.
    [Fact]
    public void A_collector_that_takes_time_reports_a_nonzero_duration()
    {
        var slow = new FakeCollector(UnknownCategory, () =>
        {
            // Spins rather than sleeps: a sleep lasts as long as the scheduler chooses, while spinning
            // guarantees elapsed work. A few milliseconds is far above timer resolution.
            var until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 500); // ~2ms
            while (Stopwatch.GetTimestamp() < until)
            {
            }

            return CollectResult.Ids(new uint[] { 1 });
        });

        var snapshot = CollectorRunner.Run(new[] { slow }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(snapshot.Durations[UnknownCategory] > TimeSpan.Zero);
    }

    // A slow collector that throws is the one most worth seeing in the log.
    [Fact]
    public void A_collector_that_throws_is_still_timed()
    {
        var thrower = new FakeCollector(UnknownCategory, () => throw new InvalidOperationException("boom"));

        var snapshot = CollectorRunner.Run(new[] { thrower }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(snapshot.Durations.ContainsKey(UnknownCategory));
        Assert.False(snapshot.Collections.ContainsKey(UnknownCategory));
    }

    // A zero would claim a measurement that never happened.
    [Fact]
    public void A_category_the_user_disabled_is_not_timed_because_it_never_ran()
    {
        var snapshot = CollectorRunner.Run(
            new[] { Collecting(UnknownCategory, 1) },
            OptedIn(), // opted into nothing
            RemoteConfig());

        Assert.Empty(snapshot.Durations);
    }

    // A hand-built snapshot need not carry timings, and callers enumerate the default without a null
    // check.
    [Fact]
    public void Durations_default_to_empty_rather_than_null()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>(),
            Skipped = new Dictionary<string, string>(),
        };

        Assert.NotNull(snapshot.Durations);
        Assert.Empty(snapshot.Durations);
    }

    // The payload builder enumerates this set without a null check on every non-unlock upload.
    [Fact]
    public void Complete_keys_default_to_empty_rather_than_null()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>(),
            Skipped = new Dictionary<string, string>(),
        };

        Assert.NotNull(snapshot.CompleteKeys);
        Assert.Empty(snapshot.CompleteKeys);
    }

    [Fact]
    public void Run_merges_source_notes_into_the_snapshot()
    {
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
            ["saddlebag"] = new ItemSourceStatus { State = SourceStates.Unscanned },
        };
        var collector = new FakeCollector(
            "items", () => CollectResult.Items(
                new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } },
                sourceNotes));

        var snapshot = CollectorRunner.Run(new[] { collector }, OptedIn("items"), RemoteConfig());

        Assert.NotNull(snapshot.SourceNotes);
        Assert.Equal(2, snapshot.SourceNotes.Count);
        Assert.Equal(SourceStates.Live, snapshot.SourceNotes["inventory"].State);
        Assert.Equal(SourceStates.Unscanned, snapshot.SourceNotes["saddlebag"].State);
    }

    [Fact]
    public void Run_collects_empty_source_notes_when_no_collector_reports_them()
    {
        var collector = Collecting(UnknownCategory, 42);

        var snapshot = CollectorRunner.Run(new[] { collector }, OptedIn(UnknownCategory), RemoteConfig());

        Assert.NotNull(snapshot.SourceNotes);
        Assert.Empty(snapshot.SourceNotes);
    }

    [Fact]
    public void Run_ignores_a_skipped_collector_when_merging_source_notes()
    {
        // A skip carries no source notes, so only collected results contribute. This pins the merge
        // to the runner's collected branch.
        var notes = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
        };
        var skipping = new FakeCollector("mounts", () => CollectResult.Skipped("sheet_unavailable"));
        var collecting = new FakeCollector(
            "items", () => CollectResult.Items(
                new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } },
                notes));

        var snapshot = CollectorRunner.Run(
            new ICollector[] { skipping, collecting }, OptedIn("mounts", "items"), RemoteConfig());

        Assert.Single(snapshot.SourceNotes);
        Assert.Equal(SourceStates.Live, snapshot.SourceNotes["inventory"].State);
    }

    [Fact]
    public void Run_merges_multiple_collectors_source_notes_with_last_one_winning()
    {
        var notes1 = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
        };
        var notes2 = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Cached },
            ["saddlebag"] = new ItemSourceStatus { State = SourceStates.Unscanned },
        };

        var collector1 = new FakeCollector(
            "achievements", () => CollectResult.Ids(new uint[] { 1 }));
        var collector2 = new FakeCollector(
            "items", () => CollectResult.Items(
                new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } },
                notes1));
        var collector3 = new FakeCollector(
            "mounts", () => CollectResult.Items(
                new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } },
                notes2));

        var snapshot = CollectorRunner.Run(
            new ICollector[] { collector1, collector2, collector3 },
            OptedIn("achievements", "items", "mounts"),
            RemoteConfig());

        Assert.Equal(SourceStates.Cached, snapshot.SourceNotes["inventory"].State);
        Assert.Equal(SourceStates.Unscanned, snapshot.SourceNotes["saddlebag"].State);
    }
}
