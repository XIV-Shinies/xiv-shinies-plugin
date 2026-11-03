using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports how many of the server's requested items the local character possesses.
/// </summary>
/// <remarks>
/// A count proves a relic step on the server and, for items tracked by number, feeds a running total;
/// a stale positive still proves a step after the item is consumed, so caches count too, reported as
/// not fresh. Live containers and the cache-backed sources, readable once the player opens them, are
/// summed, so copies parked on a retainer count. Only the local player's own storage is read and this
/// collector never touches a retainer id; it shares its read gates and source notes with
/// <see cref="GlamourCollector"/> through <see cref="StorageSources"/>.
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

    // Walked once per pass; order is cosmetic. Every armory chest is listed, soul crystals included, so
    // no manifest item hides in a skipped one. StorageContainers says what is shared and never walked.
    private static readonly InventoryType[] LiveContainers =
    [
        .. StorageContainers.GearContainers.Select(container => container.Type),
        InventoryType.ArmorySoulCrystal,
        InventoryType.Crystals,
        InventoryType.Currency,
    ];

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
    public bool RequiresServerSupport => info.RequiresServerSupport;

    /// <inheritdoc/>
    public bool IsSingleRecord => info.IsSingleRecord;

    /// <inheritdoc/>
    public bool ReadsStorage => info.ReadsStorage;

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
        sourceNotes[SourceKeys.Inventory] = StorageSources.Inventory();
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

            ItemTallies.Add(
                live,
                id,
                ItemTally.ForQuality((uint)quantity, slot->IsHighQuality(), slot->IsCollectable()));
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

            ItemTallies.Add(
                live, id, ItemTally.ForQuality(count, isHighQuality: false, isCollectable: false));
        }
    }

    // Each cache-backed source contributes only once the player has opened it, and its note says
    // whether it was read or never opened. StorageSources holds each gate and the note built on it.
    private void BuildCachedTallies(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached,
        Dictionary<string, ItemSourceStatus> notes)
    {
        var uiState = UIState.Instance();
        notes[SourceKeys.Armoire] = StorageSources.Armoire(uiState);
        if (StorageSources.IsArmoireLoaded(uiState))
        {
            // The armoire holds a single copy of each item, so a match counts 1.
            foreach (var id in manifestIds)
            {
                if (id != 0 && IsStoredInArmoire(id))
                {
                    ItemTallies.Add(
                        cached, id, ItemTally.ForQuality(1, isHighQuality: false, isCollectable: false));
                }
            }
        }

        var finder = ItemFinderModule.Instance();
        if (finder is null)
        {
            // Nothing is readable, so each note is unscanned, except as StorageSources.Saddlebag says.
            notes[SourceKeys.Saddlebag] = StorageSources.Saddlebag(null, uiState);
            notes[SourceKeys.Retainers] = StorageSources.Retainers(null);
            notes[SourceKeys.GlamourDresser] = StorageSources.GlamourDresser(null);
            return;
        }

        if (StorageSources.IsSaddlebagCached(finder))
        {
            TallySlots(finder->SaddleBagItemIds, finder->SaddleBagItemCount, manifestIds, cached);
            TallySlots(finder->PremiumSaddleBagItemIds, finder->PremiumSaddleBagItemCount, manifestIds, cached);
        }

        notes[SourceKeys.Saddlebag] = StorageSources.Saddlebag(finder, uiState);

        // The outfit index is resolved before the walk, so the walk always has a real one; an unreadable
        // sheet throws instead (see GetMirageSetIndex).
        if (StorageSources.IsDresserCached(finder))
            TallyGlamourDresser(finder, GetMirageSetIndex(), manifestIds, cached);

        notes[SourceKeys.GlamourDresser] = StorageSources.GlamourDresser(finder);

        // The gate reads only the map's count, never its keys. The note says "3 of 5": remembered
        // retainers of the total (see StorageSources.Retainers).
        if (StorageSources.HasRememberedRetainers(finder))
            TallyRetainers(finder, manifestIds, cached);

        notes[SourceKeys.Retainers] = StorageSources.Retainers(finder);
    }

    /// <summary>Adds every occupied slot of the player's own retainers to the cached tally.</summary>
    /// <remarks>
    /// Only the map's values are read; its keys are retainer ids, which item counts have no use for.
    /// <c>ItemFinderModule</c> persists this cache to a local file, so contents can appear after login
    /// without a summon and may be stale; a never-visited retainer is simply absent.
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
            // An entry with no inventory adds nothing yet counts as remembered, so only its copies go
            // missing; the glamour category skips the whole pass instead (see GlamourCollector.ReadHeld).
            var retainer = inventoryPointer.Value;
            if (retainer is null)
                continue;

            TallySlots(retainer->ItemIds, retainer->ItemCount, manifestIds, cached);

            // Equipped gear has no counts: one each.
            foreach (var equippedId in retainer->EquippedItemIds)
                ItemTallies.AddStoredId(cached, manifestIds, equippedId, 1);
        }
    }

    /// <summary>
    /// Adds every slot whose id is in the manifest to the cached tally, reading a parallel (item id,
    /// count) array pair: the saddlebags and each retainer's bags.
    /// </summary>
    // Bounded by the shorter span, so a mismatched pair never reads out of bounds. AddStoredId reads
    // the high-quality encoding these copies use and decides whether a slot counts.
    private static void TallySlots(
        Span<uint> itemIds,
        Span<ushort> counts,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        for (var slot = 0; slot < itemIds.Length && slot < counts.Length; slot++)
            ItemTallies.AddStoredId(cached, manifestIds, itemIds[slot], counts[slot]);
    }

    /// <summary>
    /// Adds every manifest-matching glamour-dresser slot to the cached tally, expanding stored
    /// outfits into their pieces.
    /// </summary>
    /// <remarks>
    /// The dresser pairs each slot's stored id with its outfit unlock bits, and
    /// <see cref="ItemTallies.AddDresserSlot"/> holds the rules for a slot. The bits come from
    /// <c>ItemFinderModule</c>, because <c>MirageManager.IsSetSlotUnlocked</c> clears its data on a
    /// zone change.
    /// </remarks>
    private static void TallyGlamourDresser(
        ItemFinderModule* finder,
        IReadOnlyDictionary<uint, uint[]> mirageSetIndex,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        // Bounded by the shorter array, so a length mismatch never reads past it.
        var ids = finder->GlamourDresserItemIds;
        var bits = finder->GlamourDresserItemSetUnlockBits;

        for (var slot = 0; slot < ids.Length && slot < bits.Length; slot++)
            ItemTallies.AddDresserSlot(cached, manifestIds, mirageSetIndex, ids[slot], bits[slot]);
    }

    private bool IsStoredInArmoire(uint itemId)
    {
        var uiState = UIState.Instance();

        // While the armoire is not loaded the game would answer a confident "no". This is the gate the
        // armoire's note is built on, so the two agree.
        if (!StorageSources.IsArmoireLoaded(uiState))
            return false;

        // An unreadable sheet throws, so the field only ever holds a real index.
        armoireIndex ??= BuildArmoireIndex();

        // The game answers by armoire row, not by item, hence the lookup.
        return armoireIndex.TryGetValue(itemId, out var cabinetId)
               && uiState->Cabinet.IsItemInCabinet(cabinetId);
    }

    /// <remarks>
    /// Lets an unreadable sheet throw from <see cref="StorageSheets.CabinetRows"/>:
    /// <see cref="CollectorRunner"/> then omits the whole <c>items</c> category, which the server reads
    /// as "not read this time". Catching it would send counts beside an Armoire note already set to
    /// <c>Loaded</c>, claiming a scan that never happened.
    /// </remarks>
    private IReadOnlyDictionary<uint, uint> BuildArmoireIndex() =>
        ArmoireIndex.Build(StorageSheets.CabinetRows(dataManager));

    /// <summary>The outfit index, built once (see <see cref="StorageSheets"/>).</summary>
    /// <remarks>
    /// Throws on an unreadable sheet, from <see cref="StorageSheets.MirageSets"/>: an omitted category
    /// is honest, and counts that silently skip every stored outfit are not.
    /// </remarks>
    private IReadOnlyDictionary<uint, uint[]> GetMirageSetIndex() =>
        mirageSetIndex ??= StorageSheets.MirageSets(dataManager);
}
