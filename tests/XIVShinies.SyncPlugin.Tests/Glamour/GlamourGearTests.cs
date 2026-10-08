using Xunit;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// GlamourGear decides which held items count as glamour gear, from the plain values the collector
// looks up on the game's item sheet. The sheet lookup itself is verified by in-game QA.
public class GlamourGearTests
{
    // EquipSlotCategory row ids. Row 0 means "equips nowhere"; row 4 is the body slot, and row 17
    // is the slot a soul crystal equips to.
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

    // Materials, consumables, minions and the like link to row 0: they cannot be equipped at all.
    [Fact]
    public void An_item_with_no_equip_slot_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(itemRowFound: true, equipSlotCategoryRowId: NoSlot, soulCrystal: 0));
    }

    // An id the item sheet has no row for is not an item this plugin can say anything about.
    [Fact]
    public void An_id_with_no_item_row_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(itemRowFound: false, equipSlotCategoryRowId: BodySlot, soulCrystal: 0));
    }

    // The item names an equip slot row the sheet cannot supply, so nothing says the item is not a
    // soul crystal: it is left out rather than guessed at. Null is how the caller says "not read".
    [Fact]
    public void An_unreadable_equip_slot_row_does_not_qualify()
    {
        Assert.False(
            GlamourGear.Qualifies(itemRowFound: true, equipSlotCategoryRowId: BodySlot, soulCrystal: null));
    }
}
