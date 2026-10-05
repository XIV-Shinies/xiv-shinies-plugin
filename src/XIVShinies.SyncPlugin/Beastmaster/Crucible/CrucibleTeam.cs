using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>Which of its six modes the team window is in.</summary>
/// <remarks>The values are the window's own numbers for each mode.</remarks>
public enum CrucibleTeamMode
{
    /// <summary>Choosing the familiars to bring, at the entrance before the run.</summary>
    RosterPick = 0,

    /// <summary>Looking over the team, from Team Composition or beside Board Layout.</summary>
    Browse = 1,

    /// <summary>Choosing which familiars fight the next battle.</summary>
    Lineup = 2,

    /// <summary>Choosing which familiar eats a feed.</summary>
    Feed = 3,

    /// <summary>Choosing which familiars rest at a campsite.</summary>
    Campsite = 4,

    /// <summary>Choosing which downed familiar a Blessed Horn revives.</summary>
    BlessedHorn = 5,
}

/// <summary>One of the player's familiars as the team window showed it.</summary>
/// <param name="PetId">The familiar's <c>XBMPet</c> row id (its bestiary number).</param>
/// <param name="Place">Its lineup place, 0 to 2, or 3 when it is not in the lineup.</param>
/// <param name="CurrentHp">Its current HP; 0 means it is down.</param>
/// <param name="MaxHp">Its maximum HP, feeds included.</param>
/// <param name="Rank">Its rank as the window drew it, which is the board's cap when it is capped.</param>
/// <param name="RankSynced">Whether the board has capped its rank.</param>
/// <param name="FeedItemIds">The <c>XBMItem</c> ids of the feeds it has eaten, in order.</param>
/// <param name="Resting">Whether it is picked to rest, in the campsite mode.</param>
public sealed record CrucibleFamiliar(
    uint PetId,
    int Place,
    int CurrentHp,
    int MaxHp,
    int Rank,
    bool RankSynced,
    IReadOnlyList<uint> FeedItemIds,
    bool Resting);

/// <summary>What the team window showed.</summary>
/// <param name="Mode">Which mode the window was in.</param>
/// <param name="ItemId">
/// The item the window is choosing a familiar for: the feed in the feed mode, the horn in the
/// Blessed Horn mode; null in the other modes.
/// </param>
/// <param name="Familiars">The familiars listed, in the window's order.</param>
// `uint?` is an id that may be absent: `number | null`.
public sealed record CrucibleTeamReading(
    CrucibleTeamMode Mode,
    uint? ItemId,
    IReadOnlyList<CrucibleFamiliar> Familiars);

/// <summary>Reads the team window (<c>XBMPetParty</c>) from the values the window was given.</summary>
/// <remarks>
/// <para>
/// The window carries its mode and a count, then fifteen familiar places of 77 values each (an
/// empty place has no name), and the item it is choosing for. In the roster pick the count is how
/// many have been picked, the picks fill the first places in pick order, and the places past the
/// count keep stale copies of earlier picks, so only the counted places are read. In every other
/// mode the count is how many familiars were brought, and every filled place is one of them.
/// </para>
/// <para>
/// Each record checks itself: its icon is 242000 plus its id, and its HP drawn as text matches its
/// HP numbers. No familiar may appear twice. Any failure refuses the whole reading, because a
/// misaligned record would put one familiar's HP on another.
/// </para>
/// </remarks>
public static class CrucibleTeam
{
    /// <summary>Where the window keeps its mode.</summary>
    private const int ModeIndex = 2;

    /// <summary>Where the window keeps its count.</summary>
    private const int CountIndex = 5;

    /// <summary>Where the first familiar record starts.</summary>
    private const int FirstRecord = 6;

    /// <summary>How many values each familiar record spans.</summary>
    private const int RecordStride = 77;

    /// <summary>How many familiar places the window has.</summary>
    private const int PlaceCount = 15;

    /// <summary>Where the item being chosen for sits, in the feed and Blessed Horn modes.</summary>
    private const int ItemIdIndex = 1187;

    /// <summary>A familiar's icon is this number plus its <c>XBMPet</c> id.</summary>
    private const uint IconBase = 242000;

    /// <summary>The most feeds one familiar's record can hold.</summary>
    private const int MaxFeeds = 5;

    /// <summary>The highest lineup place: 0 to 2 are the three picks, 3 is none.</summary>
    private const int NoPlace = 3;

    // Offsets within one familiar record.
    private const int RankOffset = 0;
    private const int IconOffset = 1;
    private const int NameOffset = 3;
    private const int HpTextOffset = 4;
    private const int CurrentHpOffset = 5;
    private const int MaxHpOffset = 6;

    /// <summary>The first feed pair: an icon, then the feed's item id, five pairs in a row.</summary>
    private const int FirstFeedOffset = 7;

    private const int FeedCountOffset = 72;
    private const int PlaceOffset = 74;
    private const int RestingOffset = 75;
    private const int PetIdOffset = 76;

    /// <summary>The highest position the records read: the last place's id.</summary>
    private const int LastRecordIndex = FirstRecord + ((PlaceCount - 1) * RecordStride) + PetIdOffset;

    /// <summary>
    /// Reads the team window, or null when the values are not the layout this reader knows.
    /// </summary>
    /// <param name="values">Every value the window was handed, in order.</param>
    public static CrucibleTeamReading? Read(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= LastRecordIndex)
            return null;

        // `is not { } modeNumber` is true when the place holds no number, and the `if` returns; past
        // it, `modeNumber` is the number. `is > X` is a relational pattern: true for any number
        // above X, here any mode past the last. An enum is a named number underneath, and `(uint)`
        // reads the number behind the last mode.
        if (values[ModeIndex].Number is not { } modeNumber
            || modeNumber is > (uint)CrucibleTeamMode.BlessedHorn)
        {
            return null;
        }

        if (values[CountIndex].Number is not { } count || count > PlaceCount)
            return null;

        // The cast the other way: the window's number, now known to be in range, as its mode.
        var mode = (CrucibleTeamMode)modeNumber;
        var familiars = new List<CrucibleFamiliar>(PlaceCount);

        // A HashSet answers "seen this one already?" in one step, like a JavaScript Set.
        var seen = new HashSet<uint>();

        // The roster pick reads its counted places only; the other modes read every place. `(int)`
        // turns the window's unsigned count, at most fifteen here, into the `int` a loop counts in.
        var places = mode == CrucibleTeamMode.RosterPick ? (int)count : PlaceCount;
        for (var place = 0; place < places; place++)
        {
            var record = FirstRecord + (place * RecordStride);

            // A place with no name holds no familiar.
            if (string.IsNullOrEmpty(values[record + NameOffset].Text))
            {
                // Every counted roster place holds a pick.
                if (mode == CrucibleTeamMode.RosterPick)
                    return null;

                continue;
            }

            // `seen.Add` returns false when the id is already in the set (unlike JavaScript's
            // `Set.add`, which returns the set), so `!seen.Add(...)` catches a repeat.
            if (ReadFamiliar(values, record) is not { } familiar || !seen.Add(familiar.PetId))
                return null;

            familiars.Add(familiar);
        }

        // Outside the roster pick, the familiars read must number exactly the count.
        if (mode != CrucibleTeamMode.RosterPick && familiars.Count != count)
            return null;

        uint? itemId = null;

        // `mode is A or B` is true when the mode is either one.
        if (mode is CrucibleTeamMode.Feed or CrucibleTeamMode.BlessedHorn)
        {
            if (values.Count <= ItemIdIndex || values[ItemIdIndex].Number is not { } item || item == 0)
                return null;

            itemId = item;
        }

        return new CrucibleTeamReading(mode, itemId, familiars);
    }

    /// <summary>Reads one familiar record, or null when it fails its own checks.</summary>
    /// <param name="values">Every value the window was handed.</param>
    /// <param name="record">Where the record starts.</param>
    private static CrucibleFamiliar? ReadFamiliar(IReadOnlyList<AddonValue> values, int record)
    {
        if (values[record + PetIdOffset].Number is not { } petId || petId == 0)
            return null;

        // The icon is derived from the id, so the two agreeing proves the record is aligned.
        if (values[record + IconOffset].Number != IconBase + petId)
            return null;

        if (values[record + CurrentHpOffset].Number is not { } current
            || values[record + MaxHpOffset].Number is not { } max
            || current > max)
        {
            return null;
        }

        // The HP the window drew as text must be the HP the record carries as numbers. Tuples
        // compare by their values, element by element, and a null (unreadable) fraction equals no
        // pair, so text that does not read refuses the record too.
        if (CrucibleText.ReadFraction(values[record + HpTextOffset].Text) != ((int)current, (int)max))
            return null;

        // `var (rank, synced)` unpacks the tuple into two variables, like `const [rank, synced]`
        // in TypeScript, and does not match a null, so an unreadable label refuses the record.
        if (CrucibleText.ReadRank(values[record + RankOffset].Text) is not var (rank, synced))
            return null;

        if (values[record + PlaceOffset].Number is not { } place || place > NoPlace)
            return null;

        if (values[record + RestingOffset].Flag is not { } resting)
            return null;

        if (values[record + FeedCountOffset].Number is not { } feedCount || feedCount > MaxFeeds)
            return null;

        var feeds = new List<uint>((int)feedCount);
        for (var feed = 0; feed < feedCount; feed++)
        {
            // Each pair is an icon, then the id: the id sits one past the pair's start.
            var idIndex = record + FirstFeedOffset + (feed * 2) + 1;
            if (values[idIndex].Number is not { } feedId || feedId == 0)
                return null;

            feeds.Add(feedId);
        }

        return new CrucibleFamiliar(
            petId, (int)place, (int)current, (int)max, rank, synced, feeds, resting);
    }
}
