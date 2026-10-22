using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>One accepted reading of the game's beast record set.</summary>
/// <param name="Ranks">
/// Each bestiary number with a rank of 1 or more and a well-formed HP record, mapped to that rank.
/// </param>
/// <param name="Leveled">
/// The bestiary numbers whose beast is raised past where a pact starts: rank 2 or above, or rank 1
/// with some EXP. Only a beast with a pact can be raised, so these are held whatever the record set
/// stores for a beast without one; a rank 1 beast with no EXP counts as held only once the bestiary
/// window shows it so.
/// </param>
public sealed record BeastRankReading(IReadOnlyDictionary<int, int> Ranks, IReadOnlySet<int> Leveled);

/// <summary>What a look at the record set's bytes amounted to.</summary>
public enum RankRecordVerdict
{
    /// <summary>
    /// The first four bytes do not hold the filled marker. They read 0 until the player talks to the
    /// Crucible of the Unbroken's NPC, and a record set a patch has moved usually lands here too, since
    /// its marker then reads something other than 2.
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
/// The record set is a marker of 2 (four bytes, little-endian), then one rank byte per beast in
/// bestiary order from No. 1, then one EXP byte per beast, then one HP record per beast in the same
/// order (see <see cref="IsWellFormedHpRecord"/>). A held beast's rank runs 1 to 25 and its EXP 0 to
/// 100. No published definition names its place in memory, so a patch can move it. A small move can
/// keep the marker and, when every beast shares a rank and EXP, pass the range checks too. A read off
/// by anything but whole HP records almost always breaks their shape, so every raised beast must have
/// a well-formed one. A read off by whole HP records keeps the shape, and only the marker, the range
/// checks and agreement with the bestiary window stand against it.
/// </remarks>
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

    /// <summary>The width of one beast's HP record in bytes: five four-byte words.</summary>
    public const int HpRecordBytes = 20;

    /// <summary>Where the HP records begin, for a bestiary of this size.</summary>
    /// <param name="beastCount">How many beasts the bestiary holds, from the game's data.</param>
    /// <remarks>
    /// Right after one rank byte and one EXP byte per beast. A bestiary that grows without the record
    /// set growing in step misplaces this and the EXP figures, so the HP records read misshapen, which
    /// refuses a reading with any raised beast and leaves the rest unranked. Growth by a multiple of ten
    /// beasts shifts them by whole records and keeps the shape, but the range checks still apply, since
    /// the last beasts' EXP figures then come from the first HP records' bytes.
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
    public static ReadOnlySpan<byte> HpRecord(ReadOnlySpan<byte> bytes, int beastCount, int index) =>
        bytes.Slice(HpRecordsStart(beastCount) + (HpRecordBytes * index), HpRecordBytes);

    /// <summary>One of an HP record's five words.</summary>
    /// <param name="record">The HP record's bytes.</param>
    /// <param name="word">Which word, 0 for the first.</param>
    public static uint HpRecordWord(ReadOnlySpan<byte> record, int word) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[(4 * word)..]);

    /// <summary>
    /// Whether an HP record has the shape of the HP record of a beast that holds a pact.
    /// </summary>
    /// <param name="record">The HP record's bytes.</param>
    /// <remarks>
    /// The words are the beast's HP on each of the five Master's Boards in turn. The boards cap the
    /// rank a beast fights at 5, 10, 15, 20 and 25, and the first two field it at about 4/11 and half
    /// of the HP at that cap. So the first three rise, which rules out an all-zero record, and the last
    /// three never fall, as they match at rank 15 or below. The second passes half the third by
    /// rounding at most, which alone refuses a read one word early behind a zero: such a read keeps the
    /// rest of the shape but puts the first figure, over half the second, in the second's place. The
    /// last figure, the HP at the beast's own rank, runs to a few thousand, inside two bytes. A read
    /// a byte or two early (every figure times 256 or more) or late (each word taking the next one's
    /// low bytes as its high ones) almost always pushes it past that ceiling, even where the figures
    /// keep their order.
    /// </remarks>
    public static bool IsWellFormedHpRecord(ReadOnlySpan<byte> record)
    {
        var first = HpRecordWord(record, 0);
        var second = HpRecordWord(record, 1);
        var third = HpRecordWord(record, 2);
        var fourth = HpRecordWord(record, 3);
        var last = HpRecordWord(record, 4);

        // The 16 is a margin past any rounding.
        return first < second
            && second < third
            && second <= (third / 2) + 16
            && third <= fourth
            && fourth <= last
            && last <= ushort.MaxValue;
    }

    /// <summary>
    /// Whether the bytes start with the marker the game writes once the record set is filled.
    /// </summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <remarks>
    /// Tells a filled record set refused for its values apart from bytes without the marker, unfilled
    /// or moved by a patch, so only the first is reported.
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
    /// bestiary, hold a value out of range, or lack a well-formed HP record for a raised beast. A rank
    /// of 0 is left out, as is any beast without a well-formed HP record.
    /// </returns>
    public static BeastRankReading? Read(ReadOnlySpan<byte> bytes, int beastCount)
    {
        if (beastCount <= 0 || bytes.Length < ByteLength(beastCount))
            return null;

        // An unfilled record set reads 0 here (see RankRecordVerdict.NotFilled).
        if (!IsFilled(bytes))
            return null;

        var rankBytes = bytes[MarkerBytes..(MarkerBytes + beastCount)];
        var expBytes = bytes[(MarkerBytes + beastCount)..(MarkerBytes + (2 * beastCount))];

        var ranks = new Dictionary<int, int>();
        var leveled = new HashSet<int>();

        // One bad value refuses the whole reading: it is evidence the layout has moved, which makes
        // the rest untrustworthy too.
        for (var index = 0; index < beastCount; index++)
        {
            int rank = rankBytes[index];
            int exp = expBytes[index];
            if (rank > MaxRank || exp > MaxExp)
                return null;

            // Only a beast with a pact can be raised, and such a beast has a well-formed HP record.
            var raised = rank >= 2 || (rank == 1 && exp > 0);
            var hasHpRecord = IsWellFormedHpRecord(HpRecord(bytes, beastCount, index));
            if (raised && !hasHpRecord)
                return null;

            // An unraised beast with no HP record may hold no pact, so it stays unranked. If the
            // window shows it as held, Assess refuses the reading for leaving it out.
            if (!hasHpRecord)
                continue;

            // Bestiary order: index 0 is No. 1.
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
    /// missing from the reading means the bytes are not what they claim to be. With nothing held yet,
    /// the reading's own checks stand alone.
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
