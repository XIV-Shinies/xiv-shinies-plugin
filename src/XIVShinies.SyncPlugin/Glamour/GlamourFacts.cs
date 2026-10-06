using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>The <c>glamour</c> category's facts: the gear the character holds and where.</summary>
/// <remarks>
/// <para>
/// This travels under <c>collections.glamour</c> in <c>POST /api/plugin/v1/sync</c>. Unlike most
/// categories, which only ever grow, the server reads it as <b>current holdings</b>: for every
/// container this object reports, the list is the whole of what that container holds right now,
/// and a piece the server knew about that is missing from it is treated as gone (the server applies
/// its own grace rules before acting on that).
/// </para>
/// <para>
/// That makes the difference between <b>absent</b> and <b>empty</b> load-bearing. A container the
/// plugin could not read is left <c>null</c>, which the shared serializer policy
/// (<see cref="Api.ApiJson.Options"/>) omits from the JSON entirely, so the server keeps what it
/// already knew. A container that was read and holds nothing is an empty list, which serializes as
/// <c>[]</c> and tells the server its old contents are gone. Sending <c>[]</c> for an unread
/// container would therefore erase real holdings, which is why every per-container list below is
/// nullable.
/// </para>
/// <para>
/// The lists are never truncated to fit the server's caps; <see cref="GlamourSnapshot"/>'s class
/// remarks say why the whole category is withheld instead.
/// </para>
/// </remarks>
// A `record` is a class built for holding data: the compiler writes value-based equality and a
// readable ToString, and `with` expressions copy-and-tweak (`facts with { Armoire = null }`), much
// like object spread in TypeScript. Value-based equality compares each property, and a list
// property compares by reference (the same list instance), not by contents — fine for a wire shape,
// and the reason the tests compare serialized JSON rather than records.
//
// `sealed` means no other class can inherit from this one. `init` means a property can be set only
// while the object is being created (in the `new GlamourFacts { ... }` initializer), never
// reassigned afterwards — like a `readonly` field in a TypeScript interface.
public sealed record GlamourFacts
{
    /// <summary>The payload version this shape belongs to. Bumped, never redefined.</summary>
    /// <remarks>
    /// The server reads <c>version</c> first to know how to interpret the rest. A change to what an
    /// existing key means gets a new number rather than quietly reusing this one, so a server can
    /// always tell which shape it was sent.
    /// </remarks>
    // `const` is a compile-time constant, like a module-level `const` in TypeScript. Being `public`
    // on the type lets callers and tests refer to it as `GlamourFacts.CurrentVersion`.
    public const int CurrentVersion = 1;

    /// <summary>
    /// The shape version, serialized as <c>version</c>. Defaults to <see cref="CurrentVersion"/>.
    /// </summary>
    // `= CurrentVersion` is a property initializer: the default value an object gets when the
    // creator does not set the property, like a default in a TypeScript class field.
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Loose dresser pieces, one entry per dresser slot; null (omitted) when the dresser was not
    /// read.
    /// </summary>
    /// <remarks>
    /// One entry per <b>slot</b>, not per item: two slots holding the same item are two copies and
    /// arrive as two entries. A slot holding an outfit is never listed here — it arrives in
    /// <see cref="OutfitGlamours"/> instead.
    /// </remarks>
    // `IReadOnlyList<T>` is a list the receiver can index and count but not modify — the closest
    // match to TypeScript's `readonly T[]`. The `?` after it marks the reference as allowed to be
    // null (nullable reference types are on in this project, so a reference WITHOUT `?` promises
    // never to be null, much like TypeScript's strictNullChecks).
    public IReadOnlyList<DresserPiece>? Dresser { get; init; }

    /// <summary>
    /// One entry per dresser slot that holds an outfit; null (omitted) when the dresser was not
    /// read.
    /// </summary>
    /// <remarks>
    /// The same outfit stored in two slots arrives as two entries, so an <c>outfitId</c> may repeat.
    /// This is null exactly when <see cref="Dresser"/> is: both lists come from one read of one
    /// container.
    /// </remarks>
    public IReadOnlyList<OutfitGlamour>? OutfitGlamours { get; init; }

    /// <summary>Item ids the Armoire holds; null (omitted) when the Armoire was not read.</summary>
    /// <remarks>
    /// The game fetches the Armoire's contents from its servers only once the player opens it, so
    /// "not read" is the ordinary state early in a session rather than an error.
    /// </remarks>
    public IReadOnlyList<uint>? Armoire { get; init; }

    /// <summary>
    /// Equippable pieces held in bags, equipped, the armory chest, the saddlebag, retainers and the
    /// last-summoned retainer's market listings, one entry per item with how many copies are held,
    /// ordered by id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one list here that is always present (<c>required</c>, never null): rather than the
    /// contents of a single container, it is a union of the gear held across several, so it has no
    /// "not read" state of its own.
    /// </para>
    /// <para>
    /// Each item appears once, under its base id, with its copies summed across every container
    /// that holds one (see <see cref="HeldPiece.Count"/>). The count matters because every outfit
    /// glamour needs a copy of its own. Only gear is listed; materials, consumables and other
    /// non-equippable items are left out.
    /// </para>
    /// </remarks>
    // `required` makes the compiler refuse to build a GlamourFacts unless this property is set in
    // the initializer — a compile-time guarantee, rather than a runtime check, that it is never
    // forgotten.
    public required IReadOnlyList<HeldPiece> Held { get; init; }
}

/// <summary>One piece of gear the character holds, and how many copies of it.</summary>
/// <remarks>
/// The glamour dresser and the Armoire are reported in their own lists, and their copies are not
/// included here.
/// </remarks>
public sealed record HeldPiece
{
    /// <summary>The piece's base item id (the high-quality encoding already removed).</summary>
    // `uint` is an unsigned 32-bit integer, so a negative id is unrepresentable.
    public required uint Id { get; init; }

    /// <summary>
    /// How many copies are held, at least 1: summed across bags, equipped gear, the armory chest,
    /// the saddlebag, retainers' bags and equipped gear, and a retainer's market listings, at every
    /// quality.
    /// </summary>
    /// <remarks>
    /// Always sent, even at 1: the number of copies is what says how many outfit glamours they can
    /// fill.
    /// </remarks>
    public required uint Count { get; init; }
}

/// <summary>One loose piece stored in one glamour dresser slot.</summary>
public sealed record DresserPiece
{
    /// <summary>The piece's base item id (the high-quality encoding already removed).</summary>
    public required uint Id { get; init; }

    /// <summary>True for a high-quality copy; null (omitted) otherwise.</summary>
    /// <remarks>
    /// <para>
    /// <c>bool?</c> is a <b>nullable value type</b>: a plain <c>bool</c> can only be true or false,
    /// and the <c>?</c> adds a third state, null — the C# counterpart of <c>boolean | null</c> in
    /// TypeScript. (For value types like <c>bool</c>, <c>int</c> and <c>uint</c> the <c>?</c>
    /// really changes the type into <c>Nullable&lt;bool&gt;</c>; for reference types like lists it
    /// is only an annotation for the compiler's null checks.)
    /// </para>
    /// <para>
    /// The third state is what keeps normal quality off the wire. The serializer omits nulls but
    /// would write a plain <c>false</c> as <c>"hq":false</c> on every normal-quality piece, so a
    /// normal-quality copy is null here and the key appears only as <c>"hq":true</c>.
    /// </para>
    /// </remarks>
    public bool? Hq { get; init; }

    /// <summary>
    /// Both dye channels (0 = undyed), or null (omitted) when they could not be read this pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game keeps a slot's dyes only in a live copy of the dresser that is loaded in the zone
    /// where the dresser was last opened; everywhere else the dyes are simply unknown. A piece is
    /// still reported without them — the piece itself is a known fact — and an omitted
    /// <c>stains</c> tells the server "not read", never "undyed". An undyed channel that WAS read
    /// is the number 0.
    /// </para>
    /// <para>
    /// Always two numbers, first channel then second, as plain <c>int</c>s. The game stores each
    /// channel as a byte, but a property declared as <c>byte[]</c> is written as a base64 string
    /// instead of numbers. An <c>int</c> list is always written as JSON numbers, and it keeps this
    /// wire type independent of how the bytes happened to be read.
    /// </para>
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
    /// The outfit's set item id — the <c>MirageStoreSetItem</c> row the slot stores, with the
    /// high-quality encoding removed.
    /// </summary>
    public required uint OutfitId { get; init; }

    /// <summary>
    /// The base item ids of the pieces currently stored in this outfit, ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the pieces still inside it: a piece the player has taken out of the outfit is not
    /// listed. Each stored item is listed once, whatever the row's columns hold, because the list
    /// says which pieces the outfit holds rather than what each column says.
    /// </para>
    /// <para>
    /// The list can be empty. A slot whose pieces have all been withdrawn still stores the outfit,
    /// and it is reported as the game stores it.
    /// </para>
    /// </remarks>
    public required IReadOnlyList<uint> PieceIds { get; init; }
}
