using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XIVShinies.SyncPlugin.Api;

// "Cabinet" names both the armoire's memory struct and this sheet of what the armoire can hold.
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

// Every glamour-dresser outfit and the pieces inside it.
using MirageSetSheet = Lumina.Excel.Sheets.MirageStoreSetItem;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports how many of the server's requested items the local character possesses.
/// </summary>
/// <remarks>
/// A count proves a relic step on the server and, for items tracked by number, feeds a running total;
/// a stale positive still proves a step after the item is consumed, so caches count too, reported as
/// not fresh. Live containers and the cache-backed sources, readable once the player opens them, are
/// summed, so copies parked on a retainer count. Only the local player's own storage is read, and no
/// retainer id leaves the process.
/// </remarks>
// Unsafe: FFXIVClientStructs' Instance() methods return pointers into live game memory.
public sealed unsafe class ItemCollector : ICollector
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    // Built on first use; the sheet never changes while the game runs.
    private IReadOnlyDictionary<uint, uint>? armoireIndex;

    // An outfit's set id to the pieces inside it, built on first use (see TallyGlamourDresser).
    private IReadOnlyDictionary<uint, uint[]>? mirageSetIndex;

    private readonly CategoryInfo info;

    // Walked once per pass; the order is cosmetic. Every armoury chest is listed so nothing the
    // manifest asks about, such as a soul crystal, hides in a skipped one. ArmoryFeets and ArmoryEar
    // are FFXIVClientStructs' own spellings.
    private static readonly InventoryType[] LiveContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
        InventoryType.Crystals,
        InventoryType.Currency,
    };

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and user-facing copy, from the registry, so every category is described
    /// in one file.
    /// </param>
    /// <param name="dataManager">Reads the armoire and glamour dresser outfit sheets.</param>
    /// <param name="framework">Checks that reads run on the framework thread.</param>
    public ItemCollector(CategoryInfo info, IDataManager dataManager, IFramework framework)
    {
        this.info = info;
        this.dataManager = dataManager;
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

    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(ItemCollector));

        // Without a config, which items matter is unknown: "could not read", not "found nothing".
        if (context.RemoteConfig is null)
            return CollectResult.Skipped(CollectSkipReasons.NoRemoteConfig);

        // Every offered group is off, so nothing is looked at; empty facts would read as "checked, and
        // you own none of these". The rule lives in ManifestConsent.
        if (ManifestConsent.GroupsOfferedButNoneEnabled(context))
            return CollectResult.Skipped(CollectSkipReasons.NoItemGroupsEnabled);

        // Read once: ManifestFor recomputes on every call, and the scan needs one stable list.
        var manifest = context.ManifestFor(CategoryKeys.Items);

        // An empty manifest is a real answer. No source notes: no container was walked, so a note
        // would describe a scan that never happened.
        if (manifest.Count == 0)
            return CollectResult.Items(Array.Empty<ItemPossession>());

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return CollectResult.Skipped(CollectSkipReasons.InventoryUnavailable);

        // One set lookup per slot. Duplicate manifest ids collapse, so the armoire's single copy never
        // counts twice; counts still sum across sources.
        var manifestIds = new HashSet<uint>(manifest);

        // What the live containers hold and what the caches remember, combined per quality at the end.
        var live = new Dictionary<uint, ItemTally>();
        var cached = new Dictionary<uint, ItemTally>();

        // Built from the same flags the scan reads, so a note always matches what was consulted.
        var sourceNotes = new Dictionary<string, ItemSourceStatus>();

        foreach (var type in LiveContainers)
            TallyLiveContainer(inventory, type, manifestIds, live);

        // Currencies kept outside any container; see TallyCurrencyFallback.
        TallyCurrencyFallback(manifestIds, live);

        // Every live container was walked, so both sources are live. Currencies are reported apart but
        // share the state: the fallback reads the same current memory, whether or not it ran.
        sourceNotes[SourceKeys.Inventory] = new ItemSourceStatus { State = SourceStates.Live };
        sourceNotes[SourceKeys.Currencies] = new ItemSourceStatus { State = SourceStates.Live };

        BuildCachedTallies(manifestIds, cached, sourceNotes);

        // Lets the panel say why displayed gear never counts; the payload builder drops it from
        // uploads. Last, because the panel lists chips in report order and this one never contributes.
        sourceNotes[SourceKeys.Mannequins] = new ItemSourceStatus { State = SourceStates.Unreadable };

        // BuildPossessions applies the explicit-zero, omit-when-unseen and freshness rules. Keep the
        // named arguments (see its remarks).
        return CollectResult.Items(
            ItemTallies.BuildPossessions(
                manifest: manifest,
                live: live,
                cached: cached,
                omitWhenUnseen: context.ItemOmitWhenUnseenIds),
            sourceNotes);
    }

    private static void TallyLiveContainer(
        InventoryManager* inventory,
        InventoryType type,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> live)
    {
        var container = inventory->GetInventoryContainer(type);

        // Unallocated or not yet populated: skip rather than walk uninitialized memory.
        if (container is null || !container->IsLoaded)
            return;

        // One walk costs a few hundred slots whatever the manifest's size. GetInventoryItemCount per id
        // would re-walk every container for each id.
        var size = container->GetSize();
        for (var slotIndex = 0; slotIndex < size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);

            // A null slot should not happen, but a bad dereference here would end the process uncatchably.
            if (slot is null)
                continue;

            // The base id, not GetItemId(), whose virtual form adds the high-quality offset; the
            // manifest lists base ids. Quality is read separately.
            var id = slot->ItemId;

            // Id 0 is empty-slot padding; tested first so a manifest listing 0 cannot match it.
            if (id == 0 || !manifestIds.Contains(id))
                continue;

            // An occupied slot holds at least 1; this keeps a torn read from wrapping to a huge uint.
            var quantity = slot->Quantity;
            if (quantity <= 0)
                continue;

            AccumulateLive(live, id, (uint)quantity, slot->IsHighQuality(), slot->IsCollectable());
        }
    }

    /// <summary>
    /// Counts, from <c>CurrencyManager</c>, the manifest ids the container walk missed: currencies kept
    /// outside any container, such as scrips, Bicolor Gemstones and ventures.
    /// </summary>
    /// <remarks>
    /// Which ids live there is the game's choice, so this asks about every id the walk did not find; one
    /// it found is skipped, since counting it again would double it. An id found nowhere stays absent
    /// for <c>BuildPossessions</c> to report.
    /// </remarks>
    private static void TallyCurrencyFallback(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> live)
    {
        // Unavailable this pass: keep what the walk already found.
        var currency = CurrencyManager.Instance();
        if (currency is null)
            return;

        // Bounded by the manifest, not by every currency the game knows.
        foreach (var id in manifestIds)
        {
            if (id == 0 || live.ContainsKey(id))
                continue;

            // GetItemCount's behavior for an id in no bucket is undocumented, so ask HasItem first.
            if (!currency->HasItem(id))
                continue;

            // Currencies carry no quality. A zero is skipped: the explicit zero covers it, and for an
            // omit-when-unseen id the omission is equivalent, since the server drops zeros for those ids.
            var count = currency->GetItemCount(id);
            if (count == 0)
                continue;

            AccumulateNq(live, id, count);
        }
    }

    // Each cache-backed source contributes only once the player has opened it, and its note says
    // whether it was read or never opened.
    private void BuildCachedTallies(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached,
        Dictionary<string, ItemSourceStatus> notes)
    {
        // The armoire comes from UIState, not ItemFinderModule. The game fetches it on the first open
        // each session and cannot answer before then.
        var uiState = UIState.Instance();
        var cabinetLoaded = uiState is not null && uiState->Cabinet.IsCabinetLoaded();
        notes[SourceKeys.Armoire] = new ItemSourceStatus
        {
            State = cabinetLoaded ? SourceStates.Loaded : SourceStates.Unscanned,
        };
        if (cabinetLoaded)
        {
            // The armoire holds a single copy of each item, so a match counts 1.
            foreach (var id in manifestIds)
            {
                if (id != 0 && IsStoredInArmoire(id))
                    AccumulateNq(cached, id, 1);
            }
        }

        var finder = ItemFinderModule.Instance();
        if (finder is null)
        {
            // Unscanned is the honest status for "not read", not an empty but current read.
            notes[SourceKeys.Saddlebag] = new ItemSourceStatus { State = SourceStates.Unscanned };
            notes[SourceKeys.Retainers] = new ItemSourceStatus { State = SourceStates.Unscanned };
            notes[SourceKeys.GlamourDresser] = new ItemSourceStatus { State = SourceStates.Unscanned };
            return;
        }

        if (finder->IsSaddleBagCached)
        {
            TallySlots(finder->SaddleBagItemIds, finder->SaddleBagItemCount, manifestIds, cached);
            TallySlots(finder->PremiumSaddleBagItemIds, finder->PremiumSaddleBagItemCount, manifestIds, cached);
            notes[SourceKeys.Saddlebag] = new ItemSourceStatus { State = SourceStates.Cached };
        }
        else
        {
            notes[SourceKeys.Saddlebag] = new ItemSourceStatus { State = SourceStates.Unscanned };
        }

        // The outfit index is resolved before the walk, so the walk always has a real one; an unreadable
        // sheet throws instead (see BuildMirageSetIndex).
        if (finder->IsGlamourDresserCached)
        {
            TallyGlamourDresser(finder, GetMirageSetIndex(), manifestIds, cached);
            notes[SourceKeys.GlamourDresser] = new ItemSourceStatus { State = SourceStates.Cached };
        }
        else
        {
            notes[SourceKeys.GlamourDresser] = new ItemSourceStatus { State = SourceStates.Unscanned };
        }

        // The note says "3 of 5": cached retainers of the total. Neither read touches a retainer id or
        // name. The manager reports 0 until the game fills it, which reads as owning none, so 0 omits it.
        var retainerManager = RetainerManager.Instance();
        var knownTotal = retainerManager is not null ? (int)retainerManager->GetRetainerCount() : 0;
        int? retainerTotal = knownTotal > 0 ? knownTotal : null;

        var retainerCount = finder->RetainerInventories.Count;
        if (retainerCount > 0)
        {
            TallyRetainers(finder, manifestIds, cached);
            notes[SourceKeys.Retainers] = new ItemSourceStatus
            {
                State = SourceStates.Cached,
                Count = retainerCount,
                Total = retainerTotal,
            };
        }
        else
        {
            notes[SourceKeys.Retainers] = new ItemSourceStatus
            {
                State = SourceStates.Unscanned,
                Total = retainerTotal,
            };
        }
    }

    /// <summary>Adds every occupied slot of the player's own retainers to the cached tally.</summary>
    /// <remarks>
    /// Only the map's values are read: its keys are retainer ids, which are never touched and never
    /// leave the process. <c>ItemFinderModule</c> persists this cache to a local file, so contents can
    /// appear after login without a summon and may be stale; a never-visited retainer is simply absent.
    /// <c>IsRetainerCurrent</c> would tell a fresh read from the file, but it needs the keys, and both
    /// report as not fresh anyway.
    /// </remarks>
    private static void TallyRetainers(
        ItemFinderModule* finder,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        // Values only: the keys are retainer ids.
        foreach (var inventoryPointer in finder->RetainerInventories.Values)
        {
            var retainer = inventoryPointer.Value;
            if (retainer is null)
                continue;

            TallySlots(retainer->ItemIds, retainer->ItemCount, manifestIds, cached);

            // Equipped gear has no counts: one each.
            foreach (var equippedId in retainer->EquippedItemIds)
            {
                if (equippedId != 0 && manifestIds.Contains(equippedId))
                    AccumulateNq(cached, equippedId, 1);
            }
        }
    }

    /// <summary>
    /// Adds every slot whose id is in the manifest to the cached tally, reading a parallel (item id,
    /// count) array pair: the saddlebags and each retainer's bags.
    /// </summary>
    // Bounded by the shorter span, so a mismatched pair never reads out of bounds.
    private static void TallySlots(
        Span<uint> itemIds,
        Span<ushort> counts,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        for (var slot = 0; slot < itemIds.Length && slot < counts.Length; slot++)
        {
            var id = itemIds[slot];
            if (id == 0 || !manifestIds.Contains(id))
                continue;

            AccumulateNq(cached, id, counts[slot]);
        }
    }

    /// <summary>
    /// Adds every manifest-matching glamour-dresser slot to the cached tally, expanding stored
    /// outfits into their pieces.
    /// </summary>
    /// <remarks>
    /// A slot holds an item's id or an outfit's set id (a <c>MirageStoreSetItem</c> row), and a paired
    /// array holds each slot's unlock bits: <paramref name="setIndex"/> maps a set id to its pieces, and
    /// the bits say which are stored now. The bits come from <c>ItemFinderModule</c>, because
    /// <c>MirageManager.IsSetSlotUnlocked</c> clears its data on a zone change. Each stored piece counts
    /// one copy, and a manifest listing both a set id and its pieces counts both.
    /// </remarks>
    private static void TallyGlamourDresser(
        ItemFinderModule* finder,
        IReadOnlyDictionary<uint, uint[]> setIndex,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        // Bounded by the shorter array, so a length mismatch never reads past it.
        var ids = finder->GlamourDresserItemIds;
        var bits = finder->GlamourDresserItemSetUnlockBits;

        for (var slot = 0; slot < ids.Length && slot < bits.Length; slot++)
        {
            var storedId = ids[slot];
            if (storedId == 0)
                continue;

            // An individually stored item, or an outfit's set id that the manifest lists itself.
            if (manifestIds.Contains(storedId))
                AccumulateNq(cached, storedId, 1);

            if (setIndex.TryGetValue(storedId, out var pieces))
            {
                foreach (var pieceId in MirageSetIndex.StoredPieces(pieces, bits[slot]))
                {
                    if (manifestIds.Contains(pieceId))
                        AccumulateNq(cached, pieceId, 1);
                }
            }
        }
    }

    private static void AccumulateLive(
        Dictionary<uint, ItemTally> tallies,
        uint id,
        uint quantity,
        bool isHighQuality,
        bool isCollectable)
    {
        // Collectable first: it is its own quality, distinct from high quality. Each quantity lands in
        // one bucket, and the buckets travel apart because the server applies different rules to each.
        ItemTally delta;
        if (isCollectable)
            delta = new ItemTally(Nq: 0, Hq: 0, Collectable: quantity);
        else if (isHighQuality)
            delta = new ItemTally(Nq: 0, Hq: quantity, Collectable: 0);
        else
            delta = new ItemTally(Nq: quantity, Hq: 0, Collectable: 0);

        AddTally(tallies, id, delta);
    }

    // The caches expose ids and counts but no quality flags, so every cached count is normal quality.
    private static void AccumulateNq(Dictionary<uint, ItemTally> tallies, uint id, uint quantity) =>
        AddTally(tallies, id, new ItemTally(Nq: quantity, Hq: 0, Collectable: 0));

    // One item can fill several slots and containers; an absent id starts from the all-zero tally.
    private static void AddTally(Dictionary<uint, ItemTally> tallies, uint id, ItemTally delta)
    {
        tallies.TryGetValue(id, out var existing);
        tallies[id] = existing.Add(delta);
    }

    private bool IsStoredInArmoire(uint itemId)
    {
        var uiState = UIState.Instance();
        if (uiState is null)
            return false;

        // Before the first open the game does not know the contents, and would answer a confident "no".
        if (!uiState->Cabinet.IsCabinetLoaded())
            return false;

        // An unreadable sheet throws, so the field only ever holds a real index.
        armoireIndex ??= BuildArmoireIndex();

        // The game answers by armoire row, not by item, hence the lookup.
        return armoireIndex.TryGetValue(itemId, out var cabinetId)
               && uiState->Cabinet.IsItemInCabinet(cabinetId);
    }

    /// <remarks>
    /// Lets an unreadable sheet throw: <see cref="CollectorRunner"/> then omits the whole
    /// <c>items</c> category, which the server reads as "not read this time". Catching it would
    /// send counts beside an Armoire note already set to <c>Loaded</c>, claiming a scan that never
    /// happened.
    /// </remarks>
    private IReadOnlyDictionary<uint, uint> BuildArmoireIndex()
    {
        var sheet = dataManager.GetExcelSheet<CabinetSheet>();

        var rows = new List<(uint CabinetId, uint ItemId)>();
        foreach (var row in sheet)
            rows.Add((row.RowId, row.Item.RowId));

        return ArmoireIndex.Build(rows);
    }

    private IReadOnlyDictionary<uint, uint[]> GetMirageSetIndex() =>
        mirageSetIndex ??= BuildMirageSetIndex();

    /// <remarks>
    /// Throws on an unreadable sheet: an omitted category is honest, and counts that silently skip
    /// every stored outfit are not.
    /// </remarks>
    private IReadOnlyDictionary<uint, uint[]> BuildMirageSetIndex()
    {
        var sheet = dataManager.GetExcelSheet<MirageSetSheet>();

        var rows = new List<(uint SetItemId, uint[] PieceItemIds)>();
        foreach (var row in sheet)
        {
            // The sheet's column order is load-bearing: a stored outfit's unlock bit i refers to piece
            // i. An empty column reads id 0 and keeps its place; StoredPieces drops it, Build does not.
            var pieces = new uint[]
            {
                row.MainHand.RowId,
                row.OffHand.RowId,
                row.Head.RowId,
                row.Body.RowId,
                row.Hands.RowId,
                row.Legs.RowId,
                row.Feet.RowId,
                row.Earrings.RowId,
                row.Necklace.RowId,
                row.Bracelets.RowId,
                row.Ring.RowId,
            };
            rows.Add((row.RowId, pieces));
        }

        return MirageSetIndex.Build(rows);
    }
}
