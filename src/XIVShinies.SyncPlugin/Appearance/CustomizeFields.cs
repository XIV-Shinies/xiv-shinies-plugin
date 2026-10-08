using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Appearance;

/// <summary>
/// The wire names of the 26 bytes in the game's character customization data, in byte order.
/// </summary>
/// <remarks>
/// <para>
/// <b>A frozen vocabulary.</b> These strings are the keys of the <c>appearance</c> category's
/// <c>customize</c> object, and the server stores each byte under its name. They equal the names of
/// Dalamud's <c>CustomizeIndex</c> enum, but are written out here rather than taken from that enum
/// so that a rename upstream can never change what the plugin sends.
/// </para>
/// <para>
/// <b>Position is meaning.</b> The entry at index <c>i</c> names byte <c>i</c> of the game's
/// <c>CustomizeData</c>, so the order of this table is the byte order. Reordering it would send
/// every moved byte under another byte's name.
/// </para>
/// <para>
/// <b>Five bytes are packed</b>, each carrying a flag in bit 7 (<c>0x80</c>) beside whatever its
/// other seven bits hold. They travel raw, exactly as the game stores them, and the server stores
/// them that way too; XIV Shinies unpacks them when it draws the character:
/// </para>
/// <list type="bullet">
/// <item><description>
/// byte 7, <c>HasHighlights</c>: bit 7 set means highlights are on.
/// </description></item>
/// <item><description>
/// byte 12, <c>FaceFeatures</c>: bits 0–6 switch facial features 1–7 on, and bit 7 is the legacy
/// tattoo.
/// </description></item>
/// <item><description>byte 16, <c>EyeShape</c>: bit 7 is the small iris.</description></item>
/// <item><description>byte 19, <c>LipStyle</c>: bit 7 is lipstick.</description></item>
/// <item><description>
/// byte 24, <c>Facepaint</c>: bit 7 means the face paint is reversed.
/// </description></item>
/// </list>
/// <para>
/// Every value is the game's own raw number. The color bytes (<c>SkinColor</c>, <c>HairColor</c>,
/// <c>LipColor</c> and the rest) hold an index into one of the game's palettes, not a color. It
/// too is stored as sent, and XIV Shinies resolves it to a color when it draws the character.
/// </para>
/// <para>
/// Any change to this table — a new byte, a renamed or reordered entry — changes the layout, as
/// does reading an existing byte differently, so it ships only together with a new payload version
/// (<see cref="AppearanceSnapshot.CurrentVersion"/>): a payload's version is what tells the server
/// which layout it is reading.
/// </para>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports constants.
public static class CustomizeFields
{
    // The table itself. `private` keeps the raw array out of reach of every other class, and
    // `static readonly` makes it one shared array, assigned once, before the class is first used.
    // `readonly` stops the field from pointing at a different array, but would not stop someone
    // writing into this one, which is why only the read-only wrapper below is exposed.
    private static readonly string[] NamesInByteOrder =
    {
        "Race",              // 0
        "Gender",            // 1
        "ModelType",         // 2
        "Height",            // 3
        "Tribe",             // 4
        "FaceType",          // 5
        "HairStyle",         // 6
        "HasHighlights",     // 7, packed
        "SkinColor",         // 8
        "EyeColor",          // 9
        "HairColor",         // 10
        "HairColor2",        // 11
        "FaceFeatures",      // 12, packed
        "FaceFeaturesColor", // 13
        "Eyebrows",          // 14
        "EyeColor2",         // 15
        "EyeShape",          // 16, packed
        "NoseShape",         // 17
        "JawShape",          // 18
        "LipStyle",          // 19, packed
        "LipColor",          // 20
        "RaceFeatureSize",   // 21
        "RaceFeatureType",   // 22
        "BustSize",          // 23
        "Facepaint",         // 24, packed
        "FacepaintColor",    // 25
    };

    /// <summary>The 26 field names, where the name at index <c>i</c> belongs to byte <c>i</c>.</summary>
    // `{ get; } = …` is a get-only property given its value once, before the class is first used.
    // `IReadOnlyList<string>` is close to TypeScript's `ReadonlyArray<string>`: it offers indexing
    // and a Count, and nothing that changes an element. Unlike TypeScript's `readonly`, which the
    // compiler erases, `Array.AsReadOnly` wraps the array in an object that enforces this at run
    // time too: writing through it throws, and it cannot be cast back to the array underneath.
    // The wrapper reads straight through to the array, so nothing is copied.
    //
    // It sits below the array on purpose: static initializers run in the order they appear in the
    // file, so the array has to be filled before this line wraps it.
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(NamesInByteOrder);
}
