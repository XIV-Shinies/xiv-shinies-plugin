using Xunit;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// Which held items count as glamour gear, from what the collector looks up on the item sheet.
public class GlamourGearTests
{
    // EquipSlotCategory rows: 0 equips nowhere, 4 is the body, and 17 is the soul crystal slot.
    private const uint NoSlot = 0;
    private const uint BodySlot = 4;
    private const uint SoulCrystalSlot = 17;

    [Fact]
    public void Ordinary_gear_qualifies()
    {
        Assert.True(
            GlamourGear.Qualifies(itemRowFound: true, equipSlotCategoryRowId: BodySlot, soulCrystal: 0));
    }

    // A soul crystal has an equip slot of its own, but it never changes how the character looks.
    [Fact]
    public void A_soul_crystal_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(
                itemRowFound: true, equipSlotCategoryRowId: SoulCrystalSlot, soulCrystal: 1));
    }

    // Materials, consumables, minions and the like link to row 0.
    [Fact]
    public void An_item_with_no_equip_slot_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(itemRowFound: true, equipSlotCategoryRowId: NoSlot, soulCrystal: 0));
    }

    [Fact]
    public void An_id_with_no_item_row_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(itemRowFound: false, equipSlotCategoryRowId: BodySlot, soulCrystal: 0));
    }

    // Null is the caller's "not read". Nothing then says the item is not a soul crystal, so it is
    // left out rather than guessed at.
    [Fact]
    public void An_unreadable_equip_slot_row_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(itemRowFound: true, equipSlotCategoryRowId: BodySlot, soulCrystal: null));
    }
}
