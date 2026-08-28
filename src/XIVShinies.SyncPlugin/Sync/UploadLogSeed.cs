#if DEBUG
using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Builds a set of plausible upload-log entries for capturing screenshots of the log, without
/// waiting on real syncs.
/// </summary>
/// <remarks>
/// <para>
/// The upload log's interesting states are slow or expensive to reach honestly: the interval
/// sync is half an hour away, a "(changed)" mark needs an actual new acquisition, and the proof
/// note needs the server to derive a relic step. Capturing all of them in one window means
/// either a long wait or buying increasingly expensive minions, and it has to be repeated
/// whenever the log's layout changes. This produces the same shapes on demand.
/// </para>
/// <para>
/// <b>Compiled only into DEBUG builds.</b> The whole file sits inside an <c>#if DEBUG</c>, so a
/// Release build — the only kind that ships — contains no trace of it. That is deliberate:
/// these entries are fabricated, and a released plugin must never be able to put invented sync
/// history in front of a user.
/// </para>
/// <para>
/// The entries are built from the <b>registered collectors</b> rather than a hardcoded list of
/// collections, so a new collection appears in the screenshot on its own — at a fallback count
/// until someone gives it a figure in <see cref="PlausibleCounts"/>, which the test suite
/// requires. Where a row needs one specific collection, it names it through
/// <see cref="CategoryKeys"/>, so renaming a key is a compile error here rather than a silently
/// wrong screenshot.
/// </para>
/// <para>
/// Dalamud-free on purpose, like the rest of <c>Sync</c>'s pure logic: it takes the collectors
/// and a clock, and returns records. That keeps it unit-testable.
/// </para>
/// </remarks>
public static class UploadLogSeed
{
    /// <summary>
    /// A believable fact count for a category, used when nothing more specific is called for.
    /// </summary>
    /// <remarks>
    /// Deliberately not real numbers from anyone's character, and deliberately not round ones —
    /// a screenshot showing "500" reads as placeholder art, while an odd number reads as somebody's
    /// actual collection. Keyed by category so each collection gets a figure in the right ballpark
    /// (there are far more quests than minions), with a fallback for a collection added later.
    /// </remarks>
    // Internal rather than private so the test that enforces its coverage can reach it.
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
    };

    /// <summary>The count to show for a collection this file has never heard of.</summary>
    private const int UnknownCategoryCount = 62;

    /// <summary>
    /// Builds the entries, oldest first — the order <see cref="UploadLog.Record"/> wants, since it
    /// pushes each new entry to the front.
    /// </summary>
    /// <param name="collectors">The registered collectors, in registration order.</param>
    /// <param name="now">
    /// The moment the newest entry should appear to have happened. The window renders these as
    /// local times, so passing the real current time is what makes the log look live.
    /// </param>
    public static IReadOnlyList<UploadLogEntry> Build(
        IReadOnlyList<ICollector> collectors, DateTimeOffset now)
    {
        // An empty registry has nothing to photograph, and every row below assumes at least one
        // collection to talk about. Returning an empty log says that plainly; reaching into the
        // list first would throw, and the caller is a command handler whose exceptions Dalamud
        // catches and logs — so the failure would be a silent no-op with nothing on screen to
        // explain it. This guard is also what makes Find's collectors[0] fallback safe.
        if (collectors.Count == 0)
            return [];

        // Every entry claims the same manifest version, the way a real session does: the value
        // comes from /config and does not change between uploads.
        const string manifestVersion = "a1b9c4e7";

        var everything = new List<UploadLogCategory>(collectors.Count);
        foreach (var collector in collectors)
            everything.Add(Category(collector));

        // The collection the login row reports as unreadable. One value feeds both the skip map
        // and the omission, so the row can never blame a collection it also sent.
        //
        // Null when that collection is not among the ones being seeded — switched off by the user
        // or the server, so it never reaches this list. The skip reason names a specific in-game
        // place, so falling back to whichever collector was registered first would put a sentence
        // about the Occult Crescent under, say, Achievements. The row goes without its unreadable
        // line instead: a screenshot missing one state beats one asserting a wrong state.
        var unreadable = FindOrNull(collectors, CategoryKeys.OccultProgression)?.CategoryKey;

        return
        [
            // Oldest. A login sync reads everything it can, and one collection could not be read
            // at all — the state that draws the "Could not read:" line under the row.
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

            // A manual sync where the server proved relic steps from the items sent — the row that
            // shows the proof note beside the manifest-driven category.
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

            // A scheduled sync that swept everything, with a single collection moved since the
            // manual sync above — one "(changed)" mark among many steady counts.
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

            // Newest. An unlock upload carries only the collection that changed, which is what
            // makes this row short — and its one category is necessarily marked changed.
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
    /// The fingerprint is derived from the key and count rather than hashed from facts: nothing
    /// here has facts, and the diff only ever asks whether two fingerprints are equal.
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

            // A manifest-driven category is compared on how many of its entries the character
            // holds, so it needs one for the diff to have anything to say. Fewer than the fact
            // count, since a manifest asks about items the character does not have.
            collector.UsesItemManifest ? 43 : null);
    }

    /// <summary>The same categories with one of them moved, so the diff marks it "(changed)".</summary>
    /// <remarks>
    /// <para>
    /// Moves whichever signal that category is actually compared on — the owned-entry count for a
    /// manifest-driven collection, the fact count and fingerprint for every other. Asking the same
    /// question the diff asks is what keeps a seeded row from claiming a change the window then
    /// declines to draw.
    /// </para>
    /// <para>
    /// Falls back to the first collection when the named one is not registered, so the row still
    /// demonstrates a "(changed)" mark. A key naming a collection that has been de-registered
    /// would otherwise leave the row with nothing marked at all — a screenshot that looks fine
    /// and shows the wrong thing.
    /// </para>
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

    /// <summary>Where a category sits in the list, or null when it is not there.</summary>
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
    /// Drops the first collection when the named one is not registered, on the same reasoning as
    /// <see cref="Moved(IReadOnlyList{UploadLogCategory}, string)"/>: the row exists to show a
    /// category missing from the sent list, and dropping nothing would quietly lose that.
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

    /// <summary>The collector for a key, or null when none of them announces it.</summary>
    /// <remarks>
    /// For a row that would rather say nothing than say the wrong thing. Where a row can carry on
    /// with any collection at all, <see cref="Find"/>'s fallback is the one to use.
    /// </remarks>
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
    /// The fallback keeps a screenshot possible from whatever collectors a developer's build
    /// happens to register. <see cref="Build"/> returns early on an empty list, so there is always
    /// a first collector to fall back to.
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
