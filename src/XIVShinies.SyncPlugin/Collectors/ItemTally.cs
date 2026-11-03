using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>Per-quality possession counts for one item id.</summary>
/// <remarks>
/// The qualities stay separate because the server applies different rules to each; the plugin never
/// sums across them.
/// </remarks>
public readonly record struct ItemTally(uint Nq, uint Hq, uint Collectable)
{
    /// <summary>A tally with the whole quantity in one quality bucket.</summary>
    /// <remarks>Collectable comes first: it is its own quality, distinct from high quality.</remarks>
    public static ItemTally ForQuality(uint quantity, bool isHighQuality, bool isCollectable)
    {
        if (isCollectable)
            return new ItemTally(Nq: 0, Hq: 0, Collectable: quantity);

        if (isHighQuality)
            return new ItemTally(Nq: 0, Hq: quantity, Collectable: 0);

        return new ItemTally(Nq: quantity, Hq: 0, Collectable: 0);
    }

    /// <summary>
    /// Combines two tallies of the same item, such as its live and cached counts, quality by quality.
    /// </summary>
    public ItemTally Add(ItemTally other) =>
        new(Nq + other.Nq, Hq + other.Hq, Collectable + other.Collectable);

    /// <summary>True when no copies were seen in any quality.</summary>
    public bool IsEmpty => Nq == 0 && Hq == 0 && Collectable == 0;
}

/// <summary>
/// The items category's tally rules: how each slot a scan reads adds to the per-item tallies, and
/// how those tallies become wire entries under the explicit-zero rule.
/// </summary>
/// <remarks>
/// The explicit-zero rule (see <c>docs/api-contract.md</c>): a present entry, zero included, is a
/// fact, an absent id carries no information, and the server decides per id what a count means. A
/// zero is trustworthy because an unreadable inventory skips the whole pass and caches only add to a
/// count. The exception is the omit-when-unseen set (see
/// <see cref="Api.ConfigResponse.ItemOmitWhenUnseenIds"/>): content-bound currencies the game shows
/// only inside their content, so one no source resolved gets no entry rather than a zero.
/// </remarks>
public static class ItemTallies
{
    /// <summary>Adds a quantity, already in its quality bucket, to an item's running tally.</summary>
    public static void Add(Dictionary<uint, ItemTally> tallies, uint id, ItemTally delta)
    {
        // One item can fill several slots and containers; an absent id starts from the all-zero tally.
        tallies.TryGetValue(id, out var existing);
        tallies[id] = existing.Add(delta);
    }

    /// <summary>
    /// Tallies one slot of an item finder copy (the saddlebag, or a retainer's bags or equipped gear)
    /// when the manifest lists its item.
    /// </summary>
    /// <param name="cached">The cached tally being built this pass.</param>
    /// <param name="manifestIds">The item ids the server asked about.</param>
    /// <param name="storedId">
    /// The slot's stored id, high-quality encoding included (see <see cref="GameItemId"/>).
    /// </param>
    /// <param name="quantity">How many copies the slot holds.</param>
    /// <remarks>
    /// A high-quality copy counts as Hq under its base id, and nothing from these copies counts as
    /// collectable. Base id 0 is an empty slot and never counts, even if the manifest lists 0.
    /// </remarks>
    public static void AddStoredId(
        Dictionary<uint, ItemTally> cached,
        IReadOnlySet<uint> manifestIds,
        uint storedId,
        uint quantity)
    {
        var (baseId, isHq) = GameItemId.Split(storedId);
        var copies = ItemTally.ForQuality(quantity, isHq, isCollectable: false);

        AddIfListed(cached, manifestIds, baseId, copies);
    }

    /// <summary>
    /// Tallies one glamour dresser slot of the item finder's copy, expanding a stored outfit into its
    /// pieces.
    /// </summary>
    /// <param name="cached">The cached tally being built this pass.</param>
    /// <param name="manifestIds">The item ids the server asked about.</param>
    /// <param name="setIndex">
    /// An outfit's set id to its pieces, from <see cref="MirageSetIndex.Build"/>.
    /// </param>
    /// <param name="storedId">
    /// The slot's stored id, high-quality encoding included (see <see cref="GameItemId"/>).
    /// </param>
    /// <param name="setBits">The slot's outfit unlock bits, from the array paired with the ids.</param>
    /// <remarks>
    /// A slot holds an item's id or an outfit's set id (a <c>MirageStoreSetItem</c> row), whose stored
    /// pieces the set index and bits name. The slot's id and each stored piece count one copy when the
    /// manifest lists them, so a manifest listing a set id and its pieces counts both. Every piece of
    /// an outfit shares one quality (a game rule), so the pieces take the slot's quality.
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

        var oneCopy = ItemTally.ForQuality(1, isHq, isCollectable: false);

        AddIfListed(cached, manifestIds, baseId, oneCopy);

        // The lookup uses the base id, so a high-quality outfit slot resolves to its set too.
        if (setIndex.TryGetValue(baseId, out var pieces))
        {
            foreach (var pieceId in MirageSetIndex.StoredPieces(pieces, setBits))
                AddIfListed(cached, manifestIds, pieceId, oneCopy);
        }
    }

    // Id 0 is an empty slot; tested first so a manifest listing 0 cannot match it.
    private static void AddIfListed(
        Dictionary<uint, ItemTally> tallies,
        IReadOnlySet<uint> manifestIds,
        uint id,
        ItemTally delta)
    {
        if (id != 0 && manifestIds.Contains(id))
            Add(tallies, id, delta);
    }

    /// <summary>One <see cref="ItemPossession"/> per valid manifest id, zeros included.</summary>
    /// <param name="manifest">The server-controlled list of valid item ids to report on.</param>
    /// <param name="live">
    /// Counts read live this pass: bags, equipped gear, armory chest, crystals and currencies. A
    /// missing id counts as empty.
    /// </param>
    /// <param name="cached">
    /// Counts from the game's local caches: armoire, glamour dresser, saddlebags, retainers. Any
    /// contribution marks the entry stale. A missing id counts as empty.
    /// </param>
    /// <param name="omitWhenUnseen">
    /// The server's omit-when-unseen ids. One in neither tally gets no entry; one either tally holds,
    /// at any count, reports normally. Null, from an older server or none configured, omits nothing.
    /// </param>
    /// <returns>
    /// Possessions in manifest order. An entry is stale when the cache contributed any quality. An
    /// all-zero entry is fresh and carries only <c>Count</c>.
    /// </returns>
    /// <remarks>
    /// Call with named arguments: the two dictionaries share a type, and swapping them silently
    /// inverts every <c>Fresh</c> flag.
    /// </remarks>
    public static IReadOnlyList<ItemPossession> BuildPossessions(
        IReadOnlyList<uint> manifest,
        IReadOnlyDictionary<uint, ItemTally> live,
        IReadOnlyDictionary<uint, ItemTally> cached,
        IReadOnlySet<uint>? omitWhenUnseen = null)
    {
        var result = new List<ItemPossession>(manifest.Count);
        var seen = new HashSet<uint>();

        foreach (var id in manifest)
        {
            if (id == 0 || !seen.Add(id))
                continue;

            // Resolved means present in a tally, at any count: the sources record only ids they saw.
            var liveResolved = live.TryGetValue(id, out var liveTally);
            var cachedResolved = cached.TryGetValue(id, out var cachedTally);

            // See the class remarks. An explicit zero here would overwrite the real count the server
            // already holds.
            if (!liveResolved && !cachedResolved && omitWhenUnseen?.Contains(id) == true)
                continue;

            var total = liveTally.Add(cachedTally);

            // Any cached quality means the count came partly from an earlier scan.
            var fresh = cachedTally.IsEmpty;

            // Count is NQ only; the server decides HQ requirements. Zero HQ and Collectable counts
            // are omitted, per the DTO contract.
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
