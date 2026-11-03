using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Appearance;

/// <summary>
/// The wire names of the 26 bytes in the game's character customization data, in byte order.
/// </summary>
/// <remarks>
/// Written out rather than taken from Dalamud's <c>CustomizeIndex</c> enum, so an upstream rename
/// cannot change the wire. Every byte travels raw: the color bytes hold palette indices, and five
/// bytes pack a flag into bit 7 (marked below). Any change to this table, or to how a byte is read,
/// needs a new <see cref="AppearanceSnapshot.CurrentVersion"/>.
/// </remarks>
public static class CustomizeFields
{
    // Entry i names byte i of the game's CustomizeData, so reordering sends bytes under other names.
    private static readonly string[] NamesInByteOrder =
    {
        "Race",              // 0
        "Gender",            // 1
        "ModelType",         // 2
        "Height",            // 3
        "Tribe",             // 4
        "FaceType",          // 5
        "HairStyle",         // 6
        "HasHighlights",     // 7, packed: bit 7 means highlights are on
        "SkinColor",         // 8
        "EyeColor",          // 9
        "HairColor",         // 10
        "HairColor2",        // 11
        "FaceFeatures",      // 12, packed: bits 0-6 are features 1-7, bit 7 the legacy tattoo
        "FaceFeaturesColor", // 13
        "Eyebrows",          // 14
        "EyeColor2",         // 15
        "EyeShape",          // 16, packed: bit 7 is the small iris
        "NoseShape",         // 17
        "JawShape",          // 18
        "LipStyle",          // 19, packed: bit 7 is lipstick
        "LipColor",          // 20
        "RaceFeatureSize",   // 21
        "RaceFeatureType",   // 22
        "BustSize",          // 23
        "Facepaint",         // 24, packed: bit 7 means the face paint is reversed
        "FacepaintColor",    // 25
    };

    /// <summary>The 26 field names, where the name at index <c>i</c> belongs to byte <c>i</c>.</summary>
    // Must stay below the array: static initializers run in file order.
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(NamesInByteOrder);
}
