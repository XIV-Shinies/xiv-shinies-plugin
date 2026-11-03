using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// The live inventory containers that can hold gear, shared by every collector that walks them.
/// </summary>
/// <remarks>
/// Shared so the items and glamour categories walk the same containers. The live saddlebag and
/// retainer containers (<c>SaddleBag1</c>, <c>PremiumSaddleBag1</c>, <c>RetainerPage1</c>,
/// <c>RetainerEquippedItems</c> and their siblings) are never walked: they report loaded while empty
/// before first use, and the retainer ones then keep the last summoned retainer's contents, so a walk
/// cannot tell empty from unread or whose gear it sees, and the item finder's gated copies are read
/// instead (see <see cref="StorageSources"/>). The one exception, <c>RetainerMarket</c>, is read
/// only by <see cref="GlamourCollector"/> because listings are in no item finder copy; it shares the
/// same weakness, so <see cref="Glamour.RetainerRoster.MarketOwner"/> decides whose listings it holds.
/// </remarks>
internal static class StorageContainers
{
    /// <summary>
    /// Every live container that can hold gear, with the held place it counts under: the four
    /// carried bags, the equipped set, and every armory chest except the soul crystal chest.
    /// </summary>
    /// <remarks>
    /// A container added here has to name where its copies count; nothing falls into a place by
    /// default. What an unloaded container means is each collector's choice: the glamour category
    /// skips the pass (see <see cref="GlamourCollector"/>), and the items category passes over it.
    /// </remarks>
    // ArmoryFeets and ArmoryEar are FFXIVClientStructs' own spellings.
    internal static readonly IReadOnlyList<(InventoryType Type, string Place)> GearContainers =
    [
        (InventoryType.Inventory1, HeldPlaces.Bags),
        (InventoryType.Inventory2, HeldPlaces.Bags),
        (InventoryType.Inventory3, HeldPlaces.Bags),
        (InventoryType.Inventory4, HeldPlaces.Bags),
        (InventoryType.EquippedItems, HeldPlaces.Equipped),
        (InventoryType.ArmoryMainHand, HeldPlaces.Armory),
        (InventoryType.ArmoryOffHand, HeldPlaces.Armory),
        (InventoryType.ArmoryHead, HeldPlaces.Armory),
        (InventoryType.ArmoryBody, HeldPlaces.Armory),
        (InventoryType.ArmoryHands, HeldPlaces.Armory),
        (InventoryType.ArmoryWaist, HeldPlaces.Armory),
        (InventoryType.ArmoryLegs, HeldPlaces.Armory),
        (InventoryType.ArmoryFeets, HeldPlaces.Armory),
        (InventoryType.ArmoryEar, HeldPlaces.Armory),
        (InventoryType.ArmoryNeck, HeldPlaces.Armory),
        (InventoryType.ArmoryWrist, HeldPlaces.Armory),
        (InventoryType.ArmoryRings, HeldPlaces.Armory),
    ];
}
