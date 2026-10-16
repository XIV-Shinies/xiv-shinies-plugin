using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// The beast record set sits where no published definition names it, so these pin both halves of the
// reader: a record set laid out as the game holds it yields the rank of each beast its HP records
// vouch for, and one that is not yields nothing.
// Each `[Fact]` is one test, like `it(...)` in Jest; a `[Theory]` runs once per `[InlineData]` row,
// like `it.each`. `uint` is an unsigned 32-bit number, never negative, the type of the marker and of
// each word in an HP record.
public class BeastRankRecordTests
{
    /// <summary>
    /// A record set's bytes: the marker, then the ranks, then the EXP figures, then a well-formed HP
    /// record for every beast.
    /// </summary>
    // `params byte[] ranks` lets a caller list the ranks one by one, like a rest parameter
    // (`...ranks`) in JavaScript.
    private static byte[] RecordSet(uint marker, byte[] exp, params byte[] ranks)
    {
        var bytes = new byte[BeastRankRecord.ByteLength(ranks.Length)];

        // Written little-endian, the byte order the reader expects, whatever the machine's own.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, marker);
        ranks.CopyTo(bytes, BeastRankRecord.MarkerBytes);
        exp.CopyTo(bytes, BeastRankRecord.MarkerBytes + ranks.Length);

        // Each beast gets its own HP, as each species has in the game, in the proportions a beast at
        // rank 5 or below shows: the first figure 4/11 of it, the second half, the last three whole.
        // `(uint)` converts the int arithmetic to the `uint` words the record holds.
        for (var number = 1; number <= ranks.Length; number++)
        {
            var hp = (uint)(1500 + (10 * number));
            WriteHpRecord(bytes, ranks.Length, number, hp * 4 / 11, hp / 2, hp, hp, hp);
        }

        return bytes;
    }

    /// <summary>Overwrites one beast's HP record with the five words given.</summary>
    private static void WriteHpRecord(byte[] bytes, int beastCount, int number, params uint[] words)
    {
        var start = BeastRankRecord.HpRecordsStart(beastCount)
            + (BeastRankRecord.HpRecordBytes * (number - 1));

        // `AsSpan(offset)` is a view of the array from that offset on, so each word lands in place.
        for (var word = 0; word < words.Length; word++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start + (4 * word)), words[word]);
    }

    /// <summary>A filled record set with the given ranks and every EXP figure at 0.</summary>
    private static byte[] Filled(params byte[] ranks) =>
        RecordSet(BeastRankRecord.FilledMarker, new byte[ranks.Length], ranks);

    [Fact]
    public void Each_beast_maps_to_its_rank_in_bestiary_order()
    {
        var reading = BeastRankRecord.Read(Filled(9, 6, 3), beastCount: 3);

        // `!` tells the compiler a possibly-null value is present; the assertion above it is what
        // makes that true.
        Assert.NotNull(reading);
        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = 6, [3] = 3 }, reading!.Ranks);
    }

    // 0 is not a rank, and the contract accepts none below 1, so a beast at 0 is left out of the
    // ranks.
    [Fact]
    public void A_beast_with_no_rank_is_left_out()
    {
        var reading = BeastRankRecord.Read(Filled(9, 0, 3), beastCount: 3);

        Assert.Equal(new Dictionary<int, int> { [1] = 9, [3] = 3 }, reading!.Ranks);
    }

    // The game leaves the marker at 0 until the player talks to the NPC: no reading, and no error.
    [Fact]
    public void An_unfilled_record_set_is_no_reading()
    {
        var bytes = RecordSet(0, new byte[3], 9, 6, 3);

        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 3));
    }

    // An out-of-range rank or EXP figure is one sign the record set has moved. One bad value
    // refuses every rank, because it means none of them can be trusted.
    [Fact]
    public void A_rank_above_the_highest_refuses_the_whole_reading()
    {
        Assert.Null(BeastRankRecord.Read(Filled(9, 26, 3), beastCount: 3));
    }

    [Fact]
    public void An_exp_figure_above_the_highest_refuses_the_whole_reading()
    {
        var bytes = RecordSet(BeastRankRecord.FilledMarker, new byte[] { 67, 101, 55 }, 9, 6, 3);

        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 3));
    }

    [Fact]
    public void A_different_marker_refuses_the_reading()
    {
        Assert.Null(BeastRankRecord.Read(RecordSet(3, new byte[3], 9, 6, 3), beastCount: 3));
    }

    [Fact]
    public void Too_few_bytes_for_the_bestiary_refuses_the_reading()
    {
        Assert.Null(BeastRankRecord.Read(Filled(9, 6, 3), beastCount: 4));
    }

    // One byte short of the last HP record is still too few: the reading is refused rather than
    // looked for past the end of the bytes.
    [Fact]
    public void A_record_set_cut_short_inside_its_hp_records_refuses_the_reading()
    {
        // `AsSpan(start, length)` is a view of that many bytes from `start`, like
        // `subarray(start, start + length)`.
        var bytes = Filled(9, 6).AsSpan(0, BeastRankRecord.ByteLength(2) - 1);

        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 2));
    }

    // Without the bestiary's size the EXP figures cannot be found, so nothing can be checked.
    [Fact]
    public void An_unknown_bestiary_size_refuses_the_reading()
    {
        Assert.Null(BeastRankRecord.Read(Filled(9, 6, 3), beastCount: 0));
    }

    // The highest values the game uses are in range, not past it.
    [Fact]
    public void The_highest_rank_and_exp_are_accepted()
    {
        var bytes = RecordSet(BeastRankRecord.FilledMarker, new byte[] { 100 }, 25);

        var reading = BeastRankRecord.Read(bytes, beastCount: 1);

        Assert.Equal(new Dictionary<int, int> { [1] = 25 }, reading!.Ranks);
    }

    // --- Which beasts the reading proves held ------------------------------------------------------

    // Only a beast with a pact can be raised, so rank 2 or above proves one.
    [Fact]
    public void A_beast_at_rank_two_or_above_is_leveled()
    {
        var reading = BeastRankRecord.Read(Filled(2, 25), beastCount: 2);

        Assert.Equal(new[] { 1, 2 }, reading!.Leveled.OrderBy(number => number));
    }

    // Rank 1 with any EXP has been raised too.
    [Fact]
    public void A_beast_at_rank_one_with_exp_is_leveled()
    {
        var bytes = RecordSet(BeastRankRecord.FilledMarker, new byte[] { 5 }, 1);

        Assert.Equal(new[] { 1 }, BeastRankRecord.Read(bytes, beastCount: 1)!.Leveled);
    }

    // Rank 1 with no EXP could be how the record set stores a beast without a pact, so it proves
    // nothing. Its rank is still read when its HP record is well-formed, for a beast the bestiary
    // shows as held.
    [Fact]
    public void A_beast_at_rank_one_with_no_exp_is_ranked_but_not_leveled()
    {
        var reading = BeastRankRecord.Read(Filled(1), beastCount: 1);

        Assert.Empty(reading!.Leveled);
        Assert.Equal(new Dictionary<int, int> { [1] = 1 }, reading.Ranks);
    }

    // EXP with no rank is not a combination a pact produces, so it proves nothing either.
    [Fact]
    public void Exp_with_no_rank_is_not_leveled()
    {
        var bytes = RecordSet(BeastRankRecord.FilledMarker, new byte[] { 40 }, 0);

        Assert.Empty(BeastRankRecord.Read(bytes, beastCount: 1)!.Leveled);
    }

    // --- Agreement with the bestiary window ----------------------------------------------------------

    [Fact]
    public void A_reading_that_ranks_every_held_beast_agrees()
    {
        var ranks = new Dictionary<int, int> { [1] = 9, [2] = 6, [3] = 3 };

        Assert.True(BeastRankRecord.AgreesWith(ranks, new[] { 1, 3 }));
    }

    // A held beast always has a rank, so a held beast the reading leaves out means the bytes are
    // something else.
    [Fact]
    public void A_reading_that_misses_a_held_beast_disagrees()
    {
        var ranks = new Dictionary<int, int> { [1] = 9 };

        Assert.False(BeastRankRecord.AgreesWith(ranks, new[] { 1, 3 }));
    }

    [Fact]
    public void With_nothing_held_yet_there_is_nothing_to_disagree_with()
    {
        Assert.True(BeastRankRecord.AgreesWith(new Dictionary<int, int>(), Array.Empty<int>()));
    }

    // --- The marker on its own ---------------------------------------------------------------------

    [Fact]
    public void Fewer_bytes_than_the_marker_are_not_filled()
    {
        Assert.False(BeastRankRecord.IsFilled(new byte[] { 2, 0 }));
    }

    // The distinction the refusal warning rests on: filled, yet not a reading.
    [Fact]
    public void A_filled_record_set_with_a_bad_value_is_filled_but_not_read()
    {
        var bytes = Filled(9, 26);

        Assert.True(BeastRankRecord.IsFilled(bytes));
        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 2));
    }

    // --- The decision on a look at the record set --------------------------------------------------

    [Fact]
    public void An_unfilled_record_set_is_assessed_as_not_filled()
    {
        var bytes = RecordSet(0, new byte[2], 9, 6);

        var assessment = BeastRankRecord.Assess(bytes, 2, Array.Empty<int>());

        Assert.Equal(RankRecordVerdict.NotFilled, assessment.Verdict);
        Assert.Null(assessment.Reading);
    }

    [Fact]
    public void A_filled_record_set_with_a_bad_value_is_refused()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 26), 2, Array.Empty<int>());

        Assert.Equal(RankRecordVerdict.Refused, assessment.Verdict);
        Assert.Null(assessment.Reading);
    }

    // A held beast the reading leaves unranked refuses it outright, so the window's check cannot be
    // skipped on the way to an accepted reading.
    [Fact]
    public void A_record_set_that_leaves_a_held_beast_unranked_is_refused()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 0), 2, new[] { 2 });

        Assert.Equal(RankRecordVerdict.Refused, assessment.Verdict);
    }

    [Fact]
    public void A_record_set_that_passes_every_check_is_accepted_with_its_reading()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 6), 2, new[] { 1 });

        Assert.Equal(RankRecordVerdict.Accepted, assessment.Verdict);
        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = 6 }, assessment.Reading!.Ranks);
        Assert.Equal(new[] { 1, 2 }, assessment.Reading.Leveled.OrderBy(number => number));
    }

    // A beast the window showed as held has a pact, so a record set with no well-formed HP record for
    // it is not the record set it claims to be.
    [Fact]
    public void A_held_beast_without_a_well_formed_hp_record_is_refused()
    {
        var bytes = Filled(9, 1);
        WriteHpRecord(bytes, 2, 2, 0, 0, 0, 0, 0);

        var assessment = BeastRankRecord.Assess(bytes, 2, new[] { 2 });

        Assert.Equal(RankRecordVerdict.Refused, assessment.Verdict);
    }

    // --- The HP records ----------------------------------------------------------------------------

    // The layout pinned in plain numbers, independent of the helpers above, which place their bytes
    // with the reader's own arithmetic: a bestiary of 50 has its HP records from byte 104, and the
    // whole record set spans 1104 bytes.
    [Fact]
    public void The_record_set_is_laid_out_as_the_game_holds_it()
    {
        Assert.Equal(104, BeastRankRecord.HpRecordsStart(50));
        Assert.Equal(1104, BeastRankRecord.ByteLength(50));

        // One beast, written out byte by byte: the marker, rank 9, EXP 0, then the HP record's words
        // 40 and 80, then 1510 (0x05E6) three times, each with its lowest byte first. `0x` writes a number
        // in hexadecimal, as in JavaScript, and the `u` suffix writes it as a `uint`.
        var bytes = new byte[]
        {
            2, 0, 0, 0,
            9,
            0,
            40, 0, 0, 0, 80, 0, 0, 0, 0xE6, 5, 0, 0, 0xE6, 5, 0, 0, 0xE6, 5, 0, 0,
        };

        Assert.Equal(new Dictionary<int, int> { [1] = 9 }, BeastRankRecord.Read(bytes, beastCount: 1)!.Ranks);
        Assert.Equal(1510u, BeastRankRecord.HpRecordWord(BeastRankRecord.HpRecord(bytes, 1, 0), 2));
    }

    // The smallest figures that still rise, then hold.
    [Fact]
    public void The_smallest_rising_record_is_well_formed()
    {
        var record = new byte[BeastRankRecord.HpRecordBytes];
        record[4] = 1;
        record[8] = 2;
        record[12] = 2;
        record[16] = 2;

        Assert.True(BeastRankRecord.IsWellFormedHpRecord(record));
    }

    // The last three figures hold one value only at rank 15 or below; past it the fourth and last
    // climb, and past rank 20 the last climbs alone. Each case is read from a raised beast at a rank
    // the shape fits, so a shape the check refused would refuse the whole reading.
    [Theory]
    [InlineData(9, 608u, 1011u, 2020u, 2020u, 2020u)] // every figure past the second matches
    [InlineData(16, 667u, 1172u, 2943u, 2997u, 2997u)] // the third stops while the last two climb
    [InlineData(21, 667u, 1172u, 2943u, 3210u, 3480u)] // each of the last three climbs past the one before
    [InlineData(25, 667u, 1172u, 2943u, 3210u, 65535u)] // the last figure at the two-byte ceiling
    public void A_raised_beast_whose_hp_figures_rise_and_never_fall_is_read(
        int rank, uint first, uint second, uint third, uint fourth, uint fifth)
    {
        // `(byte)` narrows the rank to the one byte the record set stores it in.
        var bytes = Filled(9, (byte)rank);
        WriteHpRecord(bytes, 2, 2, first, second, third, fourth, fifth);

        var reading = BeastRankRecord.Read(bytes, beastCount: 2);

        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = rank }, reading!.Ranks);
        Assert.Contains(2, reading.Leveled);
    }

    // Each case but the last breaks the shape one way.
    [Theory]
    [InlineData(549u, 760u, 1521u, 1520u, 1520u)] // the third figure is above the fourth
    [InlineData(549u, 760u, 1520u, 1521u, 1520u)] // the fourth figure is above the last
    [InlineData(760u, 760u, 1520u, 1520u, 1520u)] // the first figure is not below the second
    [InlineData(5u, 20u, 20u, 20u, 20u)] // the second figure is not below the third
    [InlineData(549u, 777u, 1520u, 1520u, 1520u)] // the second is more than half the third, past the margin
    [InlineData(549u, 760u, 1520u, 1520u, 65536u)] // the last figure does not fit in two bytes
    [InlineData(0u, 0u, 0u, 0u, 0u)] // no record at all
    public void A_raised_beast_without_a_well_formed_hp_record_refuses_the_whole_reading(
        uint first, uint second, uint third, uint fourth, uint fifth)
    {
        var bytes = Filled(9, 6);
        WriteHpRecord(bytes, 2, 2, first, second, third, fourth, fifth);

        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 2));
    }

    // Rank 1 with some EXP is raised too, so it is held to the shape as firmly as a higher rank.
    [Fact]
    public void A_beast_at_rank_one_with_exp_and_no_hp_record_refuses_the_whole_reading()
    {
        var bytes = RecordSet(BeastRankRecord.FilledMarker, new byte[] { 0, 5 }, 9, 1);
        WriteHpRecord(bytes, 2, 2, 0, 0, 0, 0, 0);

        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 2));
    }

    // A beast neither raised nor shown as held may have no pact, so its HP record is not held to the
    // shape: the beast is only left unranked, and the rest of the reading stands.
    [Fact]
    public void A_fresh_beast_without_a_well_formed_hp_record_is_left_unranked()
    {
        var bytes = Filled(9, 1);
        WriteHpRecord(bytes, 2, 2, 0, 0, 0, 0, 0);

        var reading = BeastRankRecord.Read(bytes, beastCount: 2);

        Assert.Equal(new Dictionary<int, int> { [1] = 9 }, reading!.Ranks);
        Assert.Equal(new[] { 1 }, reading.Leveled);
    }

    // With every beast at the same rank and EXP, nothing in those figures can tell a record set read
    // from the wrong place apart from one read from the right place. The HP records still can at
    // each distance below, none of them a whole number of HP records: whole words short of one
    // record, and one to three bytes either side of one.
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void A_record_set_read_a_few_bytes_early_is_refused_even_when_every_beast_reads_the_same(int shift)
    {
        // A `const` local is a value fixed when the code compiles, which nothing can reassign.
        const int beastCount = 50;

        // `Enumerable.Repeat(value, count)` yields the value that many times, like
        // `Array(count).fill(value)` in JavaScript.
        var ranks = Enumerable.Repeat((byte)BeastRankRecord.MaxRank, beastCount).ToArray();
        var inPlace = RecordSet(BeastRankRecord.FilledMarker, new byte[beastCount], ranks);

        // Every other beast's last figures climb past the third, as a beast's do above rank 15, so the
        // misread covers records whose last three match and records whose last two climb.
        for (var number = 2; number <= beastCount; number += 2)
        {
            var step = (uint)(10 * number);
            WriteHpRecord(inPlace, beastCount, number, 667, 1172, 2943 + step, 3210 + step, 3480 + step);
        }

        // The same bytes `shift` places later, behind bytes whose first four happen to read as the
        // marker: the marker, every rank and every EXP figure still pass their checks. The copy views
        // all but the last `shift` bytes of the record set and writes them from `shift` on.
        var early = new byte[inPlace.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(early, BeastRankRecord.FilledMarker);
        inPlace.AsSpan(0, inPlace.Length - shift).CopyTo(early.AsSpan(shift));

        Assert.Equal(beastCount, BeastRankRecord.Read(inPlace, beastCount)!.Leveled.Count);
        Assert.Null(BeastRankRecord.Read(early, beastCount));
    }

    // Fresh beasts carry no EXP, so a read one word early puts a zero in front of the first HP record
    // and slides the rest of it along by one word. The real marker byte then reads as a rank of 2,
    // which holds that misread record to the shape, and it fails: its second figure is the real
    // first, which is over half the real second.
    [Fact]
    public void A_record_set_of_fresh_beasts_read_one_word_early_is_refused()
    {
        const int beastCount = 50;
        var ranks = Enumerable.Repeat((byte)1, beastCount).ToArray();
        var inPlace = RecordSet(BeastRankRecord.FilledMarker, new byte[beastCount], ranks);

        var early = new byte[inPlace.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(early, BeastRankRecord.FilledMarker);
        inPlace.AsSpan(0, inPlace.Length - 4).CopyTo(early.AsSpan(4));

        Assert.Equal(beastCount, BeastRankRecord.Read(inPlace, beastCount)!.Ranks.Count);
        Assert.Null(BeastRankRecord.Read(early, beastCount));
    }

    // The HP records begin after one rank byte and one EXP byte per beast, so a bestiary larger than
    // the record set looks for them in the wrong place.
    [Fact]
    public void A_record_set_laid_out_for_a_smaller_bestiary_is_refused()
    {
        var bytes = new byte[BeastRankRecord.ByteLength(4)];
        Filled(9, 6, 3).CopyTo(bytes, 0);

        Assert.Null(BeastRankRecord.Read(bytes, beastCount: 4));
    }
}
