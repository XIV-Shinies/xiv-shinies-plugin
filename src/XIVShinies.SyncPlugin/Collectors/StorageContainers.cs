using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// The live inventory containers that can hold gear, shared by every collector that walks them.
/// </summary>
/// <remarks>
/// <para>
/// Kept in one place so the items and glamour categories walk the same containers: a container
/// added here reaches both. <c>ArmoryFeets</c> and <c>ArmoryEar</c> are the game's own spellings,
/// as FFXIVClientStructs names them.
/// </para>
/// <para>
/// <b>Never listed, and never to be walked:</b> the live saddlebag containers (<c>SaddleBag1</c>,
/// <c>SaddleBag2</c>, <c>PremiumSaddleBag1</c>, <c>PremiumSaddleBag2</c>) and the live retainer
/// containers (<c>RetainerPage1</c> to <c>RetainerPage7</c>, <c>RetainerEquippedItems</c>). They
/// report themselves as loaded while still empty before their first use in a session, and
/// afterwards keep the last summoned retainer's contents, so a walk of them can neither tell an
/// empty saddlebag from an unread one nor say which retainer's gear it is seeing. The saddlebag and
/// the retainers are read from the item finder's copies instead, which carry their own read gates
/// (see <see cref="StorageSources"/>).
/// </para>
/// <para>
/// <b>The single exception is <c>RetainerMarket</c></b>, the live container holding the market
/// listings of the retainer summoned most recently this session. It is not listed here either, and
/// only <see cref="GlamourCollector"/> reads it, and only to <i>add</i> copies to its held counts —
/// never as the record of what a container holds. The listings are in no item finder copy (a
/// listed piece leaves the retainer's bags), so this is the only place they can be seen; that
/// collector's held read documents why no copy is counted twice, and which listings it cannot see.
/// </para>
/// </remarks>
// `internal` keeps this visible only inside the plugin (and to its test project, which the project
// file grants access), like a module-private export. The list is fixed twice over: `static
// readonly` means the field is created once and never reassigned, and `IReadOnlyList<T>` lets code
// read, index and loop over the list but offers no way to change its contents — the counterpart of
// a TypeScript `readonly T[]`. The `[ ... ]` collection expression below builds it.
internal static class StorageContainers
{
    /// <summary>
    /// Every live container that can hold gear: the four carried bags, the equipped set, and every
    /// armory chest except the soul crystal chest.
    /// </summary>
    /// <remarks>
    /// What a container that is not loaded means is each collector's own decision. The glamour
    /// category skips the whole pass (see <see cref="GlamourCollector"/>); the items category counts
    /// what it can and passes over such a container.
    /// </remarks>
    internal static readonly IReadOnlyList<InventoryType> GearContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];
}
