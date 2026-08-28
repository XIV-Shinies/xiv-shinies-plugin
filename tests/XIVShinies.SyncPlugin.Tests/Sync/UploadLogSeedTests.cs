#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;
// CategoryKeyReflection, for checking the seed's figures still cover every declared category.
using XIVShinies.SyncPlugin.Tests.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The screenshot seed exists so the upload log's interesting states can be photographed without
// waiting on real syncs. Its one real failure mode is quiet: a seeded row that CLAIMS a state the
// window then declines to draw — a "(changed)" mark the diff does not agree with, a proof note
// that does not appear. So the tests that check a claimed state run the seeded entries through
// the same pure rules the window uses (UploadLogDiff, UploadLogText); the rest read the records.
//
// Compiled only into DEBUG builds, like the seed itself — `dotnet test -c Release` excludes this
// whole file, which is why CI builds and tests both configurations.
public class UploadLogSeedTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);

    // The seed asks a collector only for its key and its manifest flag; the rest of ICollector is
    // implemented because the interface requires it. Using a fake rather than the real registry
    // keeps these tests Dalamud-free and pins the seed's promise that it reads what it is GIVEN.
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

    // Stands in for the real registry: the three collections the seed names explicitly, the
    // manifest-driven one, and a collection this seed has never heard of.
    private static IReadOnlyList<ICollector> Collectors() =>
    [
        new FakeCollector(CategoryKeys.Mounts),
        new FakeCollector(CategoryKeys.Minions),
        new FakeCollector(CategoryKeys.Items) { UsesItemManifest = true },
        new FakeCollector(CategoryKeys.OccultProgression),
        new FakeCollector("facewear"),
    ];

    // The window reads the log newest first; Build returns oldest first, because Record pushes
    // each entry to the front. Reversing here is what the plugin's seeding loop does in effect.
    private static IReadOnlyList<UploadLogEntry> AsDisplayed() =>
        UploadLogSeed.Build(Collectors(), Now).Reverse().ToList();

    // --- The order the log is built in -------------------------------------------------------

    // Oldest first out of Build, so the entries come out newest first once recorded. Backwards,
    // the log would show a login sync as the most recent thing after an unlock.
    [Fact]
    public void The_entries_are_built_oldest_first()
    {
        var built = UploadLogSeed.Build(Collectors(), Now);

        for (var i = 1; i < built.Count; i++)
            Assert.True(built[i - 1].At < built[i].At);
    }

    // Recording the seed into a real log, rather than trusting the order it was built in. The
    // handoff is where the two halves could disagree — Build returns oldest first and Record
    // pushes to the front — and every test below reads the log the way the window does, so an
    // inverted log would quietly invalidate all of them.
    [Fact]
    public void Recording_the_seed_into_a_log_puts_the_newest_upload_first()
    {
        var log = new UploadLog();
        foreach (var entry in UploadLogSeed.Build(Collectors(), Now))
            log.Record(entry);

        Assert.Equal(SyncTrigger.Unlock, log.Entries[0].Trigger);
        Assert.Equal(SyncTrigger.Login, log.Entries[^1].Trigger);
    }

    // Every seeded upload has to look like one that succeeded: the point is to photograph the
    // ordinary log, and a failure row would put a red status in a gallery screenshot.
    [Fact]
    public void Every_seeded_upload_succeeded()
    {
        Assert.All(UploadLogSeed.Build(Collectors(), Now), e => Assert.Equal(ApiStatus.Ok, e.Status));
    }

    // --- The states each row exists to show --------------------------------------------------

    // The newest row is an unlock, which carries only the collection that changed — and the diff
    // must agree, or the row shows a lone category with no mark and the screenshot is pointless.
    [Fact]
    public void The_unlock_row_carries_one_category_and_the_diff_marks_it_changed()
    {
        var displayed = AsDisplayed();

        Assert.Equal(SyncTrigger.Unlock, displayed[0].Trigger);

        var category = Assert.Single(displayed[0].Categories);
        Assert.Contains(category.Key, UploadLogDiff.ChangedCategories(displayed, 0));
    }

    // The scheduled sync sweeps everything with exactly one collection moved, so the screenshot
    // shows a single "(changed)" mark among steady counts. More than one and the mark stops
    // reading as "this is the thing that moved".
    [Fact]
    public void The_scheduled_row_sweeps_everything_and_marks_exactly_one_category()
    {
        var displayed = AsDisplayed();
        var index = displayed.ToList().FindIndex(e => e.Trigger == SyncTrigger.Interval);

        Assert.Equal(Collectors().Count, displayed[index].Categories.Count);
        Assert.Single(UploadLogDiff.ChangedCategories(displayed, index));
    }

    // The manual row exists to show the server's proof note, which only appears when the entry
    // reports proved steps. Asserted through ProofText, the same rule the window prints.
    [Fact]
    public void The_manual_row_shows_the_servers_proof_note()
    {
        var manual = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Manual);

        Assert.Equal("2 new steps proven", UploadLogText.ProofText(manual));
    }

    // The login row exists to show the "Could not read:" line, which is driven by the skip map.
    // The skipped collection must also be absent from the categories, since a category cannot
    // have been both sent and unreadable in the same pass.
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

    // The seed reads the collectors it is given, so a collection added later appears in the
    // screenshot without anyone editing the seed — the same promise the runner and settings UI
    // make. A collection with no figure on file still gets a plausible count rather than zero,
    // which would photograph as "this collection synced nothing".
    [Fact]
    public void A_collection_the_seed_has_never_heard_of_still_gets_a_count()
    {
        var swept = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Interval);

        var unknown = Assert.Single(swept.Categories, c => c.Key == "facewear");
        Assert.True(unknown.Count > 0);
    }

    // A manifest-driven collection is compared on its owned-entry count, so it needs one — with
    // none, the window can say nothing about it and the "(changed)" logic has nothing to read.
    [Fact]
    public void The_manifest_driven_collection_carries_an_owned_count()
    {
        var swept = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Interval);

        var items = Assert.Single(swept.Categories, c => c.Key == CategoryKeys.Items);
        Assert.True(items.UsesItemManifest);
        Assert.NotNull(items.OwnedCount);
    }

    // Nothing here may invent a number a real character could not show. The counts are fabricated
    // but must stay in believable ranges, and a zero or a negative would photograph as a bug.
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

    // A manifest asks about items the character does not have, so the number owned is always the
    // smaller of the two. Equal or larger would photograph as a character who owns more of a list
    // than the list contains.
    [Fact]
    public void The_owned_count_is_smaller_than_the_number_of_items_asked_about()
    {
        var swept = AsDisplayed().Single(e => e.Trigger == SyncTrigger.Interval);

        var items = Assert.Single(swept.Categories, c => c.Key == CategoryKeys.Items);
        Assert.True(items.OwnedCount < items.Count);
    }

    // The manifest-driven collection is compared on its owned-entry count, so moving it has to
    // move THAT number — moving the fact count would leave the row unmarked. Covered separately
    // because the rows above all move an ordinary collection, leaving this branch unexercised.
    [Fact]
    public void Moving_a_manifest_driven_collection_marks_it_changed()
    {
        // Only the manifest-driven collection is registered, so it is the one the scheduled row
        // moves — the seed falls back to the first collection when the key it names is absent.
        var collectors = new List<ICollector>
        {
            new FakeCollector(CategoryKeys.Items) { UsesItemManifest = true },
        };

        var displayed = UploadLogSeed.Build(collectors, Now).Reverse().ToList();
        var index = displayed.FindIndex(e => e.Trigger == SyncTrigger.Interval);

        Assert.Contains(CategoryKeys.Items, UploadLogDiff.ChangedCategories(displayed, index));
    }

    // Pins that a collection absent from the seeded list takes its skip reason with it, rather
    // than the reason landing on another collection. Build explains why that matters.
    [Fact]
    public void The_unreadable_row_is_dropped_when_its_collection_is_not_seeded()
    {
        // No occult progression collector: exactly what the gate filter produces when the user or
        // the server has that collection switched off.
        var collectors = new List<ICollector>
        {
            new FakeCollector(CategoryKeys.Mounts),
            new FakeCollector(CategoryKeys.Minions),
        };

        var login = UploadLogSeed.Build(collectors, Now).Single(e => e.Trigger == SyncTrigger.Login);

        // Nothing blamed, and nothing withheld from the sent list to match a blame that was
        // never made.
        Assert.Empty(login.Skipped);
        Assert.Equal(collectors.Count, login.Categories.Count);
    }

    // The seed reads the collectors it is given, so an empty list means there is nothing to
    // photograph, and an empty log is the honest answer. The guard is also what makes the
    // collectors[0] fallback inside the builder safe.
    [Fact]
    public void An_empty_registry_produces_an_empty_log()
    {
        Assert.Empty(UploadLogSeed.Build(Array.Empty<ICollector>(), Now));
    }

    // The fallback keeps a screenshot possible, but it cannot keep the numbers sensible: a
    // collection with no figure on file photographs at the fallback, which is believable for a
    // small collection and absurd for a quest-sized one. This fails the moment a collection is
    // added, which is exactly when someone should be choosing its number.
    [Fact]
    public void Every_declared_category_has_a_figure_on_file()
    {
        Assert.Empty(CategoryKeyReflection.All().Except(UploadLogSeed.PlausibleCounts.Keys));
    }
}
#endif
