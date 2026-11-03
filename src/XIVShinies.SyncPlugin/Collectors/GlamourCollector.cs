using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Glamour;

// Aliased so the item sheet cannot be mistaken for one of the game-memory item types.
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports the gear the local character holds for glamour, and where: the glamour dresser and its
/// outfit glamours, the Armoire, and each other place a copy can be (see <see cref="HeldPlaces"/>).
/// </summary>
/// <remarks>
/// It copies values out for <see cref="GlamourSnapshot.Build"/>, which holds the snapshot's rules,
/// and passes an unread container as null so it stays off the wire (see <see cref="GlamourFacts"/>).
/// Only the local character's own storage is read, its retainers included: a raw retainer id never
/// leaves the process, only its digest (see <see cref="RetainerRoster"/>), and retainer names are
/// sent, as the consent line discloses. The pass skips while a storage window is open, because the
/// item finder updates its copies only when the window closes, so a piece just stored has left the
/// bags but not reached the copy and would read as gone (see <c>IsStorageWindowOpen</c>).
/// </remarks>
// Unsafe because FFXIVClientStructs' Instance() methods return pointers into live game memory.
public sealed unsafe class GlamourCollector : ICollector
{
    // Storage windows whose open state makes a read unsafe (see the class remarks). Retainer
    // windows open only through a summoning bell, so the bell condition stands in for all of them.
    private static readonly string[] StorageWindows =
    {
        // The Glamour Dresser.
        "MiragePrismPrismBox",

        // The outfit glamour window, opened from the dresser; it moves pieces in and out of the
        // dresser too.
        "MiragePrismPrismSetConvert",

        // The Armoire, which the game can re-request while it is open.
        "Cabinet",

        // The chocobo saddlebag.
        "InventoryBuddy",
    };

    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;

    private readonly CategoryInfo info;

    // Built on first use (see StorageSheets).
    private IReadOnlyDictionary<uint, uint[]>? mirageSetIndex;
    private IReadOnlyList<(uint CabinetId, uint ItemId)>? cabinetRows;

    // Gear answers by item id. The item sheet never changes while the game runs, so an answer never
    // goes stale, and a character's held ids repeat from pass to pass.
    private readonly Dictionary<uint, bool> glamourGearAnswers = new();

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and user-facing copy, from the registry, so every category is
    /// described in one file.
    /// </param>
    /// <param name="dataManager">Reads the outfit, Armoire and item sheets.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="gameGui">Used to check whether a storage window is open.</param>
    /// <param name="condition">Used to check whether a summoning bell is in use.</param>
    public GlamourCollector(
        CategoryInfo info,
        IDataManager dataManager,
        IFramework framework,
        IGameGui gameGui,
        ICondition condition)
    {
        this.info = info;
        this.dataManager = dataManager;
        this.framework = framework;
        this.gameGui = gameGui;
        this.condition = condition;
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

    // Ignores the context: no server manifest scopes this collection.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        // Off the framework thread a read races the game's own writes, and the access violation
        // cannot be caught.
        GameThread.EnsureFrameworkThread(framework, nameof(GlamourCollector));

        // Before any read: with a storage window open, the copies may lag the live containers.
        if (IsStorageWindowOpen())
            return CollectResult.Skipped(CollectSkipReasons.StorageWindowOpen);

        // A catch, not a null check (see CollectSkipReasons.SheetUnavailable). Dalamud caches the
        // item sheet, so asking for it each pass is only a lookup.
        IReadOnlyDictionary<uint, uint[]> mirageSetIndex;
        IReadOnlyList<(uint CabinetId, uint ItemId)> cabinetRows;
        ExcelSheet<ItemSheet> itemSheet;
        try
        {
            mirageSetIndex = this.mirageSetIndex ??= StorageSheets.MirageSets(dataManager);
            cabinetRows = this.cabinetRows ??= StorageSheets.CabinetRows(dataManager);
            itemSheet = dataManager.GetExcelSheet<ItemSheet>();
        }
        catch (Exception)
        {
            return CollectResult.Skipped(CollectSkipReasons.SheetUnavailable);
        }

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return CollectResult.Skipped(CollectSkipReasons.InventoryUnavailable);

        // Either may be null. Every StorageSources gate is then false and its note unscanned (see
        // StorageSources.Saddlebag for the one exception).
        var finder = ItemFinderModule.Instance();
        var uiState = UIState.Instance();

        // Null when the held gear cannot be read whole (see ReadHeld). The character is logged in,
        // so this is its own skip, not the log-in hint.
        var held = ReadHeld(inventory, finder);
        if (held is null)
            return CollectResult.Skipped(CollectSkipReasons.StorageUnreadable);

        var roster = ReadRoster(held.RetainerIds, held.MarketRead, held.ListingsRead);

        var dresser = ReadDresser(finder);
        var armoire = ReadArmoire(uiState, cabinetRows);

        // The game always defines Armoire rows, so an empty sheet is a misread that would send a loaded
        // Armoire as empty, every piece gone (CanInterpret guards the dresser the same way).
        if (armoire is not null && cabinetRows.Count == 0)
            return CollectResult.Skipped(CollectSkipReasons.UnexpectedLayout);

        // Still inside the framework update the reads ran in, so each note sees the flag its read
        // gated on (see StorageSources).
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = StorageSources.Inventory(),
            [SourceKeys.Saddlebag] = StorageSources.Saddlebag(finder, uiState),
            [SourceKeys.Retainers] = StorageSources.Retainers(finder),
            [SourceKeys.Armoire] = StorageSources.Armoire(uiState),
            [SourceKeys.GlamourDresser] = StorageSources.GlamourDresser(finder),
        };

        var built = GlamourSnapshot.Build(
            dresser,
            mirageSetIndex,
            armoire,
            held.Slots,
            roster,
            itemId => IsGlamourGear(itemSheet, itemId));

        if (built.WithheldReason is not null)
            return CollectResult.Skipped(built.WithheldReason);

        // A result that is not withheld always carries facts (see GlamourBuildResult).
        return CollectResult.Glamour(built.Facts!, sourceNotes);
    }

    // True when a storage window is open or a summoning bell is in use. Every doubt counts as open:
    // a wrong "open" only delays the read, while a wrong "closed" sends a stale copy.
    private bool IsStorageWindowOpen()
    {
        // Stands in for every retainer window (see StorageWindows).
        if (condition[ConditionFlag.OccupiedSummoningBell])
            return true;

        // Allocated counts as open, drawn or not: the UI may be hidden, and the dresser is undrawn a
        // frame as it opens. The finder commits on undraw, and the window is freed a few frames later.
        foreach (var name in StorageWindows)
        {
            if (!gameGui.GetAddonByName(name).IsNull)
                return true;
        }

        return false;
    }

    // What ReadHeld read. MarketRead and ListingsRead let RetainerRoster.MarketOwner credit the
    // listings; the retainer ids are raw and never leave the process.
    private sealed record HeldReading(
        List<HeldSlot> Slots, List<ulong> RetainerIds, bool MarketRead, int ListingsRead);

    // Every occupied slot that can hold gear, by place. Null when not readable whole, which skips the
    // pass: an unread gear container or retainer is still noted as read, so its pieces would read gone.
    private static HeldReading? ReadHeld(InventoryManager* inventory, ItemFinderModule* finder)
    {
        var slots = new List<HeldSlot>();
        var retainerIds = new List<ulong>();

        // Bags, equipped set and armory chests. In the world all report loaded, even the waist chest no
        // belt can use, so this skip appears only while the inventory loads.
        foreach (var (type, place) in StorageContainers.GearContainers)
        {
            if (!AddLiveContainer(inventory, type, place, slots))
                return null;
        }

        // Ids the copy loaded from disk at login do not count (see
        // StorageSources.IsSaddlebagCached).
        if (StorageSources.IsSaddlebagCached(finder))
        {
            AddStoredSlots(
                finder->SaddleBagItemIds, finder->SaddleBagItemCount, HeldPlaces.Saddlebag, 0, slots);
            AddStoredSlots(
                finder->PremiumSaddleBagItemIds,
                finder->PremiumSaddleBagItemCount,
                HeldPlaces.Saddlebag,
                0,
                slots);
        }

        // One map entry per retainer the item finder remembers. The id only ties each slot to its
        // retainer and is hashed by RetainerRoster; it never leaves the process.
        if (StorageSources.HasRememberedRetainers(finder))
        {
            foreach (var pair in finder->RetainerInventories)
            {
                var retainerId = pair.Item1;
                var retainer = pair.Item2.Value;

                // A retainer that cannot be read or hashed, yet the retainer note counts it (see
                // the method comment).
                if (retainer is null || retainerId == 0)
                    return null;

                retainerIds.Add(retainerId);

                // Stored and equipped gear both count under the retainer's place, equipped at one
                // copy each.
                AddStoredSlots(
                    retainer->ItemIds, retainer->ItemCount, HeldPlaces.Retainer, retainerId, slots);
                foreach (var equippedId in retainer->EquippedItemIds)
                {
                    if (equippedId != 0)
                        slots.Add(new HeldSlot(equippedId, 1, HeldPlaces.Retainer, retainerId));
                }
            }
        }

        // Listings have left the retainer's bags and the finder's copy, so they are seen only here and
        // never twice. An unloaded market means none, not a skip; HeldPlaces.Market says whose they are.
        var slotsBefore = slots.Count;
        var marketRead =
            AddLiveContainer(inventory, InventoryType.RetainerMarket, HeldPlaces.Market, slots);
        var listingsRead = slots.Count - slotsBefore;

        return new HeldReading(slots, retainerIds, marketRead, listingsRead);
    }

    // Names, listing counts and the last selection come from the game's retainer list, loaded at the
    // session's first summoning bell. Before it, read retainers are listed without name or market flag.
    private static RetainerRoster ReadRoster(
        IReadOnlyList<ulong> readRetainerIds, bool marketRead, int listingsRead)
    {
        var names = new Dictionary<ulong, string>();
        var listingCounts = new Dictionary<ulong, int>();
        ulong lastSelected = 0;

        var manager = RetainerManager.Instance();
        if (manager is not null && manager->IsReady)
        {
            // An unused entry has a zero id but can still hold leftover name bytes.
            foreach (ref readonly var entry in manager->Retainers)
            {
                if (entry.RetainerId == 0)
                    continue;

                names[entry.RetainerId] = entry.NameString;
                listingCounts[entry.RetainerId] = entry.MarketItemCount;
            }

            lastSelected = manager->LastSelectedRetainerId;
        }

        var marketOwner = RetainerRoster.MarketOwner(marketRead, listingsRead, lastSelected, listingCounts);
        return RetainerRoster.Build(readRetainerIds, names, marketOwner);
    }

    // False when the container was not read, so the caller can tell unread from read and empty.
    private static bool AddLiveContainer(
        InventoryManager* inventory,
        InventoryType type,
        string place,
        List<HeldSlot> slots)
    {
        var container = inventory->GetInventoryContainer(type);

        // Unallocated or not yet populated: report it unread rather than walk uninitialized memory.
        if (container is null || !container->IsLoaded)
            return false;

        var size = container->GetSize();
        for (var slotIndex = 0; slotIndex < size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);

            // Should not happen, but a bad dereference here would end the process uncatchably.
            if (slot is null)
                continue;

            // A live slot keeps quality in a separate flag, so this is already the base id; held
            // copies sum across qualities. Zero is empty-slot padding.
            var id = slot->ItemId;
            if (id == 0)
                continue;

            // An occupied slot holds at least 1; this stops a torn read wrapping to a huge uint.
            var quantity = slot->Quantity;
            if (quantity <= 0)
                continue;

            slots.Add(new HeldSlot(id, (uint)quantity, place));
        }

        return true;
    }

    // Bounded by the shorter span, so a mismatched pair never overruns. Ids keep the high-quality
    // encoding (see GameItemId) and a zero quantity passes through; GlamourSnapshot handles both.
    private static void AddStoredSlots(
        Span<uint> storedIds,
        Span<ushort> quantities,
        string place,
        ulong retainerId,
        List<HeldSlot> slots)
    {
        for (var slot = 0; slot < storedIds.Length && slot < quantities.Length; slot++)
        {
            if (storedIds[slot] != 0)
                slots.Add(new HeldSlot(storedIds[slot], quantities[slot], place, retainerId));
        }
    }

    // Null unless the finder's copy was refreshed this session (see StorageSources.IsDresserCached).
    // Arrays are copied so the builder holds no memory the game may change or free after this frame.
    private static DresserReading? ReadDresser(ItemFinderModule* finder)
    {
        if (!StorageSources.IsDresserCached(finder))
            return null;

        // The live copy, the only one with dyes, is not always loaded (see DresserReading).
        var mirage = MirageManager.Instance();
        var liveLoaded = mirage is not null && mirage->PrismBoxLoaded;

        // Named, so the five similar arrays cannot be swapped unnoticed.
        return new DresserReading(
            FinderIds: finder->GlamourDresserItemIds.ToArray(),
            FinderBits: finder->GlamourDresserItemSetUnlockBits.ToArray(),
            LiveIds: liveLoaded ? mirage->PrismBoxItemIds.ToArray() : null,
            LiveStain0: liveLoaded ? mirage->PrismBoxStain0Ids.ToArray() : null,
            LiveStain1: liveLoaded ? mirage->PrismBoxStain1Ids.ToArray() : null);
    }

    // The item ids the Armoire holds, or null when the game does not have it loaded.
    private static List<uint>? ReadArmoire(
        UIState* uiState,
        IReadOnlyList<(uint CabinetId, uint ItemId)> cabinetRows)
    {
        // Checked in the same frame as the walk: while the game re-requests the Armoire every row
        // reads "not stored", so such a read is reported as not read, never as an empty Armoire.
        if (!StorageSources.IsArmoireLoaded(uiState))
            return null;

        var armoire = new List<uint>();

        // The game answers by Armoire row, not by item.
        foreach (var (cabinetId, itemId) in cabinetRows)
        {
            if (uiState->Cabinet.IsItemInCabinet(cabinetId))
                armoire.Add(itemId);
        }

        return armoire;
    }

    // Looks the item up on the game's sheets and remembers the answer; the rule is
    // GlamourGear.Qualifies.
    private bool IsGlamourGear(ExcelSheet<ItemSheet> itemSheet, uint itemId)
    {
        if (glamourGearAnswers.TryGetValue(itemId, out var known))
            return known;

        var row = itemSheet.GetRowOrDefault(itemId);

        // An item with no row keeps these: it equips nowhere, and its soul crystal is unread.
        uint slotRowId = 0;
        sbyte? soulCrystal = null;

        if (row is not null)
        {
            var slotCategory = row.Value.EquipSlotCategory;
            slotRowId = slotCategory.RowId;
            soulCrystal = slotCategory.ValueNullable?.SoulCrystal;
        }

        var isGear = GlamourGear.Qualifies(
            itemRowFound: row is not null,
            equipSlotCategoryRowId: slotRowId,
            soulCrystal: soulCrystal);

        glamourGearAnswers[itemId] = isGear;
        return isGear;
    }
}
