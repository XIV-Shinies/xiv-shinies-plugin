using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The team window (XBMPetParty): the player's own familiars, in one of six modes.
public class CrucibleTeamTests
{
    // Positions in the window's values. ItemId holds the item the window is choosing for.
    private const int Mode = 2;
    private const int Count = 5;
    private const int ItemId = 1187;
    private const int FirstRecord = 6;
    private const int RecordStride = 77;

    // Offsets within one familiar record.
    private const int Rank = 0;
    private const int Icon = 1;
    private const int HpText = 4;
    private const int CurrentHp = 5;
    private const int MaxHp = 6;
    private const int FeedCount = 72;
    private const int Place = 74;
    private const int Resting = 75;
    private const int PetId = 76;

    /// <summary>The second value of a record's first feed pair: the feed's item id.</summary>
    private const int FirstFeedId = 8;

    // The places past the count keep stale copies of earlier picks; this fixture has a twelfth.
    [Fact]
    public void The_roster_pick_reads_only_the_counted_picks_in_order()
    {
        var reading = CrucibleTeam.Read(WindowFixture.Load("petparty-mode0-roster-stale.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleTeamMode.RosterPick, reading.Mode);
        Assert.Equal(
            new uint[] { 8, 13, 4, 7, 11, 2, 10, 5, 20, 9, 35 },
            reading.Familiars.Select(familiar => familiar.PetId));
    }

    // Every counted roster place holds a pick. This fixture's thirteenth place is empty.
    [Fact]
    public void A_roster_count_that_reaches_an_empty_place_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode0-roster-stale.json");
        values[Count] = AddonValue.FromInteger(13);

        Assert.Null(CrucibleTeam.Read(values));
    }

    // A downed familiar is listed like any other.
    [Fact]
    public void A_downed_familiar_reads_zero_hp()
    {
        var reading = CrucibleTeam.Read(WindowFixture.Load("petparty-mode1-browse-downed.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleTeamMode.Browse, reading.Mode);
        Assert.Equal(4, reading.Familiars.Count);

        var downed = reading.Familiars.Single(familiar => familiar.PetId == 16);
        Assert.Equal(0, downed.CurrentHp);
        Assert.Equal(550, downed.MaxHp);
    }

    // A place is 0 to 2 for the three picks, and 3 for none.
    [Fact]
    public void A_lineup_reads_each_familiars_place()
    {
        var reading = CrucibleTeam.Read(WindowFixture.Load("petparty-mode2-lineup-picked.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleTeamMode.Lineup, reading.Mode);
        Assert.Equal(12, reading.Familiars.Count);

        var picked = reading.Familiars
            .Where(familiar => familiar.Place < 3)
            .OrderBy(familiar => familiar.Place)
            .Select(familiar => familiar.PetId);
        Assert.Equal(new uint[] { 16, 20, 10 }, picked);
    }

    [Fact]
    public void The_feed_window_reads_the_feed_and_what_each_familiar_has_eaten()
    {
        var reading = CrucibleTeam.Read(WindowFixture.Load("petparty-mode3-feed.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleTeamMode.Feed, reading.Mode);
        Assert.Equal(148u, reading.ItemId);

        var fed = reading.Familiars.Single(familiar => familiar.PetId == 10);
        Assert.Equal(new uint[] { 146 }, fed.FeedItemIds);
        Assert.All(
            reading.Familiars.Where(familiar => familiar.PetId != 10),
            familiar => Assert.Empty(familiar.FeedItemIds));
    }

    [Fact]
    public void A_campsite_reads_which_familiars_rest()
    {
        var reading = CrucibleTeam.Read(WindowFixture.Load("petparty-mode4-campsite-resting.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleTeamMode.Campsite, reading.Mode);
        Assert.Null(reading.ItemId);
        Assert.Equal(
            new uint[] { 20 },
            reading.Familiars.Where(familiar => familiar.Resting).Select(familiar => familiar.PetId));
    }

    [Fact]
    public void The_blessed_horn_mode_reads_the_horn_and_the_downed_familiars()
    {
        var reading = CrucibleTeam.Read(WindowFixture.Load("petparty-mode5-blessed-horn.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleTeamMode.BlessedHorn, reading.Mode);
        Assert.Equal(96u, reading.ItemId);
        Assert.Equal(
            new uint[] { 20, 35 },
            reading.Familiars.Where(familiar => familiar.CurrentHp == 0).Select(familiar => familiar.PetId));
    }

    // Only the feed and Blessed Horn modes choose for an item. The value can hold a stale id in the
    // others, and the server refuses an item outside these two. These fixtures leave the value
    // unreadable, so each row plants a stale id there.
    [Theory]
    [InlineData("petparty-mode0-roster-stale.json")]
    [InlineData("petparty-mode1-browse-downed.json")]
    [InlineData("petparty-mode2-lineup-picked.json")]
    [InlineData("petparty-mode4-campsite-resting.json")]
    public void Outside_the_feed_and_blessed_horn_modes_a_stale_item_reads_as_none(string fixture)
    {
        var values = WindowFixture.Load(fixture);
        values[ItemId] = AddonValue.FromInteger(148);

        var reading = CrucibleTeam.Read(values);

        Assert.NotNull(reading);
        Assert.Null(reading.ItemId);
    }

    // The feed and Blessed Horn modes choose for an item, so an id of 0, or values that stop short of
    // it, mean a layout this reader does not know.
    [Fact]
    public void A_feed_window_without_its_item_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode3-feed.json");
        values[ItemId] = AddonValue.FromInteger(0);

        Assert.Null(CrucibleTeam.Read(values));
        Assert.Null(CrucibleTeam.Read(WindowFixture.Load("petparty-mode3-feed.json").GetRange(0, 1170)));
    }

    // The rank comes from the label behind the paw glyph (U+E036). No rank label in the fixtures
    // carries the sync glyph (U+E0BC), so this test writes one before the paw.
    [Fact]
    public void A_rank_reads_from_its_label_and_a_capped_rank_is_marked()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        var uncapped = CrucibleTeam.Read(values)!.Familiars[0];
        Assert.Equal(9, uncapped.Rank);
        Assert.False(uncapped.RankSynced);

        values[FirstRecord + Rank] = AddonValue.FromText("\uE0BC\uE0365");
        var capped = CrucibleTeam.Read(values)!.Familiars[0];

        Assert.Equal(5, capped.Rank);
        Assert.True(capped.RankSynced);
    }

    // Each record checks itself: its icon is 242000 plus its id, and its HP text matches its HP
    // numbers. Failing either means the record is misaligned.
    [Fact]
    public void An_icon_that_does_not_match_the_id_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + Icon] = AddonValue.FromInteger(242009);

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void Hp_text_that_disagrees_with_the_hp_numbers_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + HpText] = AddonValue.FromText("1000/1118");

        Assert.Null(CrucibleTeam.Read(values));
    }

    // One missing value means the record has moved.
    [Theory]
    [InlineData(Rank)]
    [InlineData(Icon)]
    [InlineData(HpText)]
    [InlineData(CurrentHp)]
    [InlineData(MaxHp)]
    [InlineData(FeedCount)]
    [InlineData(Place)]
    [InlineData(Resting)]
    [InlineData(PetId)]
    public void A_record_missing_a_value_refuses_the_reading(int offset)
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + offset] = AddonValue.Unreadable;

        Assert.Null(CrucibleTeam.Read(values));
    }

    // Values of the right kind can still be ones no familiar has: an id of 0, HP above its maximum,
    // a rank label with no digits.
    [Fact]
    public void A_pet_id_of_zero_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + PetId] = AddonValue.FromInteger(0);

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void Current_hp_above_the_maximum_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + CurrentHp] = AddonValue.FromInteger(2000);

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void A_rank_label_without_digits_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + Rank] = AddonValue.FromText("\uE036");

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void A_resting_value_that_is_not_a_flag_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + Resting] = AddonValue.FromInteger(1);

        Assert.Null(CrucibleTeam.Read(values));
    }

    // One familiar cannot be listed twice outside the roster pick's stale places.
    [Fact]
    public void A_repeated_familiar_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        var second = FirstRecord + RecordStride;
        values[second + Icon] = AddonValue.FromInteger(242008);
        values[second + PetId] = AddonValue.FromInteger(8);

        Assert.Null(CrucibleTeam.Read(values));
    }

    // Outside the roster pick, the count is the number of familiars brought, which must match the
    // places filled.
    [Fact]
    public void A_count_that_disagrees_with_the_filled_places_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[Count] = AddonValue.FromInteger(11);

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void A_count_beyond_fifteen_or_missing_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode0-roster-stale.json");
        values[Count] = AddonValue.FromInteger(16);
        Assert.Null(CrucibleTeam.Read(values));

        values[Count] = AddonValue.Unreadable;
        Assert.Null(CrucibleTeam.Read(values));
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(99u)]
    public void A_mode_this_reader_does_not_know_refuses_the_reading(uint mode)
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[Mode] = AddonValue.FromInteger(mode);

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void A_place_beyond_three_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json");
        values[FirstRecord + Place] = AddonValue.FromInteger(4);

        Assert.Null(CrucibleTeam.Read(values));
    }

    // A record can carry at most five feeds.
    [Fact]
    public void More_feeds_than_a_record_holds_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode3-feed.json");
        values[FirstRecord + FeedCount] = AddonValue.FromInteger(6);

        Assert.Null(CrucibleTeam.Read(values));
    }

    // A counted feed always names its item. Pet 10, in the sixth place, has eaten one.
    [Fact]
    public void A_counted_feed_with_item_id_zero_refuses_the_reading()
    {
        var values = WindowFixture.Load("petparty-mode3-feed.json");
        values[FirstRecord + (5 * RecordStride) + FirstFeedId] = AddonValue.FromInteger(0);

        Assert.Null(CrucibleTeam.Read(values));
    }

    [Fact]
    public void A_window_without_its_records_reads_as_nothing()
    {
        var values = WindowFixture.Load("petparty-mode2-lineup-picked.json").GetRange(0, 8);

        Assert.Null(CrucibleTeam.Read(values));
    }
}
