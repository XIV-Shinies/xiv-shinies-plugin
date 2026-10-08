namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>The rule for which held items the <c>glamour</c> category reports as gear.</summary>
/// <remarks>
/// <para>
/// "Glamour gear" here is any item the game lets a character equip: the item sheet links each item
/// to an equip slot category, and row 0 of that sheet means "equips nowhere" (materials,
/// consumables, minions and so on). Soul crystals do have an equip slot, but they never change how
/// the character looks, so they are left out. The rule is deliberately broad, and some of the ids it
/// lets through can never be a glamour. That costs a few ids on the wire and never a wrong fact: the
/// server ignores any id its own catalog does not know.
/// </para>
/// <para>
/// Pure on purpose: it takes the plain values the collector looks up on the game's sheets, so the
/// rule is unit-tested while the lookup itself, which needs the game's data files, is verified in
/// game.
/// </para>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports plain functions.
public static class GlamourGear
{
    /// <summary>True when an item counts as glamour gear.</summary>
    /// <param name="itemRowFound">Whether the item sheet has a row for the id at all.</param>
    /// <param name="equipSlotCategoryRowId">
    /// The <c>EquipSlotCategory</c> row the item links to; 0 means it equips nowhere.
    /// </param>
    /// <param name="soulCrystal">
    /// That equip slot row's soul crystal column (non-zero for a soul crystal), or null when the row
    /// could not be read.
    /// </param>
    /// <remarks>
    /// Anything the lookup could not answer counts as not gear: an id with no item row, or an item
    /// whose equip slot row cannot be read, since nothing then says it is not a soul crystal.
    /// </remarks>
    // `sbyte?` is a nullable signed byte, the type the game's sheet gives that column with a third
    // state added: like `number | null` in TypeScript, null here meaning "could not be read".
    // Comparing a nullable to a number is true only when it holds that number, so `soulCrystal == 0`
    // is false both for a soul crystal and for an unreadable row.
    public static bool Qualifies(bool itemRowFound, uint equipSlotCategoryRowId, sbyte? soulCrystal) =>
        itemRowFound && equipSlotCategoryRowId != 0 && soulCrystal == 0;
}
