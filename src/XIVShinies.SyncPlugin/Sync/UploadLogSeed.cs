#if DEBUG
using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Builds plausible upload-log entries for screenshots, without waiting on real syncs. Reads neither
/// the game nor the clock.
/// </summary>
/// <remarks>
/// The log's interesting states are slow or expensive to reach honestly: the interval sync is half an
/// hour away, a "(changed)" mark needs a real acquisition, and the proof note needs the server to
/// derive a relic step. Compiled into DEBUG builds only, because these entries are fabricated and a
/// shipped Release build must never show invented sync history. Rows are built from the registered
/// collectors, so a new collection appears on its own, and a row that needs one collection names it
/// through <see cref="CategoryKeys"/>, so a renamed key fails to compile.
/// </remarks>
public static class UploadLogSeed
{
    /// <summary>
    /// A believable fact count per category, used when nothing more specific is called for.
    /// </summary>
    /// <remarks>
    /// Not real numbers from anyone's character, and not round ones: "500" reads as placeholder art,
    /// while an odd number reads as a real collection. Keyed by category so each figure is in the
    /// right range, since there are far more quests than minions.
    /// <c>UploadLogSeedTests.Every_declared_category_has_a_figure_on_file</c> fails when a declared
    /// category has no figure here.
    /// </remarks>
    internal static readonly Dictionary<string, int> PlausibleCounts = new()
    {
        [CategoryKeys.Quests] = 2847,
        [CategoryKeys.Achievements] = 1183,
        [CategoryKeys.Mounts] = 214,
        [CategoryKeys.Minions] = 387,
        [CategoryKeys.Items] = 156,
        [CategoryKeys.QuestSequences] = 23,
        [CategoryKeys.OrchestrionRolls] = 413,
        [CategoryKeys.TripleTriadCards] = 291,
        [CategoryKeys.TripleTriadNpcs] = 78,
        [CategoryKeys.OccultProgression] = 24,
        [CategoryKeys.OccultRecords] = 31,

        // Single digits: the whole bestiary is fifty rows, and a three-figure count would photograph
        // as a collection this one can never be.
        [CategoryKeys.TamedBeasts] = 3,
    };

    /// <summary>The count for a collection missing from <see cref="PlausibleCounts"/>.</summary>
    private const int UnknownCategoryCount = 62;

    /// <summary>
    /// Builds the entries oldest first, the order <see cref="UploadLog.Record"/> wants, since it
    /// pushes each new entry to the front.
    /// </summary>
    /// <param name="collectors">The registered collectors, in registration order.</param>
    /// <param name="now">
    /// When the newest entry should appear to have happened. The window shows local times, so the
    /// real current time makes the log look live.
    /// </param>
    public static IReadOnlyList<UploadLogEntry> Build(
        IReadOnlyList<ICollector> collectors, DateTimeOffset now)
    {
        // Every row needs a collection. Reading an empty list would throw in a command handler, which
        // Dalamud catches and logs with nothing on screen to explain it. This also keeps Find safe.
        if (collectors.Count == 0)
            return [];

        // One manifest version for every entry, as in a real session: it comes from /config and does
        // not change between uploads.
        const string manifestVersion = "a1b9c4e7";

        var everything = new List<UploadLogCategory>(collectors.Count);
        foreach (var collector in collectors)
            everything.Add(Category(collector));

        // The login row's unreadable collection, both skipped and left out so the row never blames one it
        // sent. Null when absent: its skip reason names the Occult Crescent, so no other may stand in.
        var unreadable = FindOrNull(collectors, CategoryKeys.OccultProgression)?.CategoryKey;

        return
        [
            // Oldest. A login sync with one collection unreadable: the state that draws the
            // "Could not read:" line under the row.
            new UploadLogEntry
            {
                At = now.AddMinutes(-47),
                Trigger = SyncTrigger.Login,
                Status = ApiStatus.Ok,
                Categories = unreadable is null ? everything : Without(everything, unreadable),
                Skipped = unreadable is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>
                    {
                        [unreadable] = CollectSkipReasons.NotInOccultInstance,
                    },
                ManifestVersion = manifestVersion,
                ProvenSteps = 0,
            },

            // A manual sync where the server proved relic steps: the row with the proof note.
            new UploadLogEntry
            {
                At = now.AddMinutes(-31),
                Trigger = SyncTrigger.Manual,
                Status = ApiStatus.Ok,
                Categories = everything,
                Skipped = new Dictionary<string, string>(),
                ManifestVersion = manifestVersion,
                ProvenSteps = 2,
            },

            // A scheduled sync with one collection moved since the manual sync: one "(changed)" mark
            // among steady counts.
            new UploadLogEntry
            {
                At = now.AddMinutes(-12),
                Trigger = SyncTrigger.Interval,
                Status = ApiStatus.Ok,
                Categories = Moved(everything, CategoryKeys.Minions),
                Skipped = new Dictionary<string, string>(),
                ManifestVersion = manifestVersion,
                ProvenSteps = 0,
            },

            // Newest. An unlock upload carries only the collection that changed, so the row is short
            // and its one category is marked changed.
            new UploadLogEntry
            {
                At = now.AddMinutes(-1),
                Trigger = SyncTrigger.Unlock,
                Status = ApiStatus.Ok,
                Categories = [Moved(Category(Find(collectors, CategoryKeys.Mounts)))],
                Skipped = new Dictionary<string, string>(),
                ManifestVersion = manifestVersion,
                ProvenSteps = 0,
            },
        ];
    }

    /// <summary>One category as an upload sent it, with a count in a believable range.</summary>
    /// <remarks>
    /// The fingerprint comes from the key and count: there are no facts here, and the diff only checks
    /// fingerprints for equality.
    /// </remarks>
    private static UploadLogCategory Category(ICollector collector)
    {
        var count = PlausibleCounts.TryGetValue(collector.CategoryKey, out var known)
            ? known
            : UnknownCategoryCount;

        return new UploadLogCategory(
            collector.CategoryKey,
            count,
            $"{collector.CategoryKey}-{count}",
            collector.UsesItemManifest,

            // The diff compares a manifest-driven category on its owned count, so it needs one. Below
            // the fact count, since a manifest asks about items the character lacks.
            collector.UsesItemManifest ? 43 : null);
    }

    /// <summary>The same categories with one of them moved, so the diff marks it "(changed)".</summary>
    /// <remarks>
    /// Moves the signal the diff compares for that category (the owned count for a manifest-driven
    /// one, the fact count and fingerprint otherwise), so a seeded row never claims a change the
    /// window declines to draw. Falls back to the first collection when the named one is not
    /// registered, so the row still shows a "(changed)" mark rather than quietly showing none.
    /// </remarks>
    private static IReadOnlyList<UploadLogCategory> Moved(
        IReadOnlyList<UploadLogCategory> categories, string categoryKey)
    {
        var target = IndexOf(categories, categoryKey) is { } found ? found : 0;

        var moved = new List<UploadLogCategory>(categories.Count);
        for (var i = 0; i < categories.Count; i++)
            moved.Add(i == target ? Moved(categories[i]) : categories[i]);

        return moved;
    }

    private static int? IndexOf(IReadOnlyList<UploadLogCategory> categories, string categoryKey)
    {
        for (var i = 0; i < categories.Count; i++)
        {
            if (categories[i].Key == categoryKey)
                return i;
        }

        return null;
    }

    private static UploadLogCategory Moved(UploadLogCategory category) =>
        category.UsesItemManifest
            ? category with { OwnedCount = category.OwnedCount + 1 }
            : category with
            {
                Count = category.Count + 1,
                Fingerprint = category.Fingerprint + "-moved",
            };

    /// <summary>The categories minus one, for a row where that collection could not be read.</summary>
    /// <remarks>
    /// Drops the first collection when the named one is not registered, because the row exists to
    /// show a category missing from the sent list.
    /// </remarks>
    private static IReadOnlyList<UploadLogCategory> Without(
        IReadOnlyList<UploadLogCategory> categories, string categoryKey)
    {
        var target = IndexOf(categories, categoryKey) is { } found ? found : 0;

        var kept = new List<UploadLogCategory>(categories.Count);
        for (var i = 0; i < categories.Count; i++)
        {
            if (i != target)
                kept.Add(categories[i]);
        }

        return kept;
    }

    /// <summary>
    /// The collector for a key, or null when none announces it: for a row that would rather say
    /// nothing than the wrong thing. <see cref="Find"/> falls back to any collection instead.
    /// </summary>
    private static ICollector? FindOrNull(IReadOnlyList<ICollector> collectors, string categoryKey)
    {
        foreach (var collector in collectors)
        {
            if (collector.CategoryKey == categoryKey)
                return collector;
        }

        return null;
    }

    /// <summary>
    /// The registered collector for a key, or the first registered one when nothing matches.
    /// </summary>
    /// <remarks>
    /// The fallback keeps a screenshot possible with whatever collectors a build registers.
    /// <see cref="Build"/> returns early on an empty list, so a first collector always exists.
    /// </remarks>
    private static ICollector Find(IReadOnlyList<ICollector> collectors, string categoryKey)
    {
        foreach (var collector in collectors)
        {
            if (collector.CategoryKey == categoryKey)
                return collector;
        }

        return collectors[0];
    }
}
#endif
