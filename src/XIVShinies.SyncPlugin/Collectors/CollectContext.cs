using System;
using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Everything a collector may need from outside the game, handed to it for one collection pass.
/// </summary>
/// <remarks>
/// A context object rather than constructor arguments, because these values change between passes:
/// the server's config is re-fetched periodically. A collector that needs nothing from it simply
/// ignores it — which is what keeps the runner free of "does this collector need the manifest?"
/// branching.
/// </remarks>
public sealed record CollectContext
{
    /// <summary>
    /// The most items a manifest may ask about. The manifest is server-controlled and consumed on
    /// the FRAMEWORK thread — the item collector runs several container scans per id — so a
    /// hostile or broken backend sending millions of ids would otherwise freeze the game loop.
    /// The ceiling is far above any legitimate manifest (dozens of ids today, low thousands if
    /// every relic series ever shipped); if a real manifest ever approaches it, the right fix is
    /// spreading the scan across frames, not raising this number.
    /// </summary>
    public const int MaxManifestItems = 5000;

    /// <summary>The most recent <c>/config</c> response, or null when it has not been fetched.</summary>
    public ConfigResponse? RemoteConfig { get; init; }

    /// <summary>
    /// The user's per-group opt-ins, keyed by group Key (stable consent identifier). Only
    /// consulted when the config carries groups; when groups are null, the flat manifest
    /// is ungated by group consent (only the category-level toggle gates it).
    /// </summary>
    public IReadOnlySet<string> EnabledItemGroupKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The server's omit-when-unseen ids as a set the item collector can probe per manifest id
    /// (see <see cref="ConfigResponse.ItemOmitWhenUnseenIds"/> for what the ids mean). Empty
    /// when no config has been fetched or the server does not send the field — both read as
    /// "omit nothing", which is the explicit-zero behavior every other id gets.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="MaxManifestItems"/> for the same reason the manifests are: it is a
    /// server-controlled list materialized on the framework thread, and the ceiling exists
    /// because the plugin does not trust the backend's length. Past the bound an id simply
    /// stops being omitted and reports an explicit zero, which is what an unlisted id does.
    /// It carries no truncation warning of its own — it is a modifier on the item manifest
    /// rather than a manifest, so a clip here shows up as counts the server chose not to omit.
    /// </remarks>
    // Recomputed on each read like the manifests; the collector reads it once per pass.
    public IReadOnlySet<uint> ItemOmitWhenUnseenIds
    {
        get
        {
            var omit = new HashSet<uint>();
            if (RemoteConfig?.ItemOmitWhenUnseenIds is not { } ids)
                return omit;

            foreach (var id in ids)
            {
                if (omit.Count >= MaxManifestItems)
                    break;

                omit.Add(id);
            }

            return omit;
        }
    }

    /// <summary>
    /// The subset of the server's item manifest groups whose key is in
    /// <see cref="EnabledItemGroupKeys"/>, in the server's original order. Empty when groups
    /// are null (flat manifest path) or when no groups are enabled. Collectors use this to
    /// report which groups they actually scanned; settings UI uses it to populate checkboxes.
    /// </summary>
    public IReadOnlyList<ItemManifestGroup> EnabledGroups
    {
        get
        {
            var groups = RemoteConfig?.ItemManifestGroups;
            if (groups is null)
                return Array.Empty<ItemManifestGroup>();

            return groups
                .Where(g => EnabledItemGroupKeys.Contains(g.Key))
                .ToList();
        }
    }

    /// <summary>
    /// The ids the named category's manifest asks about, already bounded to
    /// <see cref="MaxManifestItems"/>. Empty for a category the server sends no manifest for,
    /// and for one that has no manifest at all — both mean "nothing to look up".
    /// </summary>
    /// <remarks>
    /// The lookup goes through <see cref="ManifestCatalog"/>, so this method — and everything
    /// downstream of it — never names a category. A collector passes its own key
    /// (<c>ManifestFor(CategoryKeys.Items)</c>), which is a collector knowing its own business
    /// rather than the orchestrator branching on someone else's.
    /// </remarks>
    public IReadOnlyList<uint> ManifestFor(string categoryKey) =>
        ManifestCatalog.For(categoryKey)?.Read(this) ?? Array.Empty<uint>();

    /// <summary>
    /// The category keys whose manifest the server sent over-long, so its tail is not being
    /// reported. Empty in the ordinary case.
    /// </summary>
    /// <remarks>
    /// Keyed by category, so the orchestrator can report a clip for a manifest that did not
    /// exist when it was written. The clipping itself is silent at the point of use (a bounded
    /// scan must not depend on anyone noticing); this is how the orchestrator learns it
    /// happened so it can say so.
    /// </remarks>
    public IReadOnlySet<string> TruncatedManifests
    {
        get
        {
            var truncated = new HashSet<string>();
            foreach (var descriptor in ManifestCatalog.All)
            {
                if (descriptor.Truncated(this))
                    truncated.Add(descriptor.CategoryKey);
            }

            return truncated;
        }
    }

    /// <summary>
    /// The only item IDs the plugin may check possession of, truncated to
    /// <see cref="MaxManifestItems"/>. Empty when the server has not told us yet, in which case
    /// an item collector has nothing to do. When the server sends groups, this is the union of
    /// the enabled ones; otherwise it is the flat manifest, gated by the category toggle alone.
    /// </summary>
    /// <remarks>Reached through <see cref="ManifestFor"/>; see <see cref="ManifestCatalog"/>.</remarks>
    // Recomputed on each read rather than stored; collectors read it once per pass.
    internal IReadOnlyList<uint> ReadItemManifest()
    {
        // If no config has been fetched yet, nothing to scan.
        if (RemoteConfig is null)
            return Array.Empty<uint>();

        // `is { Count: > 0 }` folds the null test and the empty test together, because the two
        // mean the same thing here: a server offering no groups at all is not asking the user to
        // choose between any, so the flat manifest is what it wants scanned. Treating an empty
        // array as "groups exist, and none are enabled" instead would silently scan nothing while
        // the server was plainly asking for the flat list. It is also the reading the one-time
        // consent migration takes (see SyncManager), and the two must not disagree.
        if (RemoteConfig.ItemManifestGroups is { Count: > 0 })
            return GetGroupUnionManifest();

        var manifest = RemoteConfig.ItemManifest;
        if (manifest.Count <= MaxManifestItems)
            return manifest;

        // The hostile path only: copy the sane prefix and ignore the rest.
        var bounded = new uint[MaxManifestItems];
        for (var i = 0; i < MaxManifestItems; i++)
            bounded[i] = manifest[i];

        return bounded;
    }

    /// <summary>
    /// The quest ids whose journal sequence the server wants reported, truncated to
    /// <see cref="MaxManifestItems"/>. Empty when no config has been fetched or the server does
    /// not send the field — either way there is nothing to look up.
    /// </summary>
    /// <remarks>
    /// Reading a sequence is one walk of the game's active-quest array — far cheaper than the
    /// container scans behind <see cref="ReadItemManifest"/> — but the list is still server-controlled
    /// input consumed on the framework thread, so it goes through the same bound. Sharing the item
    /// cap (rather than inventing a second constant) keeps "how much may a server ask of one pass"
    /// a single number. Reached through <see cref="ManifestFor"/>; see
    /// <see cref="ManifestCatalog"/>.
    /// </remarks>
    // Recomputed on each read like the item manifest; the collector reads it once per pass.
    internal IReadOnlyList<uint> ReadQuestSequenceManifest()
    {
        var manifest = RemoteConfig?.QuestSequenceManifest;
        if (manifest is null)
            return Array.Empty<uint>();

        if (manifest.Count <= MaxManifestItems)
            return manifest;

        // The hostile path only: copy the sane prefix and ignore the rest.
        var bounded = new uint[MaxManifestItems];
        for (var i = 0; i < MaxManifestItems; i++)
            bounded[i] = manifest[i];

        return bounded;
    }

    /// <summary>
    /// True when <see cref="ReadQuestSequenceManifest"/> was clipped at
    /// <see cref="MaxManifestItems"/> — the server asked about more quests than the plugin will
    /// look up, and the tail is not being reported.
    /// </summary>
    /// <remarks>Reported per category through <see cref="TruncatedManifests"/>.</remarks>
    internal bool QuestSequenceManifestTruncated() =>
        RemoteConfig?.QuestSequenceManifest is { } manifest && manifest.Count > MaxManifestItems;

    /// <summary>
    /// True when <see cref="ReadItemManifest"/> was clipped at <see cref="MaxManifestItems"/> —
    /// the server asked about more ids than the plugin is willing to scan, and the tail is not
    /// being reported.
    /// </summary>
    /// <remarks>
    /// Counts the way the union does — deduped, enabled groups only — so it is true exactly when
    /// the item manifest left ids behind. Reported per category through
    /// <see cref="TruncatedManifests"/>.
    /// </remarks>
    internal bool ItemManifestTruncated()
    {
        if (RemoteConfig is null)
            return false;

        // Mirrors ReadItemManifest's branch: groups when the server sent any, flat otherwise.
        if (RemoteConfig.ItemManifestGroups is { Count: > 0 })
        {
            // Count deduped ids across the enabled groups, stopping as soon as the cap is
            // exceeded — the answer is settled at that point, so the rest need not be walked.
            var seen = new HashSet<uint>();
            foreach (var group in EnabledGroups)
            {
                foreach (var id in group.Ids)
                {
                    if (seen.Add(id) && seen.Count > MaxManifestItems)
                        return true;
                }
            }

            return false;
        }

        return RemoteConfig.ItemManifest.Count > MaxManifestItems;
    }

    /// <summary>
    /// Union the ids from all enabled groups into a single manifest, with deduplication and
    /// truncation to <see cref="MaxManifestItems"/>.
    /// </summary>
    /// <remarks>
    /// Two groups may legitimately name the same item id (e.g., a seasonal item that appears in
    /// multiple collection tracks). A duplicate entry would double-report it; the dedup guard
    /// ensures each id appears once in the manifest, in first-seen order.
    /// </remarks>
    private IReadOnlyList<uint> GetGroupUnionManifest()
    {
        var result = new List<uint>();
        var seen = new HashSet<uint>();

        foreach (var group in EnabledGroups)
        {
            foreach (var id in group.Ids)
            {
                // Only add if we haven't seen this id before (dedup).
                if (seen.Add(id))
                {
                    result.Add(id);
                    // Stop once we've collected enough ids.
                    if (result.Count >= MaxManifestItems)
                        return result;
                }
            }
        }

        return result;
    }
}
