#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;
using XIVShinies.SyncPlugin.Tests.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The screenshot seed stages the upload log's states without waiting on real syncs. It fails
// quietly: a seeded row claims a state the window then declines to draw. So the tests of a claimed
// state run the seed through the window's own rules (UploadLogDiff, UploadLogText).
//
// DEBUG only, like the seed: `dotnet test -c Release` skips this file, which is why CI builds and
// tests both configurations.
public class UploadLogSeedTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);

    // The seed reads only a collector's key and manifest flag; the rest satisfies the interface. A
    // fake keeps these tests free of Dalamud and pins that the seed reads what it is given.
    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey) => CategoryKey = categoryKey;

        public string CategoryKey { get; }

        public string DisplayName => CategoryKey;

        public string Section => "Fakes";

        public string WhatGetsSent => $"Facts about {CategoryKey}.";

        public string? Details => null;

        public bool UsesItemManifest { get; init; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids([1u]);
    }

    // The three collections the seed names, the manifest-driven one, and one the seed does not know.
    private static IReadOnlyList<ICollector> Collectors() =>
    [
        new FakeCollector(CategoryKeys.Mounts),
        new FakeCollector(CategoryKeys.Minions),
        new FakeCollector(CategoryKeys.Items) { UsesItemManifest = true },
        new FakeCollector(CategoryKeys.OccultProgression),
        new FakeCollector("facewear"),
    ];

    // Newest first, as the window reads the log. Build returns oldest first because Record pushes
    // each entry to the front.
    private static IReadOnlyList<UploadLogEntry> AsDisplayed() =>
        UploadLogSeed.Build(Collectors(), Now).Reverse().ToList();

    // --- The order the log is built in -------------------------------------------------------

    // Built backwards, the log would show a login sync as newer than an unlock.
    [Fact]
    public void The_entries_are_built_oldest_first()
    {
        var built = UploadLogSeed.Build(Collectors(), Now);

        for (var i = 1; i < built.Count; i++)
            Assert.True(built[i - 1].At < built[i].At);
    }

    // Checks the handoff from Build to Record in a real log. Every test below reads the log as the
    // window does, so an inverted log would quietly invalidate them all.
    [Fact]
    public void Recording_the_seed_into_a_log_puts_the_newest_upload_first()
    {
        var log = new UploadLog();
        foreach (var entry in UploadLogSeed.Build(Collectors(), Now))
            log.Record(entry);

        Assert.Equal(SyncTrigger.Unlock, log.Entries[0].Trigger);
        Assert.Equal(SyncTrigger.Login, log.Entries[^1].Trigger);
    }

    // A failure row would put a red status in a gallery screenshot.
    [Fact]
    public void Every_seeded_upload_succeeded()
    {
        Assert.All(UploadLogSeed.Build(Collectors(), Now), e => Assert.Equal(ApiStatus.Ok, e.Status));
    }

    // --- The states each row exists to show --------------------------------------------------

    // If the diff disagreed, the row would show a lone category with no mark.
    [Fact]
    public void The_unlock_row_carries_one_category_and_the_diff_marks_it_changed()
    {
        var displayed = AsDisplayed();

        Assert.Equal(SyncTrigger.Unlock, displayed[0].Trigger);

        var category = Assert.Single(displayed[0].Categories);
        Assert.Contains(category.Key, UploadLogDiff.ChangedCategories(displayed, 0));
    }

    // One "(changed)" mark among steady counts reads as "this is what moved"; more would not.
    [Fact]
    public void The_scheduled_row_sweeps_everything_and_marks_exactly_one_category()
    {
        var displayed = AsDisplayed();
        var index = displayed.ToList().FindIndex(e => e.Trigger == SyncTrigger.Interval);

        Assert.Equal(Collectors().Count, displayed[index].Categories.Count);
        Assert.Single(UploadLogDiff.ChangedCategories(displayed, index));
    }

    // The proof note appears only when the entry reports proved steps. ProofText is the rule the
    // window prints.
    [Fact]
    public void The_manual_row_shows_the_servers_proof_note()
    {
        var manual = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Manual);

        Assert.Equal("2 new steps proven", UploadLogText.ProofText(manual));
    }

    // The skip map drives the "Could not read:" line. A category cannot be both sent and unreadable
    // in one pass.
    [Fact]
    public void The_login_row_reports_a_category_it_could_not_read()
    {
        var login = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Login);

        Assert.Equal(
            new[] { CategoryKeys.OccultProgression }, login.UnreadableCategoryKeys);
        Assert.DoesNotContain(
            login.Categories, c => c.Key == CategoryKeys.OccultProgression);
    }

    // --- Staying honest as collections are added ---------------------------------------------

    // A collection added later appears without editing the seed, as it does in the runner and the
    // settings UI. With no figure on file it still gets a plausible count; zero would photograph as
    // "this collection synced nothing".
    [Fact]
    public void A_collection_the_seed_has_never_heard_of_still_gets_a_count()
    {
        var swept = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Interval);

        var unknown = Assert.Single(swept.Categories, c => c.Key == "facewear");
        Assert.True(unknown.Count > 0);
    }

    // The window and the "(changed)" logic compare a manifest-driven collection on its owned count.
    [Fact]
    public void The_manifest_driven_collection_carries_an_owned_count()
    {
        var swept = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Interval);

        var items = Assert.Single(swept.Categories, c => c.Key == CategoryKeys.Items);
        Assert.True(items.UsesItemManifest);
        Assert.NotNull(items.OwnedCount);
    }

    // The counts are made up but must be believable; a zero or a negative would photograph as a bug.
    [Fact]
    public void Every_seeded_count_is_a_believable_positive_number()
    {
        foreach (var entry in UploadLogSeed.Build(Collectors(), Now))
        {
            foreach (var category in entry.Categories)
            {
                Assert.NotNull(category.Count);
                Assert.InRange(category.Count!.Value, 1, 10_000);
            }
        }
    }

    // A manifest asks about items the character does not have, so the owned count is always smaller.
    [Fact]
    public void The_owned_count_is_smaller_than_the_number_of_items_asked_about()
    {
        var swept = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Interval);

        var items = Assert.Single(swept.Categories, c => c.Key == CategoryKeys.Items);
        Assert.True(items.OwnedCount < items.Count);
    }

    // Moving the fact count rather than the owned count would leave the row unmarked. The rows above
    // all move an ordinary collection, so this branch needs its own test.
    [Fact]
    public void Moving_a_manifest_driven_collection_marks_it_changed()
    {
        // Only the items collector is registered, so the scheduled row moves it: the seed falls back to
        // the first collection when the key it names is absent.
        var collectors = new List<ICollector>
        {
            new FakeCollector(CategoryKeys.Items) { UsesItemManifest = true },
        };

        var displayed = UploadLogSeed.Build(collectors, Now).Reverse().ToList();
        var index = displayed.FindIndex(e => e.Trigger == SyncTrigger.Interval);

        Assert.Contains(CategoryKeys.Items, UploadLogDiff.ChangedCategories(displayed, index));
    }

    // The skip reason must leave with its collection rather than land on another; Build says why.
    [Fact]
    public void The_unreadable_row_is_dropped_when_its_collection_is_not_seeded()
    {
        // No occult progression collector, as the gate filter leaves it when the user or the server
        // switches that collection off.
        var collectors = new List<ICollector>
        {
            new FakeCollector(CategoryKeys.Mounts),
            new FakeCollector(CategoryKeys.Minions),
        };

        var login = UploadLogSeed.Build(collectors, Now).Single(e => e.Trigger == SyncTrigger.Login);

        Assert.Empty(login.Skipped);
        Assert.Equal(collectors.Count, login.Categories.Count);
    }

    // Every row needs a collection, so an empty registry seeds nothing. Returning early also keeps the
    // builder's collectors[0] fallback safe.
    [Fact]
    public void An_empty_registry_produces_an_empty_log()
    {
        Assert.Empty(UploadLogSeed.Build(Array.Empty<ICollector>(), Now));
    }

    // The fallback count is believable for a small collection and absurd for a quest-sized one. This
    // fails when a collection is added, which is when someone should choose its number.
    [Fact]
    public void Every_declared_category_has_a_figure_on_file()
    {
        Assert.Empty(CategoryKeyReflection.All().Except(UploadLogSeed.PlausibleCounts.Keys));
    }
}
#endif
