using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>Per-quality possession counts for one item id.</summary>
/// <remarks>
/// <para>
/// A <c>readonly record struct</c> is a small immutable value type copied by value on
/// assignment (no heap allocation) — perfect for a tally that lives briefly inside one
/// collection pass. The positional syntax declares the three properties in one line.
/// It is like a frozen plain object in JavaScript, but stack-allocated.
/// </para>
/// <para>
/// The three qualities (NQ, HQ, Collectable) are tracked separately because the server
/// applies different rules per quality; the plugin never sums them, only reports each.
/// </para>
/// </remarks>
public readonly record struct ItemTally(uint Nq, uint Hq, uint Collectable)
{
    /// <summary>Builds a tally holding one quantity in exactly one quality bucket.</summary>
    /// <remarks>
    /// Collectable is checked first because a collectable item is its own quality, distinct from
    /// ordinary high quality; high quality is next, and everything else is normal quality. The
    /// three buckets are never summed together — the server applies different rules per quality —
    /// so they travel apart.
    /// </remarks>
    // `static` here means the method belongs to the type itself rather than to an instance, like a
    // static factory function (`ItemTally.forQuality(...)`) in TypeScript.
    public static ItemTally ForQuality(uint quantity, bool isHighQuality, bool isCollectable)
    {
        if (isCollectable)
            return new ItemTally(Nq: 0, Hq: 0, Collectable: quantity);

        if (isHighQuality)
            return new ItemTally(Nq: 0, Hq: quantity, Collectable: 0);

        return new ItemTally(Nq: quantity, Hq: 0, Collectable: 0);
    }

    /// <summary>Combines two tallies of the same item, quality by quality.</summary>
    /// <remarks>
    /// Used to sum live and cached counts for an item. An empty tally (all zeros) added
    /// to any tally returns the original tally; this is the identity operation.
    /// </remarks>
    public ItemTally Add(ItemTally other) =>
        new(Nq + other.Nq, Hq + other.Hq, Collectable + other.Collectable);

    /// <summary>True when no copies were seen in any quality.</summary>
    /// <remarks>
    /// An all-qualities-zero check. <see cref="ItemTallies.BuildPossessions"/> applies it
    /// to the cached tally to compute freshness: any cache contribution, in any quality,
    /// marks the entry stale — even when live containers also hold copies.
    /// </remarks>
    public bool IsEmpty => Nq == 0 && Hq == 0 && Collectable == 0;
}

/// <summary>
/// The items category's tally rules: how each slot a scan reads adds to the per-item tallies, and
/// how those tallies become wire entries under the explicit-zero rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Building the tallies.</b> <see cref="Add"/> folds one slot into an item's running tally.
/// <see cref="AddStoredId"/> and <see cref="AddDresserSlot"/> hold the rules for a slot read from
/// one of the item finder's copies, where quality is encoded in the stored id and a dresser slot
/// may stand in for an outfit's pieces. All of them are Dalamud-free and unit-tested, so
/// <see cref="ItemCollector"/> holds only the game reads that feed them.
/// </para>
/// <para>
/// <b>Turning them into wire entries.</b> The explicit-zero rule (see <c>docs/api-contract.md</c>):
/// an entry PRESENT — even with count 0 — is a reported fact for that id; an id ABSENT from the
/// list was not scanned and carries no information. What a count means is the server's call per
/// id (relic proofs act only on positive counts; count-tracked ids read the number as the current
/// total). The per-id distinction is what makes a zero trustworthy at all: reaching
/// <see cref="BuildPossessions"/> means the live containers were readable (an unreadable inventory
/// skips the whole pass), and caches only ever ADD to a count — a cache can never turn a real zero
/// into something else.
/// </para>
/// <para>
/// <see cref="BuildPossessions"/> emits an entry for every valid manifest id (skipping zero and
/// duplicates), with one exception: an id in the server's omit-when-unseen set that NO source
/// resolved is left out entirely. Those ids are the content-bound currencies (see
/// <see cref="Api.ConfigResponse.ItemOmitWhenUnseenIds"/>): the game only exposes their
/// counts inside their content, so out-of-zone their absence means "not visible from here"
/// rather than "owns none" — and per the explicit-zero rule, the honest report for
/// no-information is no entry.
/// </para>
/// </remarks>
public static class ItemTallies
{
    /// <summary>Folds one quantity into an item's running tally, starting the tally if needed.</summary>
    /// <param name="tallies">The tallies being built this pass, keyed by item id.</param>
    /// <param name="id">The item the quantity belongs to.</param>
    /// <param name="delta">The quantity, already routed into its quality bucket.</param>
    /// <remarks>
    /// The same item can occupy several slots across several containers, so every slot a scan reads
    /// folds into the one tally its item already has.
    /// </remarks>
    public static void Add(Dictionary<uint, ItemTally> tallies, uint id, ItemTally delta)
    {
        // TryGetValue leaves `existing` as the default all-zero tally when the id is absent, which
        // ItemTally.Add treats as the identity.
        tallies.TryGetValue(id, out var existing);
        tallies[id] = existing.Add(delta);
    }

    /// <summary>
    /// Tallies one slot read from an item finder copy (the saddlebag, a retainer's bags or equipped
    /// gear) when its item is one the manifest asks about.
    /// </summary>
    /// <param name="cached">The cached tally being built this pass.</param>
    /// <param name="manifestIds">The item ids the server asked about.</param>
    /// <param name="storedId">The slot's stored id, high-quality encoding included.</param>
    /// <param name="quantity">How many copies the slot holds.</param>
    /// <remarks>
    /// The stored id carries the item finder's high-quality encoding (see <see cref="GameItemId"/>),
    /// so it is split first: a high-quality copy is tallied in the Hq bucket under its base id, and
    /// any other copy as normal quality. Nothing from these copies is ever reported as collectable.
    /// A base id of 0 is an empty slot, and it is never tallied, even should the manifest list 0.
    /// </remarks>
    // `IReadOnlySet<uint>` is a set the method may test membership in but not change, like a
    // `ReadonlySet<number>` in TypeScript. The collector's HashSet is one.
    public static void AddStoredId(
        Dictionary<uint, ItemTally> cached,
        IReadOnlySet<uint> manifestIds,
        uint storedId,
        uint quantity)
    {
        // `var (baseId, isHq) = ...` unpacks the returned pair into two variables, like array
        // destructuring (`const [baseId, isHq] = ...`) in TypeScript.
        var (baseId, isHq) = GameItemId.Split(storedId);
        var copies = ItemTally.ForQuality(quantity, isHq, isCollectable: false);

        AddIfListed(cached, manifestIds, baseId, copies);
    }

    /// <summary>
    /// Tallies one glamour dresser slot read from the item finder's copy, expanding a stored outfit
    /// into the pieces inside it.
    /// </summary>
    /// <param name="cached">The cached tally being built this pass.</param>
    /// <param name="manifestIds">The item ids the server asked about.</param>
    /// <param name="setIndex">
    /// Outfit set id → its piece columns, as built by <see cref="MirageSetIndex.Build"/>.
    /// </param>
    /// <param name="storedId">The slot's stored id, high-quality encoding included.</param>
    /// <param name="setBits">The slot's outfit-piece bits, from the array paired with the ids.</param>
    /// <remarks>
    /// <para>
    /// A dresser slot holds either an item's own id or an outfit's <b>set id</b> (a
    /// <c>MirageStoreSetItem</c> row). The slot's own id is tallied like any stored id, one copy,
    /// when the manifest lists it: a loose piece, or (harmlessly) a set id the manifest asks about in
    /// its own right. When the base id is a set id, the slot also stands in for the pieces stored
    /// inside the outfit, which its id alone does not name: the set index lists the outfit's pieces,
    /// and the slot's bits say which of them are stored right now. A slot's own id and its pieces
    /// are different item ids, so a manifest that lists both counts both.
    /// </para>
    /// <para>
    /// The dresser copy encodes quality in the stored id like every item finder copy (see
    /// <see cref="GameItemId"/>). Every piece of an outfit glamour shares one quality (a game rule),
    /// so the pieces take the slot's quality. Each stored piece counts one copy.
    /// </para>
    /// </remarks>
    public static void AddDresserSlot(
        Dictionary<uint, ItemTally> cached,
        IReadOnlySet<uint> manifestIds,
        IReadOnlyDictionary<uint, uint[]> setIndex,
        uint storedId,
        ushort setBits)
    {
        var (baseId, isHq) = GameItemId.Split(storedId);

        // An empty slot holds nothing, not even an outfit to look up.
        if (baseId == 0)
            return;

        // One copy, in the slot's quality bucket, for the slot itself and for each piece inside.
        var oneCopy = ItemTally.ForQuality(1, isHq, isCollectable: false);

        AddIfListed(cached, manifestIds, baseId, oneCopy);

        // The lookup uses the base id, so a high-quality outfit slot resolves to its set too. `out
        // var pieces` declares the variable TryGetValue fills when the id is a set id.
        if (setIndex.TryGetValue(baseId, out var pieces))
        {
            foreach (var pieceId in MirageSetIndex.StoredPieces(pieces, setBits))
                AddIfListed(cached, manifestIds, pieceId, oneCopy);
        }
    }

    // Folds a tally in when its item is one the manifest asks about. Id 0 is never an item (an
    // empty slot), so it is skipped before the manifest test, where a malformed manifest listing 0
    // would otherwise match it.
    private static void AddIfListed(
        Dictionary<uint, ItemTally> tallies,
        IReadOnlySet<uint> manifestIds,
        uint id,
        ItemTally delta)
    {
        if (id != 0 && manifestIds.Contains(id))
            Add(tallies, id, delta);
    }

    /// <summary>One <see cref="ItemPossession"/> per manifest id — zeros included.</summary>
    /// <param name="manifest">The server-controlled list of valid item ids to report on.</param>
    /// <param name="live">
    /// Counts from containers read directly this pass (bags, equipped gear, armory chest).
    /// Missing entries are treated as empty tallies (all qualities zero).
    /// </param>
    /// <param name="cached">
    /// Counts from the game's local caches (armoire, glamour dresser, saddlebags, retainers)
    /// — may be stale, and any contribution here marks the entry not fresh. Missing entries
    /// are treated as empty tallies.
    /// </param>
    /// <param name="omitWhenUnseen">
    /// The server's omit-when-unseen id set. An id in it that appears in NEITHER tally gets no
    /// entry instead of the explicit zero; an id either tally resolved — any count, from any
    /// source — reports normally. Null (an older server, or none configured) omits nothing.
    /// </param>
    /// <returns>
    /// A list of possessions in manifest order, one per valid manifest id. Every entry is
    /// fresh if the cache contributed nothing to it; stale if the cache contributed any
    /// quality. A zero entry (all qualities zero) is fresh and emitted with Count only
    /// (HqCount and CollectableCount omitted).
    /// </returns>
    /// <remarks>
    /// Call with named arguments — the two dictionaries share a type, and transposing them
    /// silently inverts every Fresh flag.
    /// </remarks>
    public static IReadOnlyList<ItemPossession> BuildPossessions(
        IReadOnlyList<uint> manifest,
        IReadOnlyDictionary<uint, ItemTally> live,
        IReadOnlyDictionary<uint, ItemTally> cached,
        IReadOnlySet<uint>? omitWhenUnseen = null)
    {
        // Pre-sized to the manifest length — the common case is every id valid and unique.
        var result = new List<ItemPossession>(manifest.Count);
        var seen = new HashSet<uint>();

        foreach (var id in manifest)
        {
            // Skip id 0 (padding in game arrays) and duplicate manifest entries.
            if (id == 0 || !seen.Add(id))
                continue;

            // Look up both sources, defaulting to empty tallies if not found.
            // "Resolved" is PRESENCE in a tally, not a nonzero count: the sources only record
            // ids they actually saw, so TryGetValue answering false on both means no source
            // laid eyes on this id at all this pass.
            var liveResolved = live.TryGetValue(id, out var liveTally);
            var cachedResolved = cached.TryGetValue(id, out var cachedTally);

            // The omit-when-unseen exception (see the class remarks): for a content-bound
            // currency the game is not currently exposing, silence is the honest report — an
            // explicit zero would overwrite the real count the server already holds.
            if (!liveResolved && !cachedResolved && omitWhenUnseen?.Contains(id) == true)
                continue;

            // Combine live and cached counts quality by quality.
            var total = liveTally.Add(cachedTally);

            // An entry is fresh only if the cache contributed nothing (i.e., the live scan
            // ran and found what we're reporting, or found nothing and we're reporting zero).
            // If the cache added any quality, the count came from a previous scan, not this one.
            var fresh = cachedTally.IsEmpty;

            // Build the wire entry: Count is NQ only (the server decides HQ requirements).
            // HQ and Collectable counts are omitted (null) when zero (per the DTO contract).
            var entry = new ItemPossession
            {
                Id = id,
                Count = total.Nq,
                HqCount = total.Hq == 0 ? null : total.Hq,
                CollectableCount = total.Collectable == 0 ? null : total.Collectable,
                Fresh = fresh,
            };

            result.Add(entry);
        }

        return result;
    }
}
