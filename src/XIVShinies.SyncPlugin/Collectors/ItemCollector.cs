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
/// <para>
/// Only the items in the server's manifest are ever looked at. A count matters twice over:
/// possession of a relic-stage item proves that step server-side, and for items the website tracks
/// by number (materials, currencies, tomestones) the count itself feeds a running total. A stale
/// positive is still useful for the proof case — an item that <i>was</i> there proves the step
/// happened even after it has been consumed — which is why cached sources are consulted at all and
/// why any cache contribution is reported honestly as not fresh.
/// </para>
/// <para>
/// Live containers (bags, equipped gear, the whole armory chest, crystals, and currency) are read
/// directly this pass. The cache-backed sources — the armoire, the glamour dresser, the saddlebags,
/// and the player's own <b>retainers</b> — are read only once the player has opened each of them,
/// and the game keeps a local copy the plugin can read afterwards. The counts from every source are
/// <b>summed</b>: an item held in two places contributes from both. Summing (rather than a
/// live-then-cache fallback) is what lets a count-tracked total reflect copies parked on a retainer
/// or in the saddlebag in addition to those carried live. Relic weapons and tools are commonly
/// parked on a retainer, so omitting them would under-report an item the player owns.
/// </para>
/// <para>
/// Only the local player's own storage is read; no other character's data is touched, and no
/// retainer ID ever leaves the process.
/// </para>
/// <para>
/// The glamour category (<see cref="GlamourCollector"/>) reads the same storage and reports a scan
/// state for the same sources. Both collectors gate their reads and build those notes through
/// <see cref="StorageSources"/> (which says why the two must share it), and read the same sheets
/// through <see cref="StorageSheets"/>.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and cannot
/// be unit-tested. The pure logic it builds on — <see cref="ArmoireIndex"/> (the armoire lookup),
/// <see cref="ItemTallies.AddStoredId"/> and <see cref="ItemTallies.AddDresserSlot"/> (how a slot
/// read from an item finder copy is tallied, including reading its high-quality encoding and
/// expanding a stored outfit into its pieces), <see cref="ItemTally.ForQuality"/> (routing a
/// quantity into its quality bucket), and <see cref="ItemTallies.BuildPossessions"/> (turning the
/// tallies into wire entries) — is covered by tests; the container reads themselves are verified
/// by in-game QA.
/// </para>
/// </remarks>
// `unsafe` allows raw pointers. FFXIVClientStructs maps the game's own memory layout, so its
// Instance() methods hand back pointers into the live game rather than managed objects. C# normally
// forbids this; the keyword is the explicit opt-in. There is no JS equivalent whatsoever.
public sealed unsafe class ItemCollector : ICollector
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    // Built on first use and reused. The sheet never changes while the game is running.
    private IReadOnlyDictionary<uint, uint>? armoireIndex;

    // Built on first use and reused, same as armoireIndex. Maps a glamour-dresser outfit's set id to
    // the pieces inside it, so a piece stored "as an outfit" can be counted (see TallyGlamourDresser).
    private IReadOnlyDictionary<uint, uint[]>? mirageSetIndex;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    // Every live container walked once per pass. Ordering is cosmetic — each slot is matched against
    // the manifest independently, and the possessions are built by walking the manifest, not the
    // tallies — but grouping reads clearly: first the gear containers shared with the glamour
    // category (StorageContainers.GearContainers: the four carried bags, the equipped set, and every
    // armory chest but the soul crystal chest), then the soul crystal chest, crystals and currency.
    // Every armory chest the game defines is walked, so nothing the manifest asks about can hide in a
    // slot we skipped (a soul crystal, or a belt left in the waist chest). Containers this collector
    // must never walk are listed on StorageContainers.
    //
    // `[.. a, b, c]` is a collection expression with a spread, the C# counterpart of `[...a, b, c]`
    // in TypeScript. Item counts have no use for the held places the shared list pairs with each
    // container, so `Select` keeps only the container types, like `.map(c => c.type)`.
    private static readonly InventoryType[] LiveContainers =
    [
        .. StorageContainers.GearContainers.Select(container => container.Type),
        InventoryType.ArmorySoulCrystal,
        InventoryType.Crystals,
        InventoryType.Currency,
    ];

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy. Passed in from the registry rather than
    /// hardcoded here, so that every category is described in exactly one file.
    /// </param>
    /// <param name="dataManager">
    /// Dalamud's game data accessor, used to read the Armoire and glamour-dresser outfit sheets.
    /// </param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
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
        // Everything below dereferences raw game memory. Reading it off the framework thread races
        // the game's own writes, and the resulting access violation cannot be caught — so refuse.
        GameThread.EnsureFrameworkThread(framework, nameof(ItemCollector));

        // Without a config we do not know which items the server cares about. That is "could not
        // read", not "found nothing" — so skip rather than send an empty list.
        if (context.RemoteConfig is null)
            return CollectResult.Skipped(CollectSkipReasons.NoRemoteConfig);

        // The server offers consent groups and the user has switched all of them off. Nothing may be
        // looked for, so nothing is looked at — and a collection nothing was read for must say so
        // rather than report an empty set of facts, which would read as "your inventory was checked
        // and you own none of these". The settings window turns this reason into the one action that
        // resolves it (see CollectSkipReasons.Describe). The rule itself is pure, and lives in
        // ManifestConsent so it can be unit-tested; this class cannot be, as it reads game memory.
        if (ManifestConsent.GroupsOfferedButNoneEnabled(context))
            return CollectResult.Skipped(CollectSkipReasons.NoItemGroupsEnabled);

        // Read the manifest once: ManifestFor recomputes on every call, and the scan below must
        // see one stable list.
        var manifest = context.ManifestFor(CategoryKeys.Items);

        // An empty manifest is a real answer: the server asked about nothing, so we found nothing.
        // No source notes are attached here on purpose — with nothing to look for we never walk a
        // container or consult a cache, so claiming any source was "live" or "unscanned" would
        // describe a scan that never happened.
        if (manifest.Count == 0)
            return CollectResult.Items(Array.Empty<ItemPossession>());

        var inventory = InventoryManager.Instance();
        if (inventory is null)
            return CollectResult.Skipped(CollectSkipReasons.InventoryUnavailable);

        // The set of ids the manifest asks about, built once so each slot walked below is an O(1)
        // membership test. A HashSet also collapses any duplicate manifest entries, so the armoire —
        // which holds a single copy per item — is never counted twice for one manifest id. (Counts
        // still sum ACROSS sources: the same item held in a bag and on a retainer contributes from
        // both, and a dresser can legitimately hold a piece both loosely and inside an outfit.)
        var manifestIds = new HashSet<uint>(manifest);

        // Two parallel tallies, keyed by item id: what the live containers hold, and what the caches
        // remember. They are combined per quality by ItemTallies.BuildPossessions at the end.
        var live = new Dictionary<uint, ItemTally>();
        var cached = new Dictionary<uint, ItemTally>();

        // Per-source scan status for the upload. Built from the same gating flags the scan itself
        // reads, so the note always matches what was actually consulted.
        var sourceNotes = new Dictionary<string, ItemSourceStatus>();

        // LIVE TALLY — walk every live container exactly once.
        foreach (var type in LiveContainers)
            TallyLiveContainer(inventory, type, manifestIds, live);

        // LIVE TALLY (continued) — currencies the container walk cannot see. The walk above covers
        // InventoryType.Currency (the common currencies on the in-game Currency tab), but the game
        // tracks a second class of "currency" items in CurrencyManager instead — its buckets include
        // scrips, Bicolor Gemstones, Trophy Crystals, ventures, and Island Sanctuary materials (per
        // the FFXIVClientStructs docs). Which subsystem holds a given id is the game's business and
        // not always guessable, so this simply resolves whatever the walk missed, by item id.
        TallyCurrencyFallback(manifestIds, live);

        // Reaching this point means the inventory manager was readable and every live container was
        // walked, so both live sources are genuinely live this pass. The inventory note comes from
        // StorageSources, like every storage note this collector shares with the glamour category.
        // Currencies are reported as their own source even though they ride the same scan: the
        // Currency container is one of the live containers walked above, and the currency-manager
        // fallback reads the same current game memory, so one state is truthful for both reads.
        // Whether the fallback ran at all does not change the note (a null manager leaves the walk's
        // results standing).
        sourceNotes[SourceKeys.Inventory] = StorageSources.Inventory();
        sourceNotes[SourceKeys.Currencies] = new ItemSourceStatus { State = SourceStates.Live };

        // CACHED TALLY — the armoire, glamour dresser, saddlebags, and retainers, plus their notes.
        BuildCachedTallies(manifestIds, cached, sourceNotes);

        // Mannequins are reported so the settings panel can say why displayed gear never counts:
        // the game fetches a mannequin's contents only during an in-house interaction and never
        // caches them, so there is nothing this collector could ever scan. The status is constant
        // by nature and stays local — the payload builder drops unreadable sources from uploads.
        // Reported after every scannable source, because the panel lists chips in report order and
        // the one source that can never contribute belongs at the end, not among the working ones.
        sourceNotes[SourceKeys.Mannequins] = new ItemSourceStatus { State = SourceStates.Unreadable };

        // ItemTallies applies the explicit-zero rule (one entry per valid manifest id), the
        // omit-when-unseen exception for the server's content-bound currency ids, and the
        // freshness rule (any cache contribution marks the entry not fresh). Named arguments are
        // mandatory: the two dictionaries share a type, and transposing them would invert every
        // Fresh flag silently.
        return CollectResult.Items(
            ItemTallies.BuildPossessions(
                manifest: manifest,
                live: live,
                cached: cached,
                omitWhenUnseen: context.ItemOmitWhenUnseenIds),
            sourceNotes);
    }

    // Walks one live container once, adding every occupied slot whose id is in the manifest to the
    // live tally with its quality routed to the right bucket.
    private static void TallyLiveContainer(
        InventoryManager* inventory,
        InventoryType type,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> live)
    {
        // `->` dereferences a pointer and reads a member — the pointer equivalent of `.`.
        var container = inventory->GetInventoryContainer(type);

        // A container the game has not allocated (null) or not yet populated (IsLoaded == false)
        // holds nothing we can trust; skip it rather than walk uninitialized memory.
        if (container is null || !container->IsLoaded)
            return;

        // GetSize() is the slot count for THIS container. Walking each slot once makes the whole live
        // scan cost O(total slots) — a few hundred across every bag and armory chest — regardless of
        // how many ids the manifest asks about. Asking the game for a count per manifest id would
        // re-walk every container for each id: O(ids x containers). Walking once and matching against
        // a HashSet costs the same whether the manifest holds ten ids or a thousand.
        var size = container->GetSize();
        for (var slotIndex = 0; slotIndex < size; slotIndex++)
        {
            var slot = container->GetInventorySlot(slotIndex);

            // A null slot within GetSize() should not happen, but game memory is read defensively: a
            // bad dereference here would take the whole process down uncatchably.
            if (slot is null)
                continue;

            // The raw base item id, read straight from the struct field. Deliberately NOT
            // GetItemId(), whose virtual form applies the high-quality offset to the id — the
            // manifest lists base ids, so the offset form would never match. Quality is read
            // separately below.
            var id = slot->ItemId;

            // An empty slot stores id 0 as padding, never a real item. Skip it before the manifest
            // test, since a malformed manifest could otherwise list 0 and match the padding.
            if (id == 0 || !manifestIds.Contains(id))
                continue;

            // Quantity is an int field; an occupied slot is always at least 1, but guard the cast so
            // a torn read can never wrap a negative into an enormous uint.
            var quantity = slot->Quantity;
            if (quantity <= 0)
                continue;

            // IsHighQuality()/IsCollectable() read the item's flags for us — no bit math needed.
            ItemTallies.Add(
                live,
                id,
                ItemTally.ForQuality((uint)quantity, slot->IsHighQuality(), slot->IsCollectable()));
        }
    }

    // Resolves manifest ids the container walk could not see by consulting CurrencyManager, the
    // game's tracker for currencies that are not stored in an inventory container. Its documented
    // buckets include scrips, Bicolor Gemstones, Trophy Crystals, ventures, and Island Sanctuary
    // materials — but the split between the Currency container and these buckets is the game's
    // internal choice, not a rule to rely on (a hunt seal, for example, sits in a bucket). Whatever
    // the walk already tallied is intentionally skipped here; see the guard below.
    //
    // SAFETY — never query a count for an id the manager does not track. CurrencyManager keeps its
    // currencies in three buckets (ItemBucket, SpecialItemBucket, ContentItemBucket per the
    // FFXIVClientStructs docs); its GetItemCount takes an arbitrary item id and its behavior for an id
    // that is in NO bucket is not documented, so we treat it as unsafe to call blind. HasItem is the
    // membership probe — the FFXIVClientStructs summary states it "Checks if the item is in any
    // bucket" — so every GetItemCount call is gated behind a HasItem check that returned true. We never
    // hand an untracked id to the count method on a "probably returns 0" assumption.
    //
    // DOUBLE-COUNT — the fallback only consults ids ABSENT from the live tally (`!live.ContainsKey`).
    // A currency that lives in the Currency container was already tallied by the walk and would be
    // double-counted if added again; skipping ids the walk found is how the two reads stay disjoint.
    // (Currencies never appear in the cache-backed sources, so there is no overlap with `cached`.)
    //
    // This ADDS counts the walk cannot see; it never invents one. An id that is neither in a container
    // nor tracked by the manager stays absent from every tally, and BuildPossessions still emits the
    // honest explicit zero for it from the manifest — absence here means "no live copy found", not a
    // guessed value.
    private static void TallyCurrencyFallback(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> live)
    {
        // Like the other game-memory singletons, a null instance means the subsystem is not available
        // this pass. Skip the fallback entirely and leave the container walk's results untouched — the
        // manager being briefly unreadable is not a reason to drop what we could already see.
        var currency = CurrencyManager.Instance();
        if (currency is null)
            return;

        // Iterate the deduped manifest id set, not every currency the game knows: the loop is bounded
        // by how many ids the manifest asks about (tiny), preserving the walk-once cost discipline. We
        // do not enumerate the manager's buckets.
        foreach (var id in manifestIds)
        {
            // id 0 is never a real item; an id the walk already found is left to the walk's tally so it
            // is not counted twice.
            if (id == 0 || live.ContainsKey(id))
                continue;

            // Membership probe FIRST — only ask for a count once the manager confirms it tracks the id.
            if (!currency->HasItem(id))
                continue;

            // Held amount for this currency. Currencies carry no quality, so the whole count is normal
            // quality (the Nq bucket). A zero here adds nothing the manifest's explicit zero does not
            // already report, so skip it rather than create an all-zero live entry. For an id in the
            // server's omit-when-unseen set this makes a genuine zero balance indistinguishable from
            // unseen, so it uploads as an omission rather than a zero — equivalent on the server,
            // which drops all-zero entries for those ids at apply time anyway.
            var count = currency->GetItemCount(id);
            if (count == 0)
                continue;

            ItemTallies.Add(
                live, id, ItemTally.ForQuality(count, isHighQuality: false, isCollectable: false));
        }
    }

    // Fills the cached tally from every cache-backed source, and records how each was read into the
    // source notes. Each source's read is gated by a StorageSources predicate and its note built by
    // the matching StorageSources note method; StorageSources documents why, and what each gate and
    // state means.
    private void BuildCachedTallies(
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached,
        Dictionary<string, ItemSourceStatus> notes)
    {
        // ARMOIRE — reached through UIState, independently of the item finder.
        var uiState = UIState.Instance();
        notes[SourceKeys.Armoire] = StorageSources.Armoire(uiState);
        if (StorageSources.IsArmoireLoaded(uiState))
        {
            // Tally one per stored manifest item: the armoire holds a single copy of each item it can
            // store, so a match contributes a count of 1. Iterating the deduped id set (not the raw
            // manifest) keeps a duplicate manifest entry from counting the same armoire item twice.
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
            // Nothing could be read from the cache-backed sources, and each note reads unscanned for
            // a null item finder (see StorageSources.Saddlebag for the one exception).
            notes[SourceKeys.Saddlebag] = StorageSources.Saddlebag(null, uiState);
            notes[SourceKeys.Retainers] = StorageSources.Retainers(null);
            notes[SourceKeys.GlamourDresser] = StorageSources.GlamourDresser(null);
            return;
        }

        // SADDLEBAG (including the premium half). Each stored id goes through
        // ItemTallies.AddStoredId, which reads the item finder's high-quality encoding back out (see
        // GameItemId). Nothing from these caches is ever reported as collectable.
        if (StorageSources.IsSaddlebagCached(finder))
        {
            TallySlots(finder->SaddleBagItemIds, finder->SaddleBagItemCount, manifestIds, cached);
            TallySlots(finder->PremiumSaddleBagItemIds, finder->PremiumSaddleBagItemCount, manifestIds, cached);
        }

        notes[SourceKeys.Saddlebag] = StorageSources.Saddlebag(finder, uiState);

        // GLAMOUR DRESSER. The outfit-expansion index is resolved once here rather than inside the
        // per-slot loop, so the walk below always has a real index to expand an outfit through (see
        // GetMirageSetIndex for what an unreadable sheet does instead).
        if (StorageSources.IsDresserCached(finder))
            TallyGlamourDresser(finder, GetMirageSetIndex(), manifestIds, cached);

        notes[SourceKeys.GlamourDresser] = StorageSources.GlamourDresser(finder);

        // RETAINERS. The gate asks whether the cache remembers any retainer's contents, from the
        // map's count alone; reading it does not touch the map's keys (see the privacy note on
        // TallyRetainers). The note adds how many are remembered and, when the game knows it, the
        // character's total retainer count (see StorageSources.Retainers).
        if (StorageSources.HasRememberedRetainers(finder))
            TallyRetainers(finder, manifestIds, cached);

        notes[SourceKeys.Retainers] = StorageSources.Retainers(finder);
    }

    /// <summary>
    /// Adds every occupied slot of the local player's retainers to the cached tally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retainers matter: relic weapons and tools are very often parked on one, and skipping them
    /// would report zero for an item the player demonstrably owns, leaving the relic step unproven.
    /// </para>
    /// <para>
    /// These are the local player's own retainers — their own possessions, not another player's
    /// data. Only the inventory <i>values</i> of the map are read; the keys, which are retainer
    /// IDs, are never touched and never leave the process. Nothing but item counts is uploaded.
    /// </para>
    /// <para>
    /// <c>ItemFinderModule</c> persists this cache to a local user file, so retainer contents can
    /// be present after a fresh login <b>without the player summoning the retainer</b> — the data
    /// came off disk, not from the server. It is therefore a genuine cache and may be stale, which
    /// is exactly why the retainer source is reported as cached, not live. A stale positive still
    /// proves the item was once held, which is all the server needs; possession is volatile, and
    /// relic proofs are sticky. A retainer that has never been visited contributes nothing, because
    /// its entry simply is not in the map.
    /// </para>
    /// <para>
    /// <c>IsRetainerCurrent</c> could tell a this-session read apart from the disk cache, but using
    /// it would mean reading the map's retainer-id keys, which item counts have no use for. The
    /// distinction would change nothing: both are reported as not fresh.
    /// </para>
    /// </remarks>
    private static void TallyRetainers(
        ItemFinderModule* finder,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        // `.Values` walks the map's inventories without ever reading its keys.
        foreach (var inventoryPointer in finder->RetainerInventories.Values)
        {
            // A map entry with no inventory behind it has nothing to read, so it adds nothing and
            // the walk moves on. The retainer note counts the map's entries, so it still counts
            // this retainer as remembered, and the copies stored on it are missing from this pass's
            // counts. The glamour category withholds its whole pass over such an entry (see
            // GlamourCollector.ReadHeld); this one keeps every other count and proof it read, and
            // a count that missed one retainer's copies is low until a later pass reads them.
            var retainer = inventoryPointer.Value;
            if (retainer is null)
                continue;

            // Stored in the retainer's bags, with per-slot counts.
            TallySlots(retainer->ItemIds, retainer->ItemCount, manifestIds, cached);

            // Equipped on the retainer: one each, and there are no counts to pair with.
            foreach (var equippedId in retainer->EquippedItemIds)
                ItemTallies.AddStoredId(cached, manifestIds, equippedId, 1);
        }
    }

    /// <summary>
    /// Adds every manifest-matching slot of a container's parallel (item id, count) arrays to the
    /// cached tally. Used for the saddlebags and for each retainer's bags, which share that layout.
    /// </summary>
    // A Span<T> is a window onto memory that already exists — no copy is made. Iterating one is the
    // safe way to walk a fixed-size array living inside the game's own structs. The loop bound is the
    // shorter of the two spans so a mismatched pair can never read out of bounds. Each slot goes to
    // ItemTallies.AddStoredId, which reads the high-quality encoding these copies use back out and
    // decides whether the slot is tallied at all.
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
    /// The dresser keeps two arrays paired by slot index: the stored id, and that slot's outfit
    /// unlock bits. This walks them and hands each pair to <see cref="ItemTallies.AddDresserSlot"/>,
    /// which holds the rules for a slot (a loose piece, or an outfit standing in for its pieces) and
    /// documents them. The bits are read from <c>ItemFinderModule</c> rather than
    /// <c>MirageManager.IsSetSlotUnlocked</c>, whose data is cleared on a zone change.
    /// </remarks>
    private static void TallyGlamourDresser(
        ItemFinderModule* finder,
        IReadOnlyDictionary<uint, uint[]> mirageSetIndex,
        HashSet<uint> manifestIds,
        Dictionary<uint, ItemTally> cached)
    {
        // Hoisted to locals and walked with one min-bounded loop so a length mismatch between the two
        // parallel arrays can never read past the shorter one.
        var ids = finder->GlamourDresserItemIds;
        var bits = finder->GlamourDresserItemSetUnlockBits;

        for (var slot = 0; slot < ids.Length && slot < bits.Length; slot++)
            ItemTallies.AddDresserSlot(cached, manifestIds, mirageSetIndex, ids[slot], bits[slot]);
    }

    private bool IsStoredInArmoire(uint itemId)
    {
        // While the Armoire is not loaded (or there is no UI state at all) the game genuinely does
        // not know its contents, and asking would return a confident "no" — so this is the same gate
        // the Armoire's note is built on (see StorageSources.IsArmoireLoaded).
        var uiState = UIState.Instance();
        if (!StorageSources.IsArmoireLoaded(uiState))
            return false;

        // Built once and reused; the sheet cannot change while the game runs. An unreadable sheet
        // throws rather than caching anything, so the field only ever holds a real index.
        armoireIndex ??= BuildArmoireIndex();

        // The game answers by armoire row, not by item, hence the lookup.
        return armoireIndex.TryGetValue(itemId, out var cabinetId)
               && uiState->Cabinet.IsItemInCabinet(cabinetId);
    }

    /// <remarks>
    /// An unreadable sheet throws out of here (from <see cref="StorageSheets.CabinetRows"/>) rather
    /// than being caught, and that is deliberate: the throw reaches <see cref="CollectorRunner"/>,
    /// which omits the whole <c>items</c> category — a report the server reads as "not read this
    /// time". Catching it here would instead send counts alongside an Armoire source note already
    /// set to <c>Loaded</c>, claiming a scan that never happened. Saying nothing beats saying
    /// something wrong confidently.
    /// </remarks>
    private IReadOnlyDictionary<uint, uint> BuildArmoireIndex() =>
        ArmoireIndex.Build(StorageSheets.CabinetRows(dataManager));

    /// <summary>
    /// Resolves the outfit-expansion index, building it on first use. Built once and reused; the
    /// sheet cannot change while the game runs.
    /// </summary>
    /// <remarks>
    /// Throws on an unreadable sheet (from <see cref="StorageSheets.MirageSets"/>) for the same
    /// reason <see cref="BuildArmoireIndex"/> does: an omitted category is honest, where counts that
    /// silently skipped every outfit the dresser holds are not. A throw leaves the field unset, so
    /// it only ever holds a real index.
    /// </remarks>
    private IReadOnlyDictionary<uint, uint[]> GetMirageSetIndex() =>
        mirageSetIndex ??= StorageSheets.MirageSets(dataManager);
}
