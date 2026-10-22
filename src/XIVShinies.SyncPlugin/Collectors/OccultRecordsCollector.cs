using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the IDs of every occult record the local player has discovered.
/// </summary>
/// <remarks>
/// The source, <c>MKDLoreModule.SeenLore</c>, is the character's own client-persisted file of
/// <c>MKDLore</c> row ids in first-seen order: readable anywhere, holding nothing about other
/// players, and appended on every discovery, so it is the complete seen-set (see
/// <see cref="CategoryInfo.EnumeratesCompleteDomain"/> and
/// <see cref="CollectResult.CompleteEnumeration"/>). Both occult zones share this id space, which the
/// server stores directly. Each id is one byte, so an <c>MKDLore</c> sheet grown past 255 rows would
/// need this collector revisited.
/// </remarks>
// Unsafe because Instance() returns a pointer into game memory, so each access below is guarded.
public sealed unsafe class OccultRecordsCollector : ICollector
{
    private readonly IFramework framework;

    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">The category's wire key and its user-facing copy.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public OccultRecordsCollector(CategoryInfo info, IFramework framework)
    {
        this.info = info;
        this.framework = framework;
    }

    /// <inheritdoc/>
    public string CategoryKey => info.Key;

    /// <inheritdoc/>
    public string DisplayName => info.DisplayName;

    /// <inheritdoc/>
    public string Section => info.Section;

    /// <inheritdoc/>
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public string? Details => info.Details;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    // Ignores the context: no server manifest scopes this collection.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(OccultRecordsCollector));

        var lore = MKDLoreModule.Instance();
        if (lore == null)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        // Count is computed from game-memory pointers, so it is read once and bounded first: a
        // corrupt vector header could otherwise send the loop through arbitrary memory.
        var count = lore->SeenLore.Count;
        if (count is < 0 or > PayloadCaps.MaxIdsPerCategory)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        var ids = new List<uint>((int)count);

        // A long index because StdVector, the mirror of a C++ std::vector, sizes in native widths.
        for (long i = 0; i < count; i++)
            ids.Add(lore->SeenLore[i]);

        // The completeness declaration is the category's.
        return CollectResult.Ids(ids, info.EnumeratesCompleteDomain);
    }
}
