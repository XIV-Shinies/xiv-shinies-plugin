using System;
// BinaryPrimitives reads a number out of raw bytes in a stated byte order, like
// `new DataView(buffer).getUint32(0, true)` in JavaScript.
using System.Buffers.Binary;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>One accepted reading of the game's beast record set.</summary>
/// <param name="Ranks">
/// Each bestiary number with a rank of 1 or more and a well-formed HP record, mapped to that rank.
/// </param>
/// <param name="Leveled">
/// The bestiary numbers whose beast has been raised past where a pact starts: rank 2 or above, or
/// rank 1 with some EXP toward the next rank. Only a beast the character has a pact with can be
/// raised, so these are held whatever the record set stores for a beast without one; a beast at
/// rank 1 with no EXP counts as held only once the bestiary window has shown it so.
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
    /// The record set is filled, but a value is out of range, a raised beast's HP record is
    /// misshapen, or it disagrees with the bestiary window, so it cannot be trusted.
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
/// bestiary order (No. 1 first), then one EXP byte per beast, then one HP record per beast in the
/// same order (see <see cref="IsWellFormedHpRecord"/>). A rank runs 1–25 for a beast the character
/// holds and an EXP figure 0–100 toward the next rank. Dalamud-free, so the unit suite can run it.
/// </para>
/// <para>
/// No published definition names the record set's place in memory, so a game patch can move it.
/// Most moves leave the marker reading something other than 2, but some small ones do not, and when
/// every beast shares a rank and EXP figure the range checks cannot catch those either. The HP
/// records can: a read off by anything but a whole number of HP records almost always breaks their
/// shape, so every beast a reading counts as raised must have a well-formed one. A read off by
/// whole HP records keeps the shape; the marker, the range checks and the agreement with the
/// bestiary window stand against it.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class BeastRankRecord
{
    /// <summary>The value the record set's first four bytes hold once the game has filled it.</summary>
    // A `const` is a value fixed when the code compiles. `uint` is an unsigned 32-bit number, never
    // negative, the type of the marker and of each word in an HP record.
    public const uint FilledMarker = 2;

    /// <summary>The marker's width in bytes, before the first rank.</summary>
    public const int MarkerBytes = 4;

    /// <summary>The highest rank a beast can reach.</summary>
    public const int MaxRank = 25;

    /// <summary>The highest EXP figure the record set holds for a beast.</summary>
    public const int MaxExp = 100;

    /// <summary>The width of one beast's HP record in bytes: five four-byte words.</summary>
    public const int HpRecordBytes = 20;

    /// <summary>Where the HP records begin, for a bestiary of this size.</summary>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <remarks>
    /// Right after one rank byte and one EXP byte per beast. A bestiary that grows without the
    /// record set growing in step puts this, and the EXP figures, in the wrong place. Unless the
    /// growth (ten beasts, or a multiple) shifts the HP records by whole records, they then read
    /// misshapen, which refuses a reading with any raised beast and leaves the rest unranked. A
    /// shift by whole records keeps the shape; the range checks still apply then, since the last
    /// beasts' EXP figures come from the first HP records' bytes.
    /// </remarks>
    public static int HpRecordsStart(int beastCount) => MarkerBytes + (2 * beastCount);

    /// <summary>How many bytes the record set spans for a bestiary of this size.</summary>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    public static int ByteLength(int beastCount) =>
        HpRecordsStart(beastCount) + (HpRecordBytes * beastCount);

    /// <summary>One beast's HP record, out of the record set's bytes.</summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <param name="index">The beast's place in bestiary order, 0 for No. 1.</param>
    // `ReadOnlySpan<byte>` is a read-only view over a block of bytes (an array, or part of one) that
    // costs no copy, much like a `Uint8Array.subarray` in JavaScript; the one handed back here views
    // the caller's own bytes. `Slice(start, length)` makes a view of that many bytes from `start` on.
    public static ReadOnlySpan<byte> HpRecord(ReadOnlySpan<byte> bytes, int beastCount, int index) =>
        bytes.Slice(HpRecordsStart(beastCount) + (HpRecordBytes * index), HpRecordBytes);

    /// <summary>One of an HP record's five words.</summary>
    /// <param name="record">The HP record's bytes.</param>
    /// <param name="word">Which word, 0 for the first.</param>
    // `[n..]` is everything from byte n on, like `slice(n)` on an array but without copying.
    public static uint HpRecordWord(ReadOnlySpan<byte> record, int word) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[(4 * word)..]);

    /// <summary>
    /// Whether an HP record has the shape of the HP record of a beast that holds a pact.
    /// </summary>
    /// <param name="record">The HP record's bytes.</param>
    /// <remarks>
    /// A figure, a larger one, then the max HP, larger still, in each of the last three words. The
    /// rising order rules out an all-zero record. A beast's max HP runs to a few thousand, well
    /// inside two bytes. Read a byte or two early, every figure is multiplied by 256 or more; read a
    /// byte or two late, each word takes the next figure's low bytes as its high ones. Either way the
    /// max HP's word almost always lands past the ceiling, which catches the misread even where the
    /// figures keep their order.
    /// </remarks>
    public static bool IsWellFormedHpRecord(ReadOnlySpan<byte> record)
    {
        var first = HpRecordWord(record, 0);
        var second = HpRecordWord(record, 1);
        var maxHp = HpRecordWord(record, 2);

        // `ushort.MaxValue` is 65,535, the largest number two bytes hold.
        return first < second
            && second < maxHp
            && maxHp <= ushort.MaxValue
            && HpRecordWord(record, 3) == maxHp
            && HpRecordWord(record, 4) == maxHp;
    }

    /// <summary>
    /// Whether the bytes start with the marker the game writes once the record set is filled.
    /// </summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <remarks>
    /// Tells a filled record set refused for its values apart from bytes that do not start with the
    /// marker (unfilled, or moved by a patch), so only the first is reported.
    /// </remarks>
    public static bool IsFilled(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= MarkerBytes && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == FilledMarker;

    /// <summary>Decides what the record set's bytes are worth.</summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <param name="held">The bestiary numbers the window showed as held this session.</param>
    /// <returns>
    /// Not filled; refused, for a value out of range, a raised beast without a well-formed HP
    /// record, or a held beast left unranked; or accepted with the reading.
    /// </returns>
    public static RankRecordAssessment Assess(ReadOnlySpan<byte> bytes, int beastCount, IEnumerable<int> held)
    {
        if (!IsFilled(bytes))
            return new RankRecordAssessment(RankRecordVerdict.NotFilled, null);

        var reading = Read(bytes, beastCount);
        if (reading is null || !AgreesWith(reading.Ranks, held))
            return new RankRecordAssessment(RankRecordVerdict.Refused, null);

        return new RankRecordAssessment(RankRecordVerdict.Accepted, reading);
    }

    /// <summary>Reads each beast's rank out of the record set's bytes.</summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <returns>
    /// The ranks and the leveled beasts, or null when the bytes are unfilled, too short for the
    /// bestiary, hold a value out of range, or lack a well-formed HP record for a raised beast. A
    /// rank of 0 is left out, since it is not a rank, and so is any beast without a well-formed HP
    /// record.
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

        // Every beast is checked before the reading is accepted, so one out-of-range byte, or one
        // raised beast without a well-formed HP record, refuses the whole of it: a single bad value
        // is evidence the layout has moved, which makes the rest untrustworthy too.
        for (var index = 0; index < beastCount; index++)
        {
            // Declared as `int` rather than `var` so each byte widens to an int at once, the type the
            // maps hold and the comparisons below use.
            int rank = rankBytes[index];
            int exp = expBytes[index];
            if (rank > MaxRank || exp > MaxExp)
                return null;

            // Only a beast with a pact can be raised, and such a beast has a well-formed HP record.
            var raised = rank >= 2 || (rank == 1 && exp > 0);
            var hasHpRecord = IsWellFormedHpRecord(HpRecord(bytes, beastCount, index));
            if (raised && !hasHpRecord)
                return null;

            // A beast without a raise or an HP record may hold no pact, so it is left unranked rather
            // than given a rank nothing here has vouched for. If the window shows it as held, Assess
            // refuses the reading for leaving it out.
            if (!hasHpRecord)
                continue;

            // The record set is in bestiary order, so the beast at index 0 is No. 1.
            var number = index + 1;
            if (rank > 0)
                ranks[number] = rank;
            if (raised)
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
    /// A held beast always has a rank of at least 1 and a well-formed HP record, so a held beast
    /// missing from the reading means the bytes are not what they claim to be. With nothing held yet
    /// there is nothing to compare, and the reading's own checks stand alone.
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
