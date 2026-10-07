using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// RetainerRoster turns the retainers a pass read into the upload's `retainers` list: a key per
// retainer, its hashed id, its name when the game has one, and which retainer's market listings were
// read. Reading the ids, names and market owner out of the game is verified by in-game QA.
public class RetainerRosterTests
{
    private const ulong Mogwin = 33_000_000_001;
    private const ulong Kupo = 33_000_000_002;
    private const ulong Stilt = 33_000_000_003;

    private static readonly IReadOnlyDictionary<ulong, string> NoNames = new Dictionary<ulong, string>();

    [Fact]
    public void Each_read_retainer_gets_a_key_and_its_hashed_id()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin, Kupo }, NoNames, marketRetainerId: null);

        Assert.Equal(2, roster.Entries.Count);
        Assert.Equal(new[] { 1, 2 }, roster.Entries.Select(entry => entry.Key));

        // Both sides are sorted before comparing: this test is about which digests are listed, and
        // which one gets key 1 is pinned separately below.
        Assert.Equal(
            new[] { RetainerIdHash.Compute(Mogwin), RetainerIdHash.Compute(Kupo) }.Order(),
            roster.Entries.Select(entry => entry.Id).Order());
    }

    // Keys follow the digests' order, not the raw ids', so the keys say nothing about how the raw
    // ids compare. Ids 1 and 3 are chosen because the two orders disagree: 3's digest (35be…) sorts
    // before 1's (7c9f…).
    [Fact]
    public void Keys_follow_the_digest_order_rather_than_the_raw_id_order()
    {
        var roster = RetainerRoster.Build(new ulong[] { 1, 3 }, NoNames, null);

        Assert.Equal(1, roster.KeyOf(3));
        Assert.Equal(2, roster.KeyOf(1));
    }

    // Only a retainer whose saved copy was read is listed, whoever else the game names
    // (RetainerRoster's class remarks say why).
    [Fact]
    public void A_retainer_known_only_by_name_is_not_listed()
    {
        var names = new Dictionary<ulong, string>
        {
            [Mogwin] = "Mogwin",
            [Kupo] = "Kupo",
            [Stilt] = "Stilt",
        };

        var roster = RetainerRoster.Build(new[] { Mogwin }, names, null);

        Assert.Equal(RetainerIdHash.Compute(Mogwin), Assert.Single(roster.Entries).Id);
        Assert.Null(roster.KeyOf(Kupo));
        Assert.Null(roster.KeyOf(Stilt));
    }

    // KeyOf is how a slot read from a retainer finds the key its held entry carries.
    [Fact]
    public void KeyOf_finds_the_key_given_to_each_retainer()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin, Kupo }, NoNames, marketRetainerId: null);

        foreach (var entry in roster.Entries)
        {
            var id = entry.Id == RetainerIdHash.Compute(Mogwin) ? Mogwin : Kupo;
            Assert.Equal(entry.Key, roster.KeyOf(id));
        }

        Assert.Null(roster.KeyOf(Stilt));
    }

    // Keys do not depend on the order the game hands the retainers over (RetainerRoster's class
    // remarks say why).
    [Fact]
    public void Keys_do_not_depend_on_the_order_the_retainers_were_read()
    {
        var forward = RetainerRoster.Build(new[] { Mogwin, Kupo, Stilt }, NoNames, null);
        var backward = RetainerRoster.Build(new[] { Stilt, Kupo, Mogwin }, NoNames, null);

        Assert.Equal(forward.KeyOf(Mogwin), backward.KeyOf(Mogwin));
        Assert.Equal(forward.KeyOf(Kupo), backward.KeyOf(Kupo));
        Assert.Equal(forward.KeyOf(Stilt), backward.KeyOf(Stilt));
    }

    // An unused entry (id 0) is no retainer, and the same retainer read twice is listed once.
    [Fact]
    public void Zero_and_repeated_ids_are_listed_once_or_not_at_all()
    {
        var roster = RetainerRoster.Build(new ulong[] { 0, Mogwin, Mogwin }, NoNames, null);

        Assert.Equal(RetainerIdHash.Compute(Mogwin), Assert.Single(roster.Entries).Id);
    }

    [Fact]
    public void A_name_the_game_has_travels_with_its_retainer()
    {
        var names = new Dictionary<ulong, string> { [Mogwin] = "Mogwin" };

        var roster = RetainerRoster.Build(new[] { Mogwin, Kupo }, names, null);

        Assert.Equal("Mogwin", roster.Entries.Single(entry => entry.Key == roster.KeyOf(Mogwin)).Name);
        Assert.Null(roster.Entries.Single(entry => entry.Key == roster.KeyOf(Kupo)).Name);
    }

    // A blank name says nothing and an overlong one is a misread (RetainerRoster.UsableName says
    // why), so neither is sent. The 33-character row is one past the limit, so raising the limit by
    // even one fails here.
    //
    // A [Theory] runs its body once per [InlineData] row, like `it.each([...])` in Jest.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("A name far longer than any retainer name the game allows")]
    public void A_blank_or_overlong_name_is_left_off(string name)
    {
        var names = new Dictionary<ulong, string> { [Mogwin] = name };

        var entry = Assert.Single(RetainerRoster.Build(new[] { Mogwin }, names, null).Entries);

        Assert.Null(entry.Name);
    }

    [Fact]
    public void A_name_of_exactly_32_characters_is_kept()
    {
        var name = new string('a', GlamourCaps.RetainerName);
        var names = new Dictionary<ulong, string> { [Mogwin] = name };

        Assert.Equal(name, Assert.Single(RetainerRoster.Build(new[] { Mogwin }, names, null).Entries).Name);
    }

    // The retainer whose listings were read carries `market: true`, and its key is where the listed
    // copies go. Every other retainer omits the flag.
    [Fact]
    public void The_market_retainer_is_flagged_and_its_key_is_the_market_key()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin, Kupo }, NoNames, marketRetainerId: Kupo);

        Assert.Equal(roster.KeyOf(Kupo), roster.MarketKey);
        Assert.True(roster.Entries.Single(entry => entry.Key == roster.KeyOf(Kupo)).Market);
        Assert.Null(roster.Entries.Single(entry => entry.Key == roster.KeyOf(Mogwin)).Market);
    }

    // A market owner whose own copy was not read is not listed, so its listings go unreported
    // (RetainerRoster's class remarks say why).
    [Fact]
    public void A_market_owner_whose_copy_was_not_read_gets_no_market_key()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin }, NoNames, marketRetainerId: Kupo);

        Assert.Null(roster.MarketKey);
        Assert.Null(Assert.Single(roster.Entries).Market);
        Assert.Null(roster.KeyOf(Kupo));
    }

    [Fact]
    public void With_no_listings_read_no_retainer_is_flagged()
    {
        var roster = RetainerRoster.Build(new[] { Mogwin }, NoNames, marketRetainerId: null);

        Assert.Null(roster.MarketKey);
        Assert.Null(Assert.Single(roster.Entries).Market);
    }

    [Fact]
    public void No_retainers_read_is_an_empty_roster()
    {
        var roster = RetainerRoster.Build(Array.Empty<ulong>(), NoNames, marketRetainerId: Mogwin);

        Assert.Empty(roster.Entries);
        Assert.Null(roster.MarketKey);
    }

    // --- Whose listings were read ----------------------------------------------------------------
    // The owner is trusted only when the listings read match its own count (RetainerRoster.MarketOwner's
    // remarks say why).

    // Listing counts by retainer, as the game's retainer list reports them.
    private static readonly IReadOnlyDictionary<ulong, int> ListingCounts = new Dictionary<ulong, int>
    {
        [Mogwin] = 3,
        [Kupo] = 0,
    };

    [Fact]
    public void The_last_selected_retainer_owns_the_listings_when_the_counts_agree()
    {
        Assert.Equal(Mogwin, RetainerRoster.MarketOwner(true, 3, Mogwin, ListingCounts));
    }

    // A retainer that lists nothing, with an empty container read, genuinely listed nothing: that is
    // still an owner, so the server can clear the listings it no longer has.
    [Fact]
    public void A_retainer_with_no_listings_and_an_empty_read_owns_the_empty_listings()
    {
        Assert.Equal(Kupo, RetainerRoster.MarketOwner(true, 0, Kupo, ListingCounts));
    }

    // The container is not loaded, so no listings were read at all.
    [Fact]
    public void Nothing_owns_listings_that_were_not_read()
    {
        Assert.Null(RetainerRoster.MarketOwner(false, 0, Kupo, ListingCounts));
    }

    // No retainer selected yet this session.
    [Fact]
    public void Nothing_owns_the_listings_when_no_retainer_was_selected()
    {
        Assert.Null(RetainerRoster.MarketOwner(true, 0, 0, ListingCounts));
    }

    // A selection the game's retainer list does not hold, such as another character's.
    [Fact]
    public void A_selection_missing_from_the_retainer_list_owns_nothing()
    {
        Assert.Null(RetainerRoster.MarketOwner(true, 0, Stilt, ListingCounts));
    }

    // The count read disagrees with the owner's own count: the container holds someone else's
    // listings, or none yet, so the listings cannot be credited to anyone this pass.
    [Theory]
    [InlineData(Mogwin, 0)]
    [InlineData(Mogwin, 5)]
    [InlineData(Kupo, 3)]
    public void Listings_whose_count_disagrees_with_the_owner_own_nothing(ulong owner, int listingsRead)
    {
        Assert.Null(RetainerRoster.MarketOwner(true, listingsRead, owner, ListingCounts));
    }
}
