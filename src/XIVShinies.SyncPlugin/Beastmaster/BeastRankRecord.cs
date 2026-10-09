using System;
// BinaryPrimitives reads a number out of raw bytes in a stated byte order, like
// `new DataView(buffer).getUint32(0, true)` in JavaScript.
using System.Buffers.Binary;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>One accepted reading of the game's beast record set.</summary>
/// <param name="Ranks">Each bestiary number with a rank of 1 or more, mapped to that rank.</param>
/// <param name="Leveled">
/// The bestiary numbers whose beast has been raised past where a pact starts: rank 2 or above, or
/// rank 1 with some EXP toward the next rank. Only a beast the character has a pact with can be
/// raised, so these are held whatever the record set stores for a beast without one; a beast at
/// rank 1 with no EXP counts as held only once the bestiary window has shown it so.
/// <see cref="BeastRankRecord.Assess"/> empties it when the layout is not checked for the running
/// game.
/// </param>
// A positional `record` declares its properties in the parentheses and gets a constructor and
// value-based equality written by the compiler, like a frozen object type in TypeScript.
// `IReadOnlyDictionary` and `IReadOnlySet` are read-only views of a map and a set, like TypeScript's
// `ReadonlyMap` and `ReadonlySet`.
public sealed record BeastRankReading(IReadOnlyDictionary<int, int> Ranks, IReadOnlySet<int> Leveled);

/// <summary>What a look at the record set's bytes amounted to.</summary>
// An `enum` is a fixed set of named values, like a TypeScript union of string literals.
public enum RankRecordVerdict
{
    /// <summary>
    /// The first four bytes do not hold the filled marker. They read 0 until the player talks to the
    /// Crucible of the Unbroken's NPC, the ordinary state near the NPC before then; a record set a
    /// patch has moved usually lands here too, since its marker no longer reads 2.
    /// </summary>
    NotFilled,

    /// <summary>
    /// The record set is filled, but a value is out of range or it disagrees with the bestiary
    /// window, so it cannot be trusted.
    /// </summary>
    Refused,

    /// <summary>The record set passed every check.</summary>
    Accepted,
}

/// <summary>A verdict on the record set's bytes, and the reading when it was accepted.</summary>
/// <param name="Verdict">What the bytes amounted to.</param>
/// <param name="Reading">The reading, present exactly when the verdict is Accepted.</param>
public sealed record RankRecordAssessment(RankRecordVerdict Verdict, BeastRankReading? Reading);

/// <summary>
/// Turns the bytes of the game's beast record set into each tamed beast's rank, refusing anything
/// that does not look like that record set.
/// </summary>
/// <remarks>
/// <para>
/// The record set is a marker of 2 (four bytes, little-endian), then one rank byte per beast in
/// bestiary order (No. 1 first), then one EXP byte per beast. A rank runs 1–25 for a beast the
/// character holds and an EXP figure 0–100 toward the next rank.
/// </para>
/// <para>
/// No published definition names the record set's place in memory, so a game patch can move it.
/// Most moves fail the checks (usually the marker, which then reads as unfilled), but a move of a
/// few bytes can pass them all, so a reading proves a pact
/// (<see cref="BeastRankReading.Leveled"/>) only on a game version whose layout has been checked
/// (<see cref="IsLayoutVerified"/>); elsewhere its ranks travel only beside beasts the window
/// showed as held. Dalamud-free, so the unit suite can test it.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class BeastRankRecord
{
    /// <summary>The value the record set's first four bytes hold once the game has filled it.</summary>
    public const uint FilledMarker = 2;

    /// <summary>The marker's width in bytes, before the first rank.</summary>
    public const int MarkerBytes = 4;

    /// <summary>The highest rank a beast can reach.</summary>
    public const int MaxRank = 25;

    /// <summary>The highest EXP figure the record set holds for a beast.</summary>
    public const int MaxExp = 100;

    /// <summary>The bestiary size the record set's layout was checked against.</summary>
    /// <remarks>
    /// The EXP figures start right after one rank byte per beast, so a bestiary that grows without
    /// the record set growing with it would put them somewhere else.
    /// </remarks>
    public const int VerifiedBeastCount = 50;

    /// <summary>The game versions on which the record set's place and layout were checked.</summary>
    /// <remarks>
    /// The version string the running game reports. CLAUDE.md's "Re-checking the beast rank record
    /// after a game patch" says how a version is checked and added here.
    /// </remarks>
    // `static readonly` rather than `const`: a set is built when the program runs, and `const` only
    // holds values fixed when the code compiles, such as numbers and strings.
    public static readonly IReadOnlySet<string> VerifiedGameVersions = new HashSet<string>
    {
        "2026.09.15.0000.0000",
    };

    /// <summary>How many bytes the record set spans for a bestiary of this size.</summary>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    public static int ByteLength(int beastCount) => MarkerBytes + (2 * beastCount);

    /// <summary>
    /// Whether the record set's layout has been checked for this game version and bestiary size.
    /// </summary>
    /// <param name="gameVersion">The running game's version, or null when it could not be read.</param>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    // `string?` marks a string that may be null; the compiler warns where it is used unchecked.
    public static bool IsLayoutVerified(string? gameVersion, int beastCount) =>
        beastCount == VerifiedBeastCount
        && gameVersion is not null
        && VerifiedGameVersions.Contains(gameVersion);

    /// <summary>
    /// Whether the bytes start with the marker the game writes once the record set is filled.
    /// </summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <remarks>
    /// Tells a filled record set refused for its values apart from bytes that do not start with the
    /// marker (unfilled, or moved by a patch), so only the first is reported.
    /// </remarks>
    // `ReadOnlySpan<byte>` is a read-only view over a block of bytes (an array, or part of one) that
    // costs no copy, much like a `Uint8Array.subarray` in JavaScript.
    public static bool IsFilled(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= MarkerBytes && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == FilledMarker;

    /// <summary>Decides what the record set's bytes are worth.</summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <param name="held">The bestiary numbers the window showed as held this session.</param>
    /// <param name="layoutVerified">The answer of <see cref="IsLayoutVerified"/>.</param>
    /// <returns>
    /// Not filled; refused, for a value out of range or a held beast left unranked; or accepted with
    /// the reading, whose leveled set is empty on a layout not checked for this game.
    /// </returns>
    public static RankRecordAssessment Assess(
        ReadOnlySpan<byte> bytes, int beastCount, IEnumerable<int> held, bool layoutVerified)
    {
        if (!IsFilled(bytes))
            return new RankRecordAssessment(RankRecordVerdict.NotFilled, null);

        var reading = Read(bytes, beastCount);
        if (reading is null || !AgreesWith(reading.Ranks, held))
            return new RankRecordAssessment(RankRecordVerdict.Refused, null);

        // `with { ... }` copies the record with the listed property replaced.
        if (!layoutVerified)
            reading = reading with { Leveled = new HashSet<int>() };

        return new RankRecordAssessment(RankRecordVerdict.Accepted, reading);
    }

    /// <summary>Reads each beast's rank out of the record set's bytes.</summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <returns>
    /// The ranks and the leveled beasts, or null when the bytes are unfilled, too short for the
    /// bestiary, or hold a value out of range. A rank of 0 is left out, since it is not a rank.
    /// </returns>
    // `BeastRankReading?` on a class type marks a result that may be null; the compiler warns
    // wherever a caller uses it unchecked.
    public static BeastRankReading? Read(ReadOnlySpan<byte> bytes, int beastCount)
    {
        if (beastCount <= 0 || bytes.Length < ByteLength(beastCount))
            return null;

        // An unfilled record set reads 0 here (see RankRecordVerdict.NotFilled).
        if (!IsFilled(bytes))
            return null;

        // `[start..end]` slices the span, like `slice` on an array but without copying.
        var rankBytes = bytes[MarkerBytes..(MarkerBytes + beastCount)];
        var expBytes = bytes[(MarkerBytes + beastCount)..(MarkerBytes + (2 * beastCount))];

        var ranks = new Dictionary<int, int>();
        var leveled = new HashSet<int>();

        // Every figure is checked before the reading is accepted, so one out-of-range byte refuses the
        // whole of it: a single bad value is evidence the layout has moved, which makes the rest
        // untrustworthy too.
        for (var index = 0; index < beastCount; index++)
        {
            // Declared as `int` rather than `var` so each byte widens to an int at once, the type the
            // maps hold and the comparisons below use.
            int rank = rankBytes[index];
            int exp = expBytes[index];
            if (rank > MaxRank || exp > MaxExp)
                return null;

            // The record set is in bestiary order, so the beast at index 0 is No. 1.
            var number = index + 1;
            if (rank > 0)
                ranks[number] = rank;
            if (rank >= 2 || (rank == 1 && exp > 0))
                leveled.Add(number);
        }

        return new BeastRankReading(ranks, leveled);
    }

    /// <summary>
    /// Whether a reading gives a rank to every beast the bestiary window showed as held.
    /// </summary>
    /// <param name="ranks">The ranks from a reading.</param>
    /// <param name="held">The bestiary numbers the window showed as held this session.</param>
    /// <remarks>
    /// A held beast always has a rank of at least 1, so a held beast missing from the reading means
    /// the bytes are not what they claim to be. With nothing held yet there is nothing to compare,
    /// and the reading's own checks stand alone.
    /// </remarks>
    public static bool AgreesWith(IReadOnlyDictionary<int, int> ranks, IEnumerable<int> held)
    {
        foreach (var number in held)
        {
            if (!ranks.ContainsKey(number))
                return false;
        }

        return true;
    }
}
