using System;
using System.Text.Json.Nodes;

namespace XIVShinies.SyncPlugin.Appearance;

/// <summary>The four display toggles that change how the character's gear is drawn.</summary>
/// <param name="WeaponHidden">True when the character's weapon is hidden.</param>
/// <param name="HatHidden">True when the character's headgear is hidden.</param>
/// <param name="VisorToggled">True when the headgear's visor is toggled.</param>
/// <param name="VieraEarsHidden">True when a Viera character's ears are hidden.</param>
/// <remarks>
/// <para>
/// A <c>readonly record struct</c> is a small immutable value type: it is copied by value on
/// assignment rather than shared by reference, it compares equal when every field matches, and the
/// positional syntax declares the four properties and the constructor in one line. Four booleans
/// are exactly the kind of small, short-lived value a struct suits — the closest TypeScript picture
/// is a frozen object literal, except that two with the same fields compare equal, which
/// <c>===</c> never says of two TypeScript objects.
/// </para>
/// <para>
/// Four booleans in a row are easy to pass in the wrong order, so build one with named arguments:
/// <c>new AppearanceToggles(WeaponHidden: true, HatHidden: false, …)</c>. A struct also always has
/// a <c>default</c> value — here all four false — which is an ordinary reading, not an unset one.
/// </para>
/// </remarks>
public readonly record struct AppearanceToggles(
    bool WeaponHidden,
    bool HatHidden,
    bool VisorToggled,
    bool VieraEarsHidden);

/// <summary>
/// Assembles the <c>appearance</c> category's facts: the local character's look, as raw values
/// copied out of the game.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure on purpose.</b> Everything here takes plain values and returns a JSON object, with no
/// game service in sight, so every rule below is unit-tested. Reading those values out of the game
/// happens elsewhere and is verified in game.
/// </para>
/// <para>
/// <b>Raw facts only.</b> The plugin reports the bytes the game stores. It does not unpack the
/// packed bytes, turn palette indices into colors, or judge whether a value is plausible. The server
/// stores the bytes as sent, and XIV Shinies unpacks them and resolves the colors when it draws the
/// character (see <see cref="CustomizeFields"/>).
/// </para>
/// <para>
/// <b>Built by hand.</b> The shared serializer policy (<see cref="Api.ApiJson.Options"/>)
/// camel-cases property names and dictionary keys, so a serialized dictionary would send
/// <c>"race"</c> where the contract says <c>"Race"</c>. A <see cref="JsonObject"/> is written
/// exactly as built, so its keys reach the wire as written here.
/// </para>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports plain functions.
public static class AppearanceSnapshot
{
    /// <summary>The payload version this builder writes, sent as the facts' <c>version</c>.</summary>
    /// <remarks>
    /// The version names the layout: which keys exist and what each value means. A game patch that
    /// changes the layout — a new customize byte, a field that means something else — becomes a new
    /// version the server learns to read, so a payload always says which layout it carries.
    /// </remarks>
    // `const` is a compile-time constant, like a top-level `const` holding a literal in TypeScript.
    public const int CurrentVersion = 1;

    // The free company crest's bitfield: one bit per gear slot that can show the crest. The other
    // five bits are unused, and nothing here reads them.
    private const byte CrestOffHand = 0x01;
    private const byte CrestHead = 0x02;
    private const byte CrestBody = 0x04;

    // The glasses slots the game stores. The second is unused in practice; both travel, so the
    // shape already holds if it is ever filled.
    private const int GlassesSlots = 2;

    /// <summary>
    /// Builds the category's facts, or returns null when the customization read is the wrong size.
    /// </summary>
    /// <param name="customize">
    /// The game's customization bytes, one per <see cref="CustomizeFields.Names"/> entry and in the
    /// same order. Any other length is a misread — a layout this code does not know, or a read from
    /// the wrong place — so nothing is built and the caller sends no <c>appearance</c> key (see
    /// <see cref="Collectors.CollectSkipReasons.UnexpectedLayout"/>).
    /// </param>
    /// <param name="toggles">The four display toggles.</param>
    /// <param name="crestBits">
    /// The free company crest bitfield: <c>0x01</c> off-hand, <c>0x02</c> head, <c>0x04</c> body.
    /// </param>
    /// <param name="glasses">
    /// The <c>Glasses</c> sheet row id in each glasses slot. Always sent as exactly two entries: a
    /// missing entry is sent as 0, and entries past the second are ignored. Lenient where
    /// <paramref name="customize"/> is strict, because the risks differ: the second slot is always
    /// 0 in practice, so padding with 0 asserts what the game shows, whereas a customize read of
    /// the wrong length means the byte-to-name mapping itself is unknown.
    /// </param>
    // `ReadOnlySpan<T>` is a read-only window onto memory that already exists — an array, or a
    // run of bytes inside a game structure — with no copy made. JavaScript has no exact match; the
    // closest is a typed-array view such as `new Uint8Array(buffer, offset, length)`, which shares
    // its buffer instead of copying it, except a read-only span can never be written through. A
    // span also lives only on the stack: it cannot be stored in a class's field, captured by a
    // lambda, or kept across an `await`. A plain `byte[]` converts to one automatically, which is
    // how the tests call this.
    //
    // The `?` on the return type `JsonObject?` says the method may return null, which is how it
    // reports a misread.
    public static JsonObject? Build(
        ReadOnlySpan<byte> customize,
        AppearanceToggles toggles,
        byte crestBits,
        ReadOnlySpan<ushort> glasses)
    {
        if (customize.Length != CustomizeFields.Names.Count)
            return null;

        // One key per byte, added in byte order. A JsonObject keeps its keys in the order they were
        // added, so the wire lists them in the game's own order too.
        var customizeNode = new JsonObject();
        for (var i = 0; i < customize.Length; i++)
        {
            // Widened to int before it enters the node: an in-memory JsonValue remembers the exact
            // type it was built from, so a byte-backed node would refuse GetValue<int>() until
            // serialized. The wire output is the same plain JSON number either way.
            customizeNode[CustomizeFields.Names[i]] = (int)customize[i];
        }

        var glassesNode = new JsonArray();
        for (var slot = 0; slot < GlassesSlots; slot++)
        {
            // `condition ? a : b` is the same conditional expression as in TypeScript. Int for the
            // same reason as the customize values above.
            glassesNode.Add(slot < glasses.Length ? (int)glasses[slot] : 0);
        }

        // An object initializer: each `["key"] = value` line sets one key, like writing an object
        // literal in TypeScript. Plain numbers and booleans convert to JSON nodes automatically.
        return new JsonObject
        {
            ["version"] = CurrentVersion,
            ["customize"] = customizeNode,
            ["glasses"] = glassesNode,
            ["fcCrest"] = new JsonObject
            {
                // `&` keeps only the bits both sides share, so the result is non-zero exactly when
                // that slot's bit is set — the same bitwise AND as in TypeScript.
                ["head"] = (crestBits & CrestHead) != 0,
                ["body"] = (crestBits & CrestBody) != 0,
                ["offHand"] = (crestBits & CrestOffHand) != 0,
            },
            ["weaponHidden"] = toggles.WeaponHidden,
            ["hatHidden"] = toggles.HatHidden,
            ["visorToggled"] = toggles.VisorToggled,
            ["vieraEarsHidden"] = toggles.VieraEarsHidden,
        };
    }
}
