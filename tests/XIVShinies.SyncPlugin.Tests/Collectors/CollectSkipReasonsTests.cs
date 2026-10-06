using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Turns a collector's skip reason into advice for the settings window. Note what it switches on: a
// REASON, never a category. That is what lets the "open your Achievements window" hint exist without
// anyone writing `if (key == "achievements")` anywhere in the plugin.
//
// Every described reason is drawn after its category's display name, as "{DisplayName}: {phrase}" (see
// ReadStatusViewTests), so each string is asserted whole: it has to READ correctly in that position,
// not merely contain the right keyword.
public class CollectSkipReasonsTests
{
    // The hint the extensibility contract names explicitly as the thing that must NOT be a special
    // case in the settings UI.
    [Fact]
    public void An_unloaded_achievement_list_becomes_advice_the_user_can_act_on()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.AchievementListNotLoaded);

        Assert.Equal(
            "not read yet — open your Achievements window in game once, then press Sync now.", hint);
    }

    // The whole string is asserted, not just a keyword: a keyword check would still pass if two
    // messages were swapped, and would tell the user to log in when the manifest is what is
    // missing. The string names no category-specific noun — see CollectSkipReasons.Describe for
    // why the reason is shared.
    [Fact]
    public void A_missing_manifest_explains_that_we_are_waiting_on_the_server()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoRemoteConfig);

        Assert.Equal(
            "not read yet — waiting for XIV Shinies to say what to look for.", hint);
    }

    [Fact]
    public void An_unreadable_inventory_asks_the_user_to_log_in()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.InventoryUnavailable);

        Assert.Equal(
            "not read yet — log in to a character so your inventory can be read.", hint);
    }

    // A collection whose consent groups are all switched off looks up nothing at all. The advice has to
    // name the one thing that resolves it, because nothing else will: the plugin cannot tick the boxes
    // for the user, and a collection that quietly uploads nothing forever is the failure this line
    // exists to prevent.
    [Fact]
    public void A_collection_with_no_groups_enabled_asks_the_user_to_tick_one()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NoItemGroupsEnabled);

        Assert.Equal(
            "not read — none of its groups are switched on. Tick at least one under Collections to " +
            "include it.",
            hint);
    }

    [Fact]
    public void Being_outside_the_occult_instance_asks_the_user_to_enter_the_crescent()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.NotInOccultInstance);

        Assert.Equal(
            "not read yet — enter the Occult Crescent once; it syncs during your visit.", hint);
    }

    // Pins every storage window the hint names (CollectSkipReasons.Describe says why each is named).
    [Fact]
    public void An_open_storage_window_asks_the_user_to_close_it_and_sync_again()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.StorageWindowOpen);

        Assert.Equal(
            "not read this pass — close your Glamour Dresser, Armoire, saddlebag or retainer " +
            "window, then press Sync now.",
            hint);
    }

    // Unlike the inventory hint, this names no container: what is missing is the character itself.
    [Fact]
    public void A_missing_local_player_asks_the_user_to_log_in()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.LocalPlayerUnavailable);

        Assert.Equal("not read yet — log in to a character so it can be read.", hint);
    }

    // Pins that the hint names the Sync now button as well as the return to normal
    // (CollectSkipReasons.Describe says why).
    [Fact]
    public void A_transformed_character_asks_the_user_to_return_to_normal_and_sync_again()
    {
        var hint = CollectSkipReasons.Describe(CollectSkipReasons.Transformed);

        Assert.Equal(
            "not read this pass — your character was transformed; return to normal, then press " +
            "Sync now.",
            hint);
    }

    // The checkbox beside the category already says it is off; repeating it would be noise.
    [Fact]
    public void A_disabled_category_needs_no_explanation()
    {
        Assert.Null(CollectSkipReasons.Describe(CollectSkipReasons.Disabled));
    }

    // Bugs, misreads, version mismatches and transient game states are not things the user can act
    // on in game, and the raw wire string would mean nothing to them.
    [Theory]
    [InlineData(CollectSkipReasons.CollectorError)]
    [InlineData(CollectSkipReasons.SheetUnavailable)]
    [InlineData(CollectSkipReasons.UnexpectedLayout)]
    [InlineData(CollectSkipReasons.OverCap)]
    [InlineData(CollectSkipReasons.StorageUnreadable)]
    public void A_reason_the_user_cannot_act_on_produces_no_advice(string reason)
    {
        Assert.Null(CollectSkipReasons.Describe(reason));
    }

    // A reason invented by a future collector must not surface a raw code like "facewear_locked".
    [Fact]
    public void An_unrecognized_reason_produces_no_advice_rather_than_its_raw_code()
    {
        Assert.Null(CollectSkipReasons.Describe("some_future_reason"));
    }
}
