using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>
/// The retainers one pass read, as the upload's <c>retainers</c> list, plus the lookups the held
/// entries need to name them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure on purpose.</b> It takes the plain values the collector copies out of the game (the ids
/// of the retainers whose saved copies were read, the names the game has loaded, the retainer whose
/// listings were read) and decides everything else, so every rule is unit-tested.
/// </para>
/// <para>
/// <b>Keys follow the hashed ids.</b> Each retainer gets a key from 1 upward in the order of its
/// digest, so the same retainers get the same keys pass after pass, whatever order the game hands
/// them over in, and two uploads of unchanged storage are identical.
/// </para>
/// <para>
/// <b>Only a retainer whose saved copy was read is listed.</b> Listing a retainer tells the server
/// its retainer place is current, so a retainer known only from its market listings cannot be
/// listed for them alone: its listings go unreported that pass rather than have a retainer whose
/// copy was never read stand as current.
/// </para>
/// </remarks>
// A plain `sealed class` with a private constructor rather than a record like its neighbors:
// nothing compares or copies a roster, and making it only through Build and Empty keeps every
// roster consistent.
public sealed class RetainerRoster
{
    // Raw retainer id → its key. The raw ids stay inside this object and never reach the wire.
    private readonly IReadOnlyDictionary<ulong, int> keysById;

    // Private: a roster is made only through Build or Empty below.
    private RetainerRoster(
        IReadOnlyList<RetainerEntry> entries, IReadOnlyDictionary<ulong, int> keysById, int? marketKey)
    {
        Entries = entries;
        this.keysById = keysById;
        MarketKey = marketKey;
    }

    /// <summary>A roster for a pass that read no retainer.</summary>
    // `{ get; } = …` is a get-only static property given its value once, before the class is first
    // used, so every caller shares the one empty roster. `new(...)` with no type name is a
    // target-typed `new`: the compiler takes the type from the property, RetainerRoster. The
    // `marketKey:` label names the argument it fills, so a bare `null` cannot be mistaken.
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
    /// How many listings the game's retainer list holds for each retainer, by raw id; empty when the
    /// game has not loaded that list this session.
    /// </param>
    /// <remarks>
    /// The container holds one retainer's listings, and the game names the retainer selected last,
    /// but the two can disagree: the selection can outlast the listings (a relog empties the
    /// container but may keep the selection), and the container can keep an earlier retainer's
    /// listings when the next one summoned lists nothing. Crediting the wrong retainer would tell
    /// the server that retainer's listings are current and clear the real ones, so the owner is
    /// trusted only when the number of listings read equals the count the retainer list holds for
    /// it. Any disagreement leaves the listings unreported for the pass, which is the safe outcome.
    /// Both at zero is a genuine "listed nothing".
    /// </remarks>
    // `out var expected` declares the variable TryGetValue fills; `&&` stops at the first false, so
    // it is only read once the retainer is known to be in the list.
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
    // `TryGetValue(..., out var key)` fills `key` when the id is present. `? key : null` turns a
    // miss into null; the `(int?)` cast gives both branches the same nullable type.
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
        // Each distinct real id with its digest, in digest order. `(Id: id, Digest: …)` builds a
        // named tuple, a small value with labeled fields like a `{ id, digest }` object in
        // TypeScript. `StringComparer.Ordinal` compares character codes, so the order never depends
        // on the machine's culture settings.
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
            // `var (id, digest) = …` unpacks the tuple into two variables, like destructuring
            // `const { id, digest } = …` in TypeScript.
            var (id, digest) = hashed[index];
            var key = index + 1;
            var isMarket = marketRetainerId == id;

            entries.Add(new RetainerEntry
            {
                Key = key,
                Id = digest,
                Name = UsableName(names, id),

                // `? true : null`: the flag is sent on the market retainer only (see
                // RetainerEntry.Market).
                Market = isMarket ? true : null,
            });

            keysById[id] = key;
            if (isMarket)
                marketKey = key;
        }

        return new RetainerRoster(entries, keysById, marketKey);
    }

    // The retainer's name when the game has a usable one, otherwise null. A name past the server's
    // limit would get the whole glamour key rejected, and a game name never comes near it, so a
    // longer one is a misread and is left off rather than cut short.
    private static string? UsableName(IReadOnlyDictionary<ulong, string> names, ulong id)
    {
        if (!names.TryGetValue(id, out var name))
            return null;

        return string.IsNullOrWhiteSpace(name) || name.Length > GlamourCaps.RetainerName ? null : name;
    }
}
