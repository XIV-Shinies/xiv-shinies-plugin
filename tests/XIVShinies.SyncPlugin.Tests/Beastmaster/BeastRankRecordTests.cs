using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// The beast record set sits where no published definition names it, so these pin both halves of the
// reader: a well-formed record set yields each ranked beast's rank, and anything else yields nothing.
public class BeastRankRecordTests
{
    /// <summary>A record set's bytes: the marker, then the ranks, then the EXP figures.</summary>
    // `params byte[] ranks` lets a caller list the ranks one by one, like a rest parameter
    // (`...ranks`) in JavaScript.
    private static byte[] RecordSet(uint marker, byte[] exp, params byte[] ranks)
    {
        var bytes = new byte[BeastRankRecord.ByteLength(ranks.Length)];

        // Written little-endian, the byte order the reader expects, whatever the machine's own.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, marker);
        ranks.CopyTo(bytes, BeastRankRecord.MarkerBytes);
        exp.CopyTo(bytes, BeastRankRecord.MarkerBytes + ranks.Length);
        return bytes;
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
    // nothing. Its rank is still read, for a beast the bestiary shows as held.
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

        var assessment = BeastRankRecord.Assess(bytes, 2, Array.Empty<int>(), layoutVerified: true);

        Assert.Equal(RankRecordVerdict.NotFilled, assessment.Verdict);
        Assert.Null(assessment.Reading);
    }

    [Fact]
    public void A_filled_record_set_with_a_bad_value_is_refused()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 26), 2, Array.Empty<int>(), layoutVerified: true);

        Assert.Equal(RankRecordVerdict.Refused, assessment.Verdict);
        Assert.Null(assessment.Reading);
    }

    // A held beast the reading leaves unranked refuses it outright, so the window's check cannot be
    // skipped on the way to an accepted reading.
    [Fact]
    public void A_record_set_that_leaves_a_held_beast_unranked_is_refused()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 0), 2, new[] { 2 }, layoutVerified: true);

        Assert.Equal(RankRecordVerdict.Refused, assessment.Verdict);
    }

    [Fact]
    public void A_record_set_that_passes_every_check_is_accepted_with_its_reading()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 6), 2, new[] { 1 }, layoutVerified: true);

        Assert.Equal(RankRecordVerdict.Accepted, assessment.Verdict);
        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = 6 }, assessment.Reading!.Ranks);
        Assert.Equal(new[] { 1, 2 }, assessment.Reading.Leveled.OrderBy(number => number));
    }

    // On a game version the layout was not checked on, a shifted record set could pass every check,
    // so the reading proves no pact: its ranks still travel beside the beasts the window showed held.
    [Fact]
    public void On_an_unchecked_layout_an_accepted_reading_proves_no_pact()
    {
        var assessment = BeastRankRecord.Assess(Filled(9, 6), 2, new[] { 1 }, layoutVerified: false);

        Assert.Equal(RankRecordVerdict.Accepted, assessment.Verdict);
        Assert.Equal(new Dictionary<int, int> { [1] = 9, [2] = 6 }, assessment.Reading!.Ranks);
        Assert.Empty(assessment.Reading.Leveled);
    }

    // --- Which layouts are trusted -----------------------------------------------------------------

    [Fact]
    public void A_checked_version_with_the_checked_bestiary_size_is_trusted()
    {
        var version = BeastRankRecord.VerifiedGameVersions.First();

        Assert.True(BeastRankRecord.IsLayoutVerified(version, BeastRankRecord.VerifiedBeastCount));
    }

    // A grown bestiary moves where the EXP figures start, whatever the game version.
    [Fact]
    public void A_bestiary_of_a_different_size_is_not_trusted()
    {
        var version = BeastRankRecord.VerifiedGameVersions.First();

        Assert.False(BeastRankRecord.IsLayoutVerified(version, BeastRankRecord.VerifiedBeastCount + 1));
    }

    [Theory]
    [InlineData("2099.01.01.0000.0000")]
    [InlineData(null)]
    public void An_unchecked_or_unknown_game_version_is_not_trusted(string? version)
    {
        Assert.False(BeastRankRecord.IsLayoutVerified(version, BeastRankRecord.VerifiedBeastCount));
    }
}
