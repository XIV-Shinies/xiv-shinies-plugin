using System.Collections.Generic;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Enforces the server contract's payload caps client-side, before an upload is built.
/// </summary>
/// <remarks>
/// The contract caps id lists at 50,000 ids and entry lists (items) at 10,000, and rejects an over-cap
/// payload whole with a 400; truncating keeps the rest alive, and under monotonic writes a later full
/// sweep re-sends what was dropped. The cap is chosen by shape, never by category name, and every cut
/// is reported, since a silent cap would read as complete. No byte size is measured, so these caps
/// are the only guard against the 1 MiB body limit; real payloads sit far below it, and an upload
/// refused with a 413 is dropped as terminal.
/// </remarks>
public static class PayloadCaps
{
    /// <summary>The contract's ceiling for id-list categories.</summary>
    public const int MaxIdsPerCategory = 50_000;

    /// <summary>The contract's ceiling for object-entry categories.</summary>
    public const int MaxEntriesPerCategory = 10_000;

    /// <summary>Caps every over-limit category, returning what was dropped.</summary>
    /// <param name="collections">
    /// The snapshot's collections, keyed by category, shaped like the payload's <c>collections</c>.
    /// </param>
    /// <returns>
    /// <c>Bounded</c> is the capped dictionary, the same instance when nothing was cut.
    /// <c>Dropped</c> has one <see cref="PayloadCapDrop"/> per cut category.
    /// </returns>
    /// <remarks>
    /// <b>Not for the upload path:</b> it cannot retract the completeness claim, which lives on the
    /// snapshot, so its output would ship a shortened list declared complete. Only
    /// <c>PayloadCapsTests</c> calls it.
    /// </remarks>
    internal static (Dictionary<string, JsonNode> Bounded, IReadOnlyList<PayloadCapDrop> Dropped)
        Bound(Dictionary<string, JsonNode> collections)
    {
        var (bounded, dropped, _) = BoundCore(collections);
        return (bounded, dropped);
    }

    /// <summary>
    /// Caps every over-limit category of a whole snapshot, and <b>retracts the completeness claim
    /// and the store confirmation of every category it had to cut</b>.
    /// </summary>
    /// <returns>
    /// <c>Bounded</c> is the capped snapshot, the same instance when nothing was cut.
    /// <c>Dropped</c> has one <see cref="PayloadCapDrop"/> per cut category.
    /// </returns>
    /// <remarks>
    /// Capping and retracting share one function so they cannot drift apart: a truncated list cannot
    /// carry the claim on <see cref="Collectors.CollectResult.CompleteEnumeration"/>. Dropping ids is
    /// safe under monotonic writes; declaring a shortened list complete is not.
    /// </remarks>
    public static (CollectionSnapshot Bounded, IReadOnlyList<PayloadCapDrop> Dropped)
        Bound(CollectionSnapshot snapshot)
    {
        var (bounded, dropped, truncatedKeys) = BoundCore(snapshot.Collections);

        if (dropped.Count == 0)
            return (snapshot, dropped);

        // Copied, because other code may already hold the snapshot and its set.
        var completeKeys = new HashSet<string>(snapshot.CompleteKeys);
        completeKeys.ExceptWith(truncatedKeys);

        // A store of the shortened list does not cover the entries cut from it.
        var storeConfirmations = new Dictionary<string, StoreConfirmation>(snapshot.StoreConfirmations);
        foreach (var key in truncatedKeys)
            storeConfirmations.Remove(key);

        return (snapshot with
        {
            Collections = bounded,
            CompleteKeys = completeKeys,
            StoreConfirmations = storeConfirmations,
        }, dropped);
    }

    /// <summary>
    /// Does the capping, and reports the keys it cut as well as one <see cref="PayloadCapDrop"/>
    /// per cut.
    /// </summary>
    private static (Dictionary<string, JsonNode> Bounded, IReadOnlyList<PayloadCapDrop> Dropped,
        IReadOnlyCollection<string> TruncatedKeys) BoundCore(Dictionary<string, JsonNode> collections)
    {
        var dropped = new List<PayloadCapDrop>();
        var truncatedKeys = new List<string>();

        // Allocated only on the first cut, so a compliant payload returns the original instance.
        Dictionary<string, JsonNode>? bounded = null;

        foreach (var (key, value) in collections)
        {
            // Only an array can be over a count cap; any other shape is left untouched.
            if (value is not JsonArray array)
                continue;

            if (array.Count == 0)
                continue;

            // Shape, not name, picks the cap: object elements make an entry list, bare numbers an id
            // list. The first element speaks for the array, since collectors never mix shapes.
            var cap = array[0] is JsonObject ? MaxEntriesPerCategory : MaxIdsPerCategory;

            if (array.Count <= cap)
                continue;

            var overBy = array.Count - cap;

            var truncated = new JsonArray();
            for (var i = 0; i < cap; i++)
            {
                // A JsonNode has one parent, so adding `array`'s element as-is throws; the clone is
                // detached. A JSON null element, which collectors never produce, has no node and is skipped.
                if (array[i] is { } element)
                    truncated.Add(element.DeepClone());
            }

            // Copied once, on the first cut; untouched categories keep their original nodes.
            bounded ??= new Dictionary<string, JsonNode>(collections);
            bounded[key] = truncated;

            dropped.Add(new PayloadCapDrop
            {
                CategoryKey = key,
                DroppedEntries = overBy,
                Cap = cap,
            });
            truncatedKeys.Add(key);
        }

        return (bounded ?? collections, dropped, truncatedKeys);
    }
}
