using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>What the run HUD showed: the token balance and what the player is carrying.</summary>
/// <param name="ItemIds">
/// The <c>XBMItem</c> id in each filled item slot, in slot order. A stack sits in one slot, so an
/// item held in two slots is listed twice; the reading does not total them.
/// </param>
/// <param name="GearIds">The <c>XBMItem</c> id in each filled gear slot, in slot order.</param>
public sealed record CrucibleBagReading(int Tokens, IReadOnlyList<uint> ItemIds, IReadOnlyList<uint> GearIds);

/// <summary>Reads the run HUD (<c>XBMContentsMainHUD</c>) from the values the window was given.</summary>
/// <remarks>
/// The HUD is open for the whole run and carries the token balance as text, then ten item slots and
/// ten gear slots of five values: a flag, then the item's id three values on. An empty item slot is
/// flagged shown with id 0, and an empty gear slot is flagged unfilled with no id. Any slot holding
/// the wrong kind of value (see <see cref="AddonValue"/>) refuses the whole reading.
/// </remarks>
public static class CrucibleHud
{
    // The window's layout. A slot starts at its flag, and IdOffset counts from there.
    private const int TokensIndex = 8;
    private const int SlotCount = 10;
    private const int SlotStride = 5;
    private const int FirstItemSlot = 9;
    private const int FirstGearSlot = 60;
    private const int IdOffset = 3;

    /// <summary>The highest position the layout reads: the last gear slot's id.</summary>
    private const int LastIndex = FirstGearSlot + ((SlotCount - 1) * SlotStride) + IdOffset;

    /// <summary>
    /// Reads the HUD, or null when the values do not have the layout this reader knows.
    /// </summary>
    public static CrucibleBagReading? Read(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= LastIndex)
            return null;

        if (CrucibleText.ReadNumber(values[TokensIndex].Text) is not { } tokens)
            return null;

        var items = ReadSlots(values, FirstItemSlot);
        var gear = ReadSlots(values, FirstGearSlot);

        // A null list means a slot held the wrong kind of value: the layout has moved.
        if (items is null || gear is null)
            return null;

        return new CrucibleBagReading(tokens, items, gear);
    }

    /// <summary>
    /// The ids in a run of ten slots, or null when a slot holds the wrong kind of value.
    /// </summary>
    private static List<uint>? ReadSlots(IReadOnlyList<AddonValue> values, int firstSlot)
    {
        var ids = new List<uint>(SlotCount);

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var flagIndex = firstSlot + (slot * SlotStride);

            // Every slot carries its flag, filled or not.
            if (values[flagIndex].Flag is not { } filled)
                return null;

            if (!filled)
                continue;

            // A flagged slot carries its id, which is 0 when the slot is empty.
            if (values[flagIndex + IdOffset].Number is not { } id)
                return null;

            if (id > 0)
                ids.Add(id);
        }

        return ids;
    }
}
