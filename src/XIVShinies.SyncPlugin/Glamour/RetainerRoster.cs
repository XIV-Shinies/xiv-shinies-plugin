using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>
/// The retainers one pass read, as the upload's <c>retainers</c> list, plus the lookups the held
/// entries need to name them.
/// </summary>
/// <remarks>
/// Keys run from 1 in digest order, so the same retainers keep the same keys pass after pass, whatever
/// order the game lists them in, and two uploads of unchanged storage are identical. Only a retainer
/// whose saved copy was read is listed, since listing one tells the server its retainer place is
/// current; a retainer known only from its market listings is left off and its listings go unreported
/// that pass.
/// </remarks>
public sealed class RetainerRoster
{
    // Raw retainer id to key. Raw ids stay in this object and never reach the wire.
    private readonly IReadOnlyDictionary<ulong, int> keysById;

    // Only Build and Empty make a roster, so its entries, keys and market key always agree.
    private RetainerRoster(
        IReadOnlyList<RetainerEntry> entries, IReadOnlyDictionary<ulong, int> keysById, int? marketKey)
    {
        Entries = entries;
        this.keysById = keysById;
        MarketKey = marketKey;
    }

    /// <summary>A roster for a pass that read no retainer.</summary>
    public static RetainerRoster Empty { get; } =
        new(Array.Empty<RetainerEntry>(), new Dictionary<ulong, int>(), marketKey: null);

    /// <summary>
    /// The retainer whose market listings were read this pass, or null when no retainer can be
    /// credited with them.
    /// </summary>
    /// <param name="marketRead">Whether the market listings container was loaded and read.</param>
    /// <param name="listingsRead">How many occupied listing slots that read found.</param>
    /// <param name="lastSelectedRetainerId">
    /// The retainer the game says was selected last at a summoning bell; 0 when none was.
    /// </param>
    /// <param name="listingCounts">
    /// How many listings the game's retainer list holds for each retainer, by raw id; empty when
    /// the game has not loaded that list this session.
    /// </param>
    /// <remarks>
    /// A relog clears the selection and empties the container, so no owner is named until a retainer
    /// is summoned. Crediting the wrong retainer would mark its listings current and clear the real
    /// ones, so the owner also counts only when the listings read equal the retainer list's count for
    /// it, both zero included, which is a genuine "listed nothing". Nothing guarantees the container
    /// and the selection agree, so any mismatch leaves the listings unreported that pass.
    /// </remarks>
    public static ulong? MarketOwner(
        bool marketRead,
        int listingsRead,
        ulong lastSelectedRetainerId,
        IReadOnlyDictionary<ulong, int> listingCounts) =>
        marketRead
        && lastSelectedRetainerId != 0
        && listingCounts.TryGetValue(lastSelectedRetainerId, out var expected)
        && expected == listingsRead
            ? lastSelectedRetainerId
            : null;

    /// <summary>The <c>retainers</c> entries, in key order.</summary>
    public IReadOnlyList<RetainerEntry> Entries { get; }

    /// <summary>
    /// The key of the retainer whose market listings were read, or null when no listings can be
    /// tied to a listed retainer this pass.
    /// </summary>
    public int? MarketKey { get; }

    /// <summary>The key given to a retainer, or null when it is not on the roster.</summary>
    /// <param name="retainerId">The retainer's raw id.</param>
    public int? KeyOf(ulong retainerId) =>
        keysById.TryGetValue(retainerId, out var key) ? key : (int?)null;

    /// <summary>Builds the roster for one pass.</summary>
    /// <param name="readRetainerIds">
    /// The ids of the retainers whose saved copies were read this pass. Zero (an unused entry) and
    /// repeats are ignored.
    /// </param>
    /// <param name="names">
    /// The name the game holds for each retainer it has loaded, by id; empty when the game has not
    /// loaded the retainer list this session. Only retainers in <paramref name="readRetainerIds"/>
    /// are listed, whoever else is named. A blank name, or one longer than
    /// <see cref="GlamourCaps.RetainerName"/>, is left off.
    /// </param>
    /// <param name="marketRetainerId">
    /// The retainer whose market listings were read this pass (see <see cref="MarketOwner"/>), or
    /// null when none can be credited with them.
    /// </param>
    public static RetainerRoster Build(
        IEnumerable<ulong> readRetainerIds,
        IReadOnlyDictionary<ulong, string> names,
        ulong? marketRetainerId)
    {
        // Ordinal, so the key order never depends on the machine's culture settings.
        var hashed = readRetainerIds
            .Where(id => id != 0)
            .Distinct()
            .Select(id => (Id: id, Digest: RetainerIdHash.Compute(id)))
            .OrderBy(pair => pair.Digest, StringComparer.Ordinal)
            .ToList();

        var entries = new List<RetainerEntry>(hashed.Count);
        var keysById = new Dictionary<ulong, int>(hashed.Count);
        int? marketKey = null;

        for (var index = 0; index < hashed.Count; index++)
        {
            var (id, digest) = hashed[index];
            var key = index + 1;
            var isMarket = marketRetainerId == id;

            entries.Add(new RetainerEntry
            {
                Key = key,
                Id = digest,
                Name = UsableName(names, id),

                // Sent on the market retainer only (see RetainerEntry.Market).
                Market = isMarket ? true : null,
            });

            keysById[id] = key;
            if (isMarket)
                marketKey = key;
        }

        return new RetainerRoster(entries, keysById, marketKey);
    }

    // A name past the server's limit would get the whole glamour key rejected. No game name comes
    // near it, so a longer one is a misread and is left off, not cut short.
    private static string? UsableName(IReadOnlyDictionary<ulong, string> names, ulong id)
    {
        if (!names.TryGetValue(id, out var name))
            return null;

        return string.IsNullOrWhiteSpace(name) || name.Length > GlamourCaps.RetainerName ? null : name;
    }
}
