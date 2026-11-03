using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Settings advice keys on a skip reason, never a category, so the Achievements hint needs no
// category-name branch. Each phrase is drawn as "{DisplayName}: {phrase}" (see ReadStatusViewTests),
// so each string is asserted whole: it must read correctly there.
public class CollectSkipReasonsTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

    [Fact]
    public void An_unloaded_achievement_list_becomes_advice_the_user_can_act_on()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.AchievementListNotLoaded, Host);

        Assert.Equal(
            "not read yet — open your Achievements window in game once.", hint);
    }

    // A keyword check would still pass with two messages swapped. The string names no category's
    // noun; CollectSkipReasons.Describe explains why the reason is shared.
    [Fact]
    public void A_missing_manifest_explains_that_we_are_waiting_on_the_server()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoRemoteConfig, Host);

        Assert.Equal(
            "not read yet — waiting for " + Host + " to say what to look for.", hint);
    }

    // Switched off, or not asked for by the server: a decision, not a failure.
    [Theory]
    [InlineData(CollectSkipReasons.Disabled)]
    [InlineData(CollectSkipReasons.ManifestNotOffered)]
    public void A_deliberate_skip_is_not_a_fault(string reason)
    {
        Assert.True(CollectSkipReasons.IsDeliberate(reason));
        Assert.Null(CollectSkipReasons.Describe(reason, Host));
    }

    // A collection read from a window the player opens has nothing to send until they open it. That
    // is no fault, and a line asking for it would return every session.
    [Fact]
    public void Nothing_seen_yet_is_not_a_fault_and_draws_no_line()
    {
        Assert.True(CollectSkipReasons.IsDeliberate(CollectSkipReasons.NothingSeenYet));
        Assert.Null(CollectSkipReasons.Describe(CollectSkipReasons.NothingSeenYet, Host));
    }

    [Theory]
    [InlineData(CollectSkipReasons.NoRemoteConfig)]
    [InlineData(CollectSkipReasons.CollectorError)]
    [InlineData(CollectSkipReasons.SheetUnavailable)]
    [InlineData(CollectSkipReasons.InventoryUnavailable)]
    [InlineData(CollectSkipReasons.NoItemGroupsEnabled)]
    [InlineData(CollectSkipReasons.AchievementListNotLoaded)]
    [InlineData(CollectSkipReasons.NotInOccultInstance)]
    [InlineData(CollectSkipReasons.StorageWindowOpen)]
    [InlineData(CollectSkipReasons.StorageUnreadable)]
    [InlineData(CollectSkipReasons.LocalPlayerUnavailable)]
    [InlineData(CollectSkipReasons.Transformed)]
    [InlineData(CollectSkipReasons.OverCap)]
    [InlineData(CollectSkipReasons.UnexpectedLayout)]
    [InlineData("some_future_reason")]
    public void Every_other_reason_is_a_miss(string reason)
    {
        Assert.False(CollectSkipReasons.IsDeliberate(reason));
    }

    [Fact]
    public void An_unreadable_inventory_asks_the_user_to_log_in()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.InventoryUnavailable, Host);

        Assert.Equal(
            "not read yet — log in to a character so your inventory can be read.", hint);
    }

    // With every group off the collection reads nothing, and only the user can tick a box. Without
    // this line it would upload nothing forever, silently.
    [Fact]
    public void A_collection_with_no_groups_enabled_asks_the_user_to_tick_one()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoItemGroupsEnabled, Host);

        Assert.Equal(
            "not read — none of its groups are switched on. Tick at least one under Collections to " +
            "include it.",
            hint);
    }

    [Fact]
    public void Being_outside_the_occult_instance_names_the_syncs_that_read_it_inside()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NotInOccultInstance, Host);

        Assert.Equal(
            "not read yet — the automatic sync or Sync now reads it while you are inside the Occult " +
            "Crescent.",
            hint);
    }

    // Pins every storage window the hint names (CollectSkipReasons.Describe says why each is named).
    [Fact]
    public void An_open_storage_window_asks_the_user_to_close_it_and_sync_again()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.StorageWindowOpen, Host);

        Assert.Equal(
            "not read this pass — close your Glamour Dresser, Armoire, saddlebag or retainer " +
            "window, then press Sync now.",
            hint);
    }

    // Unlike the inventory hint, this names no container: what is missing is the character itself.
    [Fact]
    public void A_missing_local_player_asks_the_user_to_log_in()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.LocalPlayerUnavailable, Host);

        Assert.Equal("not read yet — log in to a character so it can be read.", hint);
    }

    // Pins that the hint names the Sync now button as well as the return to normal
    // (CollectSkipReasons.Describe says why).
    [Fact]
    public void A_transformed_character_asks_the_user_to_return_to_normal_and_sync_again()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.Transformed, Host);

        Assert.Equal(
            "not read this pass — your character was transformed; return to normal, then press " +
            "Sync now.",
            hint);
    }

    // A deliberate skip draws no line at all (see CollectSkipReasons.IsDeliberate).
    [Fact]
    public void A_disabled_category_needs_no_explanation()
    {
        Assert.Null(CollectSkipReasons.Describe(CollectSkipReasons.Disabled, Host));
    }

    // The user cannot act in game on a bug, a misread, a version mismatch or a passing game state,
    // and the raw wire string means nothing to them.
    [Theory]
    [InlineData(CollectSkipReasons.CollectorError)]
    [InlineData(CollectSkipReasons.SheetUnavailable)]
    [InlineData(CollectSkipReasons.UnexpectedLayout)]
    [InlineData(CollectSkipReasons.OverCap)]
    [InlineData(CollectSkipReasons.StorageUnreadable)]
    public void A_reason_the_user_cannot_act_on_produces_no_advice(string reason)
    {
        Assert.Null(CollectSkipReasons.Describe(reason, Host));
    }

    [Fact]
    public void An_unrecognized_reason_produces_no_advice_rather_than_its_raw_code()
    {
        Assert.Null(CollectSkipReasons.Describe("some_future_reason", Host));
    }
}
