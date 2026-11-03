namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>The rule for which held items the <c>glamour</c> category reports as gear.</summary>
/// <remarks>
/// Gear is any item the game lets a character equip; equip slot category row 0 means it equips
/// nowhere. Soul crystals are left out, since they never change how the character looks. The rule
/// is broad on purpose: an id that can never be a glamour costs a few bytes and never a wrong fact,
/// because the server ignores any id its catalog does not know.
/// </remarks>
public static class GlamourGear
{
    /// <summary>True when an item counts as glamour gear.</summary>
    /// <param name="itemRowFound">Whether the item sheet has a row for the id at all.</param>
    /// <param name="equipSlotCategoryRowId">
    /// The <c>EquipSlotCategory</c> row the item links to; 0 means it equips nowhere.
    /// </param>
    /// <param name="soulCrystal">
    /// That equip slot row's soul crystal column (non-zero for a soul crystal), or null when the
    /// row could not be read.
    /// </param>
    /// <remarks>
    /// Anything the lookup could not answer counts as not gear: an id with no item row, or an item
    /// whose equip slot row cannot be read, since nothing then says it is not a soul crystal.
    /// </remarks>
    public static bool Qualifies(bool itemRowFound, uint equipSlotCategoryRowId, sbyte? soulCrystal) =>
        itemRowFound && equipSlotCategoryRowId != 0 && soulCrystal == 0;
}
