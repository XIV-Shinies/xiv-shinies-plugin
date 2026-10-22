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
    /// <summary>
    /// Combines two tallies of the same item, such as its live and cached counts, quality by quality.
    /// </summary>
    public ItemTally Add(ItemTally other) =>
        new(Nq + other.Nq, Hq + other.Hq, Collectable + other.Collectable);

    /// <summary>True when no copies were seen in any quality.</summary>
    public bool IsEmpty => Nq == 0 && Hq == 0 && Collectable == 0;
}

/// <summary>Turns scan tallies into wire entries, applying the explicit-zero rule.</summary>
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
    /// <summary>One <see cref="ItemPossession"/> per valid manifest id, zeros included.</summary>
    /// <param name="manifest">The server-controlled list of valid item ids to report on.</param>
    /// <param name="live">
    /// Counts read live this pass: bags, equipped gear, armoury chest, crystals and currencies. A
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
