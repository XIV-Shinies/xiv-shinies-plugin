using System;
using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Everything a collector may need from outside the game, handed to it for one collection pass.
/// </summary>
/// <remarks>
/// A per-pass context rather than constructor arguments, because the server's config is re-fetched
/// between passes. A collector that needs nothing from it ignores it, so the runner never branches
/// on what a collector needs.
/// </remarks>
public sealed record CollectContext
{
    /// <summary>The most ids a server manifest may ask about.</summary>
    /// <remarks>
    /// Manifests are server-controlled and read on the framework thread, where the item collector
    /// does per-id armoire and currency lookups on top of its container walk, so an unbounded list
    /// from a broken or hostile backend would freeze the game. The cap is far above any real manifest
    /// (low thousands even for every relic series ever shipped); if one nears it, spread the scan
    /// across frames instead of raising this.
    /// </remarks>
    public const int MaxManifestItems = 5000;

    /// <summary>The most recent <c>/config</c> response, or null when it has not been fetched.</summary>
    public ConfigResponse? RemoteConfig { get; init; }

    /// <summary>
    /// The user's per-group opt-ins, keyed by group key. Consulted only when the config carries
    /// groups; a flat manifest is gated by the category toggle alone.
    /// </summary>
    public IReadOnlySet<string> EnabledItemGroupKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The server's omit-when-unseen ids as a set (see
    /// <see cref="ConfigResponse.ItemOmitWhenUnseenIds"/> for their meaning). Empty without a config
    /// or the field, so nothing is omitted and an id no source saw reports an explicit zero.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="MaxManifestItems"/> like the manifests; past the bound an id is not
    /// omitted, so an unseen one reports an explicit zero. It modifies the item manifest rather than
    /// being one, so it has no truncation warning of its own.
    /// </remarks>
    // Recomputed on each read; the collector reads it once per pass.
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
    /// The server's item manifest groups whose key is in <see cref="EnabledItemGroupKeys"/>, in the
    /// server's order. Empty for a flat manifest or when no group is enabled.
    /// </summary>
    /// <remarks>
    /// Read by the group union manifest, its truncation check, and
    /// <see cref="ManifestConsent.GroupsOfferedButNoneEnabled"/>.
    /// </remarks>
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
    /// The ids the named category's manifest asks about, bounded to <see cref="MaxManifestItems"/>.
    /// Empty when the server sends no manifest for the category or it has none.
    /// </summary>
    /// <remarks>
    /// Looked up through <see cref="ManifestCatalog"/>, so nothing here names a category; a collector
    /// passes its own key.
    /// </remarks>
    public IReadOnlyList<uint> ManifestFor(string categoryKey) =>
        ManifestCatalog.For(categoryKey)?.Read(this) ?? Array.Empty<uint>();

    /// <summary>
    /// The category keys whose manifest the server sent over-long, so its tail is not being
    /// reported. Empty in the ordinary case.
    /// </summary>
    /// <remarks>
    /// Keyed by category, so the orchestrator can report a clip for any manifest the catalog lists.
    /// Clipping is silent where the ids are read, so a bounded scan never depends on anyone noticing;
    /// this is how the orchestrator learns of it.
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
    /// The only item ids the plugin may check possession of, bounded to
    /// <see cref="MaxManifestItems"/>. Empty until the server sends a config.
    /// </summary>
    /// <remarks>
    /// The union of the enabled groups when the server sends groups; otherwise the flat manifest,
    /// gated by the category toggle alone. Reached through <see cref="ManifestFor"/>.
    /// </remarks>
    // Recomputed on each read; collectors read it once per pass.
    internal IReadOnlyList<uint> ReadItemManifest()
    {
        if (RemoteConfig is null)
            return Array.Empty<uint>();

        // An empty group list means the flat manifest, as null does; reading it as "none enabled"
        // would scan nothing. The one-time consent migration (see SyncManager) must read it the same.
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
    /// The quest ids whose journal sequence the server wants reported, bounded to
    /// <see cref="MaxManifestItems"/>. Empty when no config has been fetched or it lacks the field.
    /// </summary>
    /// <remarks>
    /// One walk of the game's active-quest array is far cheaper than the item scans, but the list is
    /// still server input read on the framework thread. It shares the item cap so one number bounds
    /// what a server may ask of a pass. Reached through <see cref="ManifestFor"/>.
    /// </remarks>
    // Recomputed on each read; the collector reads it once per pass.
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
    /// True when <see cref="ReadQuestSequenceManifest"/> clipped the server's list at
    /// <see cref="MaxManifestItems"/>.
    /// </summary>
    /// <remarks>Reported per category through <see cref="TruncatedManifests"/>.</remarks>
    internal bool QuestSequenceManifestTruncated() =>
        RemoteConfig?.QuestSequenceManifest is { } manifest && manifest.Count > MaxManifestItems;

    /// <summary>
    /// True when <see cref="ReadItemManifest"/> clipped the server's ids at
    /// <see cref="MaxManifestItems"/>.
    /// </summary>
    /// <remarks>
    /// Counts as the union does, deduped across enabled groups only, so it is true exactly when ids
    /// were left behind. Reported per category through <see cref="TruncatedManifests"/>.
    /// </remarks>
    internal bool ItemManifestTruncated()
    {
        if (RemoteConfig is null)
            return false;

        // Mirrors ReadItemManifest's branch: groups when the server sent any, flat otherwise.
        if (RemoteConfig.ItemManifestGroups is { Count: > 0 })
        {
            // Stops at the first id past the cap; the answer is settled there.
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
    /// The enabled groups' ids as one manifest, deduped and bounded to <see cref="MaxManifestItems"/>.
    /// </summary>
    /// <remarks>
    /// Two groups may name the same item, such as a seasonal item on several tracks. Each id appears
    /// once, in first-seen order, so nothing is reported twice.
    /// </remarks>
    private IReadOnlyList<uint> GetGroupUnionManifest()
    {
        var result = new List<uint>();
        var seen = new HashSet<uint>();

        foreach (var group in EnabledGroups)
        {
            foreach (var id in group.Ids)
            {
                if (seen.Add(id))
                {
                    result.Add(id);
                    if (result.Count >= MaxManifestItems)
                        return result;
                }
            }
        }

        return result;
    }
}
