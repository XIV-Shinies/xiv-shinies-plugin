using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>The <c>glamour</c> category's facts: the gear the character holds and where.</summary>
/// <remarks>
/// Sent under <c>collections.glamour</c> in <c>POST /api/plugin/v1/sync</c> as <b>current
/// holdings</b>, unlike the categories that only grow: each container's list is all it holds now,
/// and a piece missing from it is treated as gone after the server's grace rules. An unread
/// container stays null, which <see cref="Api.ApiJson.Options"/> omits, so the server keeps what it
/// knew, while <c>[]</c> says its old contents are gone and would erase real holdings if sent for an
/// unread one. A list past the caps withholds the whole category (see <see cref="GlamourSnapshot"/>).
/// </remarks>
public sealed record GlamourFacts
{
    /// <summary>
    /// The shape version, which the server reads first to interpret the rest. A change to what an
    /// existing key means takes a new number, never this one.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>The shape version, sent as <c>version</c>.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Loose dresser pieces, one entry per slot; null (omitted) when the dresser was not read.
    /// </summary>
    /// <remarks>
    /// Two slots holding the same item are two copies and arrive as two entries. A slot holding an
    /// outfit is listed in <see cref="OutfitGlamours"/>, never here.
    /// </remarks>
    public IReadOnlyList<DresserPiece>? Dresser { get; init; }

    /// <summary>
    /// One entry per dresser slot that holds an outfit; null (omitted) when the dresser was not
    /// read.
    /// </summary>
    /// <remarks>
    /// The same outfit stored in two slots arrives as two entries, so an <c>outfitId</c> may
    /// repeat. This is null exactly when <see cref="Dresser"/> is: both lists come from one read of
    /// one container.
    /// </remarks>
    public IReadOnlyList<OutfitGlamour>? OutfitGlamours { get; init; }

    /// <summary>Item ids the Armoire holds; null (omitted) when the Armoire was not read.</summary>
    /// <remarks>
    /// The game fetches the Armoire's contents from its servers only once the player opens it, so
    /// "not read" is the ordinary state early in a session rather than an error.
    /// </remarks>
    public IReadOnlyList<uint>? Armoire { get; init; }

    /// <summary>
    /// Gear held outside the dresser and the Armoire, one entry per item per place, ordered by id,
    /// then place, then retainer key.
    /// </summary>
    /// <remarks>
    /// Always sent: a union of several containers has no "not read" state of its own, so the source
    /// notes and <see cref="Retainers"/> tell the server which places fed it. An item in two places is
    /// two entries, so the server can show where each copy sits, and retainer and market entries are
    /// further split by retainer. Only gear is listed (see <see cref="GlamourGear"/>).
    /// </remarks>
    public required IReadOnlyList<HeldPiece> Held { get; init; }

    /// <summary>
    /// Every retainer whose copies were read this pass, or null (omitted) when none was.
    /// </summary>
    /// <remarks>
    /// A retainer listed here tells the server its <see cref="HeldPlaces.Retainer"/> place is
    /// current, so a piece missing from it has left that retainer; one that carries
    /// <see cref="RetainerEntry.Market"/> does the same for its market listings. The
    /// <see cref="HeldPiece.Retainer"/> keys in <see cref="Held"/> point into this list.
    /// </remarks>
    public IReadOnlyList<RetainerEntry>? Retainers { get; init; }
}

/// <summary>The places a held copy can be in, as the wire names them, in the wire's order.</summary>
public static class HeldPlaces
{
    /// <summary>The four inventory bags.</summary>
    public const string Bags = "bags";

    /// <summary>The armory chest, every slot type but the soul crystals.</summary>
    public const string Armory = "armory";

    /// <summary>The gear the character has on.</summary>
    public const string Equipped = "equipped";

    /// <summary>The chocobo saddlebag, premium half included.</summary>
    public const string Saddlebag = "saddlebag";

    /// <summary>A retainer's bags and the gear it has on; the entry names the retainer.</summary>
    public const string Retainer = "retainer";

    /// <summary>A retainer's market listings; the entry names the retainer.</summary>
    /// <remarks>
    /// Only one retainer's listings are read, credited by <see cref="RetainerRoster.MarketOwner"/>:
    /// none before the session's first summon, and at most the last summoned retainer's after it, so a
    /// piece listed on another retainer can still read as gone. A piece sold since the summon stays
    /// counted, at most until the next summon, while the retainer list's count still matches. That
    /// stale positive beats an outfit cleared while its piece is merely for sale.
    /// </remarks>
    public const string Market = "market";

    /// <summary>Every place, in the order the wire lists them.</summary>
    // Read-only at run time, so no caller can cast it back to the array and reorder the wire.
    public static readonly IReadOnlyList<string> InWireOrder =
        Array.AsReadOnly(new[] { Bags, Armory, Equipped, Saddlebag, Retainer, Market });
}

/// <summary>Copies of one piece of gear held in one place.</summary>
/// <remarks>
/// Declared in the wire's key order: <c>id</c>, <c>place</c>, <c>retainer</c>, <c>count</c>.
/// </remarks>
public sealed record HeldPiece
{
    /// <summary>The piece's base item id (the high-quality encoding already removed).</summary>
    public required uint Id { get; init; }

    /// <summary>Where these copies are, one of the <see cref="HeldPlaces"/> values.</summary>
    public required string Place { get; init; }

    /// <summary>
    /// For a <see cref="HeldPlaces.Retainer"/> or <see cref="HeldPlaces.Market"/> entry, the
    /// <see cref="RetainerEntry.Key"/> of the retainer that holds or listed the copies; null
    /// (omitted) for every other place.
    /// </summary>
    public int? Retainer { get; init; }

    /// <summary>
    /// How many copies this place holds, at least 1, summed across its slots at every quality.
    /// </summary>
    /// <remarks>
    /// Always sent, even at 1: every outfit glamour needs a copy of its own, so the count says how
    /// many it can fill.
    /// </remarks>
    public required uint Count { get; init; }
}

/// <summary>One retainer whose copies were read this pass.</summary>
public sealed record RetainerEntry
{
    /// <summary>
    /// The number held entries use to name this retainer, 1 to 10, unique within one upload.
    /// </summary>
    /// <remarks>
    /// Local to the upload: it keeps every held entry short, and the stable identity is
    /// <see cref="Id"/>.
    /// </remarks>
    public required int Key { get; init; }

    /// <summary>
    /// The retainer's id as a SHA-256 digest in 64 lowercase hex characters (see
    /// <see cref="RetainerIdHash"/>). The game's own id never leaves the plugin.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The retainer's name, or null (omitted) when the game has no usable one.</summary>
    /// <remarks>
    /// There is none until the game loads the retainer list this session, and a blank name or one
    /// past <see cref="GlamourCaps.RetainerName"/> is left off. The server keeps the last name it
    /// received.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>
    /// True on the one retainer whose market listings were read this pass; null (omitted) on every
    /// other.
    /// </summary>
    /// <remarks>
    /// Sent even when that retainer lists no gear, which is how the server tells "listed nothing"
    /// from "not read". Null rather than false, so only <c>"market":true</c> ever reaches the wire.
    /// </remarks>
    public bool? Market { get; init; }
}

/// <summary>One loose piece stored in one glamour dresser slot.</summary>
public sealed record DresserPiece
{
    /// <summary>The piece's base item id (the high-quality encoding already removed).</summary>
    public required uint Id { get; init; }

    /// <summary>True for a high-quality copy; null (omitted) otherwise.</summary>
    /// <remarks>
    /// Null rather than false, so the key appears only as <c>"hq":true</c> and never on a
    /// normal-quality piece.
    /// </remarks>
    public bool? Hq { get; init; }

    /// <summary>
    /// Both dye channels (0 = undyed), or null (omitted) when they could not be read this pass.
    /// </summary>
    /// <remarks>
    /// The game keeps dyes only in a live copy of the dresser, loaded in the zone where the dresser
    /// was last opened; elsewhere they are unknown and the piece is sent without them, so an
    /// omitted <c>stains</c> means "not read", never "undyed". Always two numbers, first channel
    /// then second, as <c>int</c>s: the game stores bytes, but a <c>byte[]</c> would serialize as a
    /// base64 string.
    /// </remarks>
    public IReadOnlyList<int>? Stains { get; init; }
}

/// <summary>One dresser slot that holds an outfit glamour, and the pieces stored inside it.</summary>
/// <remarks>
/// No quality is sent for an outfit slot: every piece in one outfit glamour shares a single quality
/// (a game rule), and the contract does not ask for it. No dyes are sent either.
/// </remarks>
public sealed record OutfitGlamour
{
    /// <summary>
    /// The outfit's set item id: the <c>MirageStoreSetItem</c> row the slot stores, with the
    /// high-quality encoding removed.
    /// </summary>
    public required uint OutfitId { get; init; }

    /// <summary>The base item ids of the pieces still stored in this outfit, ascending.</summary>
    /// <remarks>
    /// A piece taken out of the outfit is not listed, and each stored item is listed once, whatever
    /// the sheet row's columns hold. The list can be empty: a slot whose pieces were all withdrawn
    /// still stores the outfit.
    /// </remarks>
    public required IReadOnlyList<uint> PieceIds { get; init; }
}
