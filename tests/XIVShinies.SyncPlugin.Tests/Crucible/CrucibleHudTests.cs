using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The run HUD (XBMContentsMainHUD): the token balance and what the player is carrying.
public class CrucibleHudTests
{
    // Positions in the HUD's values that the tests below change.
    private const int Tokens = 8;
    private const int FirstItemFlag = 9;
    private const int FirstItemId = 12;
    private const int SecondItemId = 17;
    private const int FirstGearFlag = 60;
    private const int FirstGearId = 63;

    [Fact]
    public void Reads_the_balance_items_and_gear_from_the_hud()
    {
        var reading = CrucibleHud.Read(WindowFixture.Load("hud.json"));

        Assert.NotNull(reading);
        Assert.Equal(225, reading.Tokens);
        Assert.Equal(new uint[] { 78, 130, 138 }, reading.ItemIds);
        Assert.Equal(new uint[] { 2, 45 }, reading.GearIds);
    }

    [Fact]
    public void A_repeated_item_is_listed_once_per_slot()
    {
        var values = WindowFixture.Load("hud.json");
        values[SecondItemId] = AddonValue.FromInteger(78);

        Assert.Equal(new uint[] { 78, 78, 138 }, CrucibleHud.Read(values)!.ItemIds);
    }

    // A HUD whose balance is not a number is not the one this reader knows, so the reading is refused
    // rather than returned without its tokens.
    [Fact]
    public void An_unreadable_balance_refuses_the_reading()
    {
        var values = WindowFixture.Load("hud.json");
        values[Tokens] = AddonValue.FromText("Crucible Items");

        Assert.Null(CrucibleHud.Read(values));
    }

    [Theory]
    [InlineData(FirstItemFlag)]
    [InlineData(FirstItemId)]
    [InlineData(FirstGearFlag)]
    [InlineData(FirstGearId)]
    public void A_slot_of_the_wrong_kind_refuses_the_reading(int index)
    {
        var values = WindowFixture.Load("hud.json");
        values[index] = AddonValue.FromText("G3 Beast Potion");

        Assert.Null(CrucibleHud.Read(values));
    }

    // The reading is refused rather than cut short at the last value.
    [Fact]
    public void A_short_value_list_refuses_the_reading()
    {
        var values = WindowFixture.Load("hud.json").GetRange(0, 40);

        Assert.Null(CrucibleHud.Read(values));
    }

    // Before the HUD's first refresh it has no values at all.
    [Fact]
    public void An_empty_window_reads_as_nothing()
    {
        Assert.Null(CrucibleHud.Read(new List<AddonValue>()));
    }
}
