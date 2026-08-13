using System.Collections.Generic;
using Dalamud.Plugin.Services;
// MKDLoreModule, the client-persisted file holding which occult records have been seen.
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the IDs of every occult record the local player has discovered.
/// </summary>
/// <remarks>
/// <para>
/// The source is <c>MKDLoreModule.SeenLore</c> — a client-persisted per-character file whose
/// bytes are <c>MKDLore</c> sheet row ids in first-seen order. Being a persisted client file
/// rather than instance state, it is readable anywhere, any time: this collector needs no
/// occult visit and no preconditions.
/// </para>
/// <para>
/// The list IS the character's complete seen-set — the game appends every discovery to it —
/// so this collector declares the enumeration complete (see
/// <see cref="CollectResult.CompleteEnumeration"/> for what the declaration licenses). Both
/// occult zones share the one id space, and the server stores the same <c>MKDLore</c> row ids
/// directly. The elements are single bytes, so no row id above 255 can ever be recorded;
/// <c>MKDLore</c> has 60 rows today, and a sheet grown past 255 would need this collector
/// revisited.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and
/// is verified by in-game QA. The module belongs to the local player's own save data; nothing
/// about any other player exists in it.
/// </para>
/// </remarks>
// `unsafe` because the module is reached through raw pointers (Instance() returns a C++
// object's address) — C#'s references and bounds checks do not apply, so every access below
// is guarded by hand.
public sealed unsafe class OccultRecordsCollector : ICollector
{
    private readonly IFramework framework;

    // How this collection names and describes itself to the user.
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
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public string? Details => info.Details;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    // This collector needs nothing from the context: the seen-set is self-contained game data,
    // scoped by no server manifest.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(OccultRecordsCollector));

        var lore = MKDLoreModule.Instance();
        if (lore == null)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        // The vector's Count is a game-memory value (a pointer subtraction, not a managed
        // property), so it is read once and sanity-bounded before anything iterates by it: a
        // corrupt vector header could otherwise send the loop walking arbitrary memory.
        var count = lore->SeenLore.Count;
        if (count is < 0 or > PayloadCaps.MaxIdsPerCategory)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        var ids = new List<uint>((int)count);

        // A `long` index because StdVector (a mirrored C++ std::vector) sizes itself in native
        // widths; each element is a single byte holding an MKDLore row id, widened to the uint
        // the id-list payload carries.
        for (long i = 0; i < count; i++)
            ids.Add(lore->SeenLore[i]);

        return CollectResult.Ids(ids, completeEnumeration: true);
    }
}
