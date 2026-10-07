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

// The game's item sheet: one row per item, saying among other things where it can be equipped.
// Aliased so it reads as a sheet and cannot be mistaken for one of the game-memory item types.
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports the gear the local character holds for glamour, and where: the glamour dresser's loose
/// pieces and outfit glamours, the Armoire, and the gear held in bags, equipped, the armory chest,
/// the saddlebag, each retainer and one retainer's market listings, with how many copies each
/// place holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Current holdings, so "not read" and "read, empty" are different facts</b> (see
/// <see cref="GlamourFacts"/>). A container this pass could not read is handed to
/// <see cref="GlamourSnapshot.Build"/> as null and stays off the wire. The held list is a union of
/// several containers and has no "not read" state of its own, so the source notes that travel beside
/// the facts, together with the facts' own retainers list, tell the server which places fed it.
/// </para>
/// <para>
/// <b>The local player's own storage only.</b> Every container read here belongs to the local
/// character, and so does every retainer. The retainers' ids, the keys of the item finder's
/// retainer map, are read to tie each held copy to its retainer, and are hashed by
/// <see cref="RetainerRoster"/> before anything is sent: the raw ids never leave the process. The
/// retainers' names are read from the game's own retainer list and sent, as the consent line
/// discloses. No other character's data is touched.
/// </para>
/// <para>
/// <b>Where each container is read from.</b> Bags, equipped gear and the armory chest are read live
/// (<see cref="StorageContainers"/> lists them); one that is not loaded makes the whole pass a skip
/// rather than an empty read (<c>ReadHeld</c> says why).
/// </para>
/// <para>
/// The saddlebag and the retainers are read from the game's item finder copies, never from their
/// live containers (<see cref="StorageContainers"/> says why), with one exception: the live market
/// container, which holds one retainer's listings, normally the last summoned one's. The dresser
/// is read from the item finder's copy, with dyes taken from the game's live dresser copy when it
/// is loaded. The Armoire is read through <c>UIState</c> while the game has it loaded. The
/// saddlebag, retainer, dresser and Armoire reads are each gated by a
/// <see cref="StorageSources"/> predicate, which also documents when an item finder copy counts as
/// read this session. The market listings count only when they can be credited to a retainer (see
/// <see cref="RetainerRoster.MarketOwner"/>).
/// </para>
/// <para>
/// <b>Skipped while a storage window is open.</b> The live containers change in the same frame as a
/// move, but the item finder's copies of the dresser, the saddlebag and the retainers change only
/// when their window closes, and the Armoire, read through <c>UIState</c>, can be re-requested
/// while its window is open. With a window open, a piece just moved into the dresser has already left the
/// bags but not yet reached the copy read here, and the pass would report a piece the character
/// still holds as gone. So the whole category waits for the window to close
/// (<see cref="CollectSkipReasons.StorageWindowOpen"/>), and the settings window tells the player
/// which windows to close. A summoning bell in use waits the same way, standing in for every
/// retainer window (see <c>StorageWindows</c>).
/// </para>
/// <para>
/// A window counts as open for as long as the game has it allocated, whether or not it is drawn.
/// While opening, the Glamour Dresser reads as allocated but not visible for a frame; while closing,
/// every storage window stays allocated for a few frames after it stops being drawn, and is then
/// freed. The item finder commits its copy on the frame the window stops being drawn, so waiting
/// for the window to be freed costs a few frames and nothing else. Hiding the game UI does not
/// change any of this: an open window stays allocated and reports itself the same way.
/// </para>
/// <para>
/// The two possible mistakes are not equal, which is why every doubt counts as a window open: a
/// window wrongly taken for open only delays the read to a later pass, while one wrongly taken for
/// closed sends a stale copy, and a piece just stored would read as removed.
/// </para>
/// <para>
/// <b>Withheld, never truncated or clamped.</b> A list, a held count or the number of retainers
/// past the server's ceiling sends no glamour facts at all (<see cref="CollectSkipReasons.OverCap"/>);
/// <see cref="GlamourSnapshot"/>'s class remarks say why.
/// </para>
/// <para>
/// <b>Source notes shared with the items category.</b> <see cref="ItemCollector"/> reports scan
/// states for the same storage sources. Both collectors gate their reads and build those notes
/// through <see cref="StorageSources"/> (which says why the two must share it), walk the same
/// containers from <see cref="StorageContainers"/>, and read the same sheets through
/// <see cref="StorageSheets"/>.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and is
/// verified by in-game QA rather than by unit tests. It only copies values out of the game and hands
/// them over; the rules live in pure, tested classes: <see cref="GlamourSnapshot"/> (assembling the
/// facts), <see cref="GlamourGear"/> (which held items are gear), <see cref="GameItemId"/> (the
/// high-quality encoding), <see cref="MirageSetIndex"/> (which of an outfit's pieces are stored)
/// and <see cref="RetainerRoster"/> (which retainers are listed, their keys and whose listings were
/// read).
/// </para>
/// </remarks>
// `unsafe` allows raw pointers. FFXIVClientStructs maps the game's own memory layout, so its
// Instance() methods hand back pointers into the live game rather than managed objects. C# normally
// forbids this; the keyword is the explicit opt-in. `T*` is "a pointer to a T", and `->` reads a
// member through a pointer, the pointer version of `.`. There is no JS equivalent whatsoever.
public sealed unsafe class GlamourCollector : ICollector
{
    // The game's internal names for the storage windows whose open state makes a read unsafe (see
    // the class remarks). A retainer's windows are not listed: they open only through a summoning
    // bell, and the bell condition checked beside this list covers all of them at once.
    private static readonly string[] StorageWindows =
    {
        // The Glamour Dresser.
        "MiragePrismPrismBox",

        // The outfit glamour window, opened from the dresser. It moves pieces between the dresser
        // and an outfit glamour, so it changes the dresser just as the dresser window does.
        "MiragePrismPrismSetConvert",

        // The Armoire.
        "Cabinet",

        // The chocobo saddlebag.
        "InventoryBuddy",
    };

    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    // Built on first use and reused: the sheets cannot change while the game runs. Each is assigned
    // only when its build succeeds (an unreadable sheet throws first), so a field holds either a real
    // result or nothing. The outfit-set index maps a stored outfit's set id to its pieces; the
    // cabinet rows pair each Armoire row with the item it stores.
    private IReadOnlyDictionary<uint, uint[]>? mirageSetIndex;
    private IReadOnlyList<(uint CabinetId, uint ItemId)>? cabinetRows;

    // The answer to "is this item glamour gear?" for every id asked so far. The item sheet cannot
    // change while the game runs, so an answer never goes stale, and a character's held ids repeat
    // from pass to pass. `new()` is a target-typed `new`: the compiler takes the type from the field.
    private readonly Dictionary<uint, bool> glamourGearAnswers = new();

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy. Passed in from the registry rather than
    /// hardcoded here, so that every category is described in exactly one file.
    /// </param>
    /// <param name="dataManager">
    /// Dalamud's game data accessor, used to read the outfit, Armoire and item sheets.
    /// </param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="gameGui">Used to check whether a storage window is open.</param>
    /// <param name="condition">Used to check whether a retainer bell is in use.</param>
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

    /// <inheritdoc/>
    // This collector needs nothing from the context: it reports all the gear the character holds,
    // with no server manifest narrowing the scope.
    public CollectResult Collect(CollectContext context)
    {
        // Everything below dereferences raw game memory. Reading it off the framework thread races
        // the game's own writes, and the resulting access violation cannot be caught — so refuse.
        GameThread.EnsureFrameworkThread(framework, nameof(GlamourCollector));

        // Checked before anything is read: with a storage window open, the copies read below may be
        // behind the live containers (see the class remarks).
        if (IsStorageWindowOpen())
            return CollectResult.Skipped(CollectSkipReasons.StorageWindowOpen);

        // The three sheets this pass needs. See CollectSkipReasons.SheetUnavailable for why this is
        // a catch rather than a null check. `??=` assigns only when the field is still null, so the
        // two storage sheets are read once and then served from the fields. The item sheet object
        // is cached by Dalamud itself, so asking for it each pass is only a lookup.
        //
        // The locals share their fields' names: `this.mirageSetIndex` is the field, and the bare
        // `mirageSetIndex` is the local declared here, the same convention the constructor uses.
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

        // A null inventory manager means the inventory cannot be read this pass, usually because no
        // character is logged in. The skip's hint tells the player so.
        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return CollectResult.Skipped(CollectSkipReasons.InventoryUnavailable);

        // The item finder may be missing. Then the saddlebag, the retainers and the dresser are
        // simply not read: each StorageSources gate is false for a null finder and each note reads
        // unscanned (see StorageSources.Saddlebag for the one exception). The UI state, which holds
        // the Armoire, is handled the same way.
        var finder = ItemFinderModule.Instance();
        var uiState = UIState.Instance();

        // Null when the held gear cannot be read as a whole (ReadHeld says when). The character is
        // logged in, so this is its own skip rather than the log-in hint above.
        var held = ReadHeld(inventory, finder);
        if (held is null)
            return CollectResult.Skipped(CollectSkipReasons.StorageUnreadable);

        // The retainers whose copies were just read, with their names and the market's owner when
        // the game has them; RetainerRoster turns them into the upload's retainers list.
        var roster = ReadRoster(held.RetainerIds, held.MarketRead, held.ListingsRead);

        var dresser = ReadDresser(finder);
        var armoire = ReadArmoire(uiState, cabinetRows);

        // The notes describe the reads just made. This all runs inside one framework update, where
        // the game's own code cannot run in between, so each flag StorageSources reads holds the
        // same value the read above gated on.
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = StorageSources.Inventory(),
            [SourceKeys.Saddlebag] = StorageSources.Saddlebag(finder, uiState),
            [SourceKeys.Retainers] = StorageSources.Retainers(finder),
            [SourceKeys.Armoire] = StorageSources.Armoire(uiState),
            [SourceKeys.GlamourDresser] = StorageSources.GlamourDresser(finder),
        };

        // `itemId => IsGlamourGear(itemSheet, itemId)` is a lambda, an inline function like an
        // arrow function in TypeScript. It closes over the item sheet so the pure builder can ask
        // its question without ever seeing a game service.
        var built = GlamourSnapshot.Build(
            dresser,
            mirageSetIndex,
            armoire,
            held.Slots,
            roster,
            itemId => IsGlamourGear(itemSheet, itemId));

        // A withheld snapshot names its reason, already a skip reason (GlamourSnapshot's class
        // remarks list them).
        if (built.WithheldReason is not null)
            return CollectResult.Skipped(built.WithheldReason);

        // `!` tells the compiler what GlamourBuildResult guarantees and cannot express in its types:
        // a result that is not withheld always carries facts.
        return CollectResult.Glamour(built.Facts!, sourceNotes);
    }

    // True when a storage window is open, or a retainer bell is in use, so this pass must not read.
    private bool IsStorageWindowOpen()
    {
        // ICondition answers the game's condition flags by indexer, like reading a map entry. This
        // flag is set for as long as the player is using a summoning bell, which stands in for the
        // retainer windows (see StorageWindows).
        if (condition[ConditionFlag.OccupiedSummoningBell])
            return true;

        foreach (var name in StorageWindows)
        {
            // GetAddonByName returns a small wrapper around the game's pointer to a window ("addon"
            // is the game's word for a UI window). IsNull means no window of that name exists right
            // now. Any window that exists counts as open, drawn or not: the class remarks say why
            // visibility alone is not enough.
            if (!gameGui.GetAddonByName(name).IsNull)
                return true;
        }

        return false;
    }

    // What ReadHeld read: every slot with its place, the raw ids of the retainers whose saved copies
    // were read, whether the market listings container was loaded, and how many occupied listing
    // slots it held. A positional record, like the builder's DresserReading.
    private sealed record HeldReading(
        List<HeldSlot> Slots, List<ulong> RetainerIds, bool MarketRead, int ListingsRead);

    // Every occupied slot of the containers that can hold the character's gear, tagged with its
    // place: the live containers, the item finder's saddlebag and retainer copies when it has them,
    // and one retainer's market listings. Slots holding anything other than gear are included too;
    // GlamourSnapshot keeps only the gear. Repeats are expected; the builder sums them per item,
    // place and retainer.
    //
    // Returns null when the held list cannot be read as a whole: a gear container (a bag, the
    // equipped set or an armory chest) is not loaded, or a retainer the item finder remembers has
    // no inventory behind it or a zero id. The inventory note says Live, so a gear container left
    // unread would arrive as current and every piece in it would read as removed. A retainer left
    // unread would be missing from the retainers list, yet the retainer note would still count it,
    // so the upload would still count as all-current and every piece only that retainer holds would
    // count toward clearing its ownership flag. The category is not read at all instead: the caller
    // reports a null as storage that could not be read (CollectSkipReasons.StorageUnreadable).
    private static HeldReading? ReadHeld(InventoryManager* inventory, ItemFinderModule* finder)
    {
        var slots = new List<HeldSlot>();
        var retainerIds = new List<ulong>();

        // BAGS, EQUIPPED SET AND ARMORY CHESTS — every one must be read, or the pass reads nothing
        // (the method comment says why). IsLoaded is the game's own signal that a container's
        // contents are ready. Once a character is in the world every one of these reports itself
        // loaded, the waist chest included, though no belt can be equipped, so this skip appears
        // only while the game is still loading the inventory.
        // `var (type, place)` unpacks each (container, place) pair StorageContainers lists.
        foreach (var (type, place) in StorageContainers.GearContainers)
        {
            if (!AddLiveContainer(inventory, type, place, slots))
                return null;
        }

        // SADDLEBAG, premium half included, from the item finder's copy: each half is a pair of
        // parallel arrays, the stored ids and their quantities. Read only once that copy has been
        // refreshed this session; ids it loaded from disk at login do not count (see
        // StorageSources.IsSaddlebagCached). The gate is false for a missing item finder too.
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

        // RETAINERS, from the item finder's copy, one map entry per retainer whose contents it
        // remembers. Walking the map itself yields key/value pairs: `Item1` is the retainer's id and
        // `Item2` its inventory. The id is kept only to tie each slot to its retainer and to be
        // hashed by RetainerRoster; it never leaves the process. A retainer the finder has never
        // remembered is simply not in the map. The gate is the one the retainer note is built on,
        // and is false for a missing item finder.
        if (StorageSources.HasRememberedRetainers(finder))
        {
            foreach (var pair in finder->RetainerInventories)
            {
                // `Item2` is a small wrapper around a pointer to the retainer's inventory, and
                // `.Value` is the raw pointer inside it, null when the entry has nothing behind it.
                var retainerId = pair.Item1;
                var retainer = pair.Item2.Value;

                // An entry with no inventory, or with a zero id, is a retainer that cannot be read
                // and named: its copies could not be tied to it, yet the retainer note still counts
                // it. So the whole pass is not read (see the method comment).
                if (retainer is null || retainerId == 0)
                    return null;

                retainerIds.Add(retainerId);

                // What the retainer stores, with a quantity per slot, and what it has equipped, one
                // copy each: both are the player's own gear, and both count under its retainer place.
                AddStoredSlots(
                    retainer->ItemIds, retainer->ItemCount, HeldPlaces.Retainer, retainerId, slots);
                foreach (var equippedId in retainer->EquippedItemIds)
                {
                    if (equippedId != 0)
                        slots.Add(new HeldSlot(equippedId, 1, HeldPlaces.Retainer, retainerId));
                }
            }
        }

        // MARKET LISTINGS — the one live retainer container read anywhere. A piece put up for sale
        // leaves its retainer's bags, and so leaves the item finder's saved copy of them; without
        // this read, a listed piece would be missing from the held counts. Because it has left the
        // bags, no copy is ever counted twice.
        //
        // The coverage is partial. The container holds one retainer's listings, normally those of
        // the retainer summoned most recently this session (RetainerRoster.MarketOwner covers when
        // it does not), and only those: listings on any other retainer are not seen, before the first
        // summon of a session nothing is seen (the container is empty, which adds nothing), and
        // summoning a different retainer drops the earlier one's listings from the held counts. So
        // this read makes a listed piece read as gone less often; it does not prevent it.
        //
        // It can also overcount for a while: a piece that has sold since that retainer was summoned
        // stays counted while the game's retainer list still gives that retainer the listing count
        // the container holds (see RetainerRoster.MarketOwner), at most until the retainer is
        // summoned again. That stale positive is the lesser error here, since the alternative is an
        // outfit cleared while its piece is merely for sale.
        //
        // Unlike a bag, a market that is not loaded never stops the pass: it only means there are no
        // listings to report. Whether it was read, and how many listings it held, are kept so the
        // roster can decide whose listings these are (see ReadRoster). The slots carry no retainer
        // of their own; the roster's market key is the one they take, and without one they are
        // left out.
        var slotsBefore = slots.Count;
        var marketRead =
            AddLiveContainer(inventory, InventoryType.RetainerMarket, HeldPlaces.Market, slots);
        var listingsRead = slots.Count - slotsBefore;

        return new HeldReading(slots, retainerIds, marketRead, listingsRead);
    }

    // The retainers whose saved copies were read, as the upload's roster. Names, listing counts and
    // the last selection come from the game's retainer list, which it loads only once a summoning
    // bell has been used this session; before then the roster still lists every read retainer,
    // without names and without a market flag. Which retainer owns the listings is decided by
    // RetainerRoster.MarketOwner, which documents the rule.
    private static RetainerRoster ReadRoster(
        IReadOnlyList<ulong> readRetainerIds, bool marketRead, int listingsRead)
    {
        var names = new Dictionary<ulong, string>();
        var listingCounts = new Dictionary<ulong, int>();
        ulong lastSelected = 0;

        var manager = RetainerManager.Instance();
        if (manager is not null && manager->IsReady)
        {
            // An entry is a retainer only when its id is non-zero: an unused entry can still hold
            // leftover name bytes. `ref readonly` reads each entry where it sits in game memory
            // rather than copying it, and NameString decodes the name's bytes into a string.
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

    // Adds every occupied slot of one live container to the held list, with its quantity and place.
    // Returns whether the container was read at all, so the caller can tell "read, and empty" from
    // "not read".
    private static bool AddLiveContainer(
        InventoryManager* inventory,
        InventoryType type,
        string place,
        List<HeldSlot> slots)
    {
        var container = inventory->GetInventoryContainer(type);

        // A container the game has not allocated (null) or not yet populated (IsLoaded == false)
        // holds nothing we can trust; it is not walked, and reported as not read, rather than walk
        // uninitialized memory.
        if (container is null || !container->IsLoaded)
            return false;

        var size = container->GetSize();
        for (var slotIndex = 0; slotIndex < size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);

            // A null slot within GetSize() should not happen, but game memory is read defensively: a
            // bad dereference here would take the whole process down uncatchably.
            if (slot is null)
                continue;

            // The base item id: a live slot keeps quality in a separate flag, so its id is already
            // the base id. Held copies are summed across qualities, so the flag is not needed. An
            // empty slot stores 0 as padding.
            var id = slot->ItemId;
            if (id == 0)
                continue;

            // Quantity is an int field; an occupied slot is always at least 1, but guard the cast so
            // a torn read can never wrap a negative into an enormous uint.
            var quantity = slot->Quantity;
            if (quantity <= 0)
                continue;

            slots.Add(new HeldSlot(id, (uint)quantity, place));
        }

        return true;
    }

    // Adds an item finder container's parallel (stored id, quantity) arrays to the held list, one
    // slot per occupied entry, tagged with its place and, for a retainer, the retainer's id. The
    // loop bound is the shorter of the two spans, so a mismatched pair can never read out of
    // bounds. The ids keep the item finder's high-quality encoding (see GameItemId), which
    // GlamourSnapshot reads back out, so they are passed through untouched. Zero is an empty slot
    // and is skipped; a zero quantity is passed through, and the builder ignores it.
    //
    // A Span<T> is a window onto memory that already exists, here an array inside the game's own
    // struct. Nothing is copied until a slot is added to the list.
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

    // The dresser as read this pass, or null when the item finder's copy has not been refreshed
    // this session or there is no item finder at all (see StorageSources.IsDresserCached, the gate
    // the dresser note is built on).
    //
    // Every array is copied out with ToArray(). The builder takes plain lists and must never hold a
    // pointer into game memory, which the game is free to change or free after this frame.
    private static DresserReading? ReadDresser(ItemFinderModule* finder)
    {
        if (!StorageSources.IsDresserCached(finder))
            return null;

        // The game's live copy of the dresser, the only one carrying dyes. It is not always loaded
        // (see DresserPiece.Stains), and when it is not, the dyes are passed as null. Its slots line
        // up with the item finder's slot by slot, and it uses the same high-quality encoding (see
        // GameItemId). GlamourSnapshot decides which of its bytes are dyes.
        var mirage = MirageManager.Instance();
        var liveLoaded = mirage is not null && mirage->PrismBoxLoaded;

        // Named arguments (`FinderIds: ...`) say which constructor parameter each value fills, so
        // the five similar-looking arrays cannot be passed in the wrong order unnoticed.
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
        // The loaded flag (StorageSources.IsArmoireLoaded, the gate the Armoire note is built on) is
        // read in the same frame as the walk, immediately before it. While the game re-requests the
        // Armoire, its loaded flag drops and every row reads "not stored"; checking the flag in the
        // same frame as the walk means such a read is reported as not read, never as an empty
        // Armoire.
        if (!StorageSources.IsArmoireLoaded(uiState))
            return null;

        var armoire = new List<uint>();

        // The game answers by Armoire row rather than by item, so each row is asked about and the
        // item it stores is collected when the answer is yes.
        foreach (var (cabinetId, itemId) in cabinetRows)
        {
            if (uiState->Cabinet.IsItemInCabinet(cabinetId))
                armoire.Add(itemId);
        }

        return armoire;
    }

    // Whether an item counts as glamour gear. This method only looks the item up on the game's
    // sheets and remembers the answer; the rule itself, and what counts as gear, is
    // GlamourGear.Qualifies.
    private bool IsGlamourGear(ExcelSheet<ItemSheet> itemSheet, uint itemId)
    {
        // `out var known` declares the variable TryGetValue fills when the id has been asked before.
        if (glamourGearAnswers.TryGetValue(itemId, out var known))
            return known;

        // The row, or null when the sheet has no row with that id. GetRowOrDefault returns `Item?`:
        // sheet rows are structs (value types), so that `?` makes it a Nullable<Item>, and `.Value`
        // reads the row inside once it is known not to be null.
        var row = itemSheet.GetRowOrDefault(itemId);

        // What the rule needs from the sheets. Both keep these starting values ("equips nowhere",
        // "not read") when the item has no row.
        uint slotRowId = 0;
        sbyte? soulCrystal = null;

        if (row is not null)
        {
            // EquipSlotCategory is a reference to a row of another sheet. RowId is the id it points
            // at (0 for an item that equips nowhere); ValueNullable reads that row, or gives null
            // when it cannot be found. `?.` reads a member only when the value before it is not
            // null and gives null otherwise, just like optional chaining in TypeScript.
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
