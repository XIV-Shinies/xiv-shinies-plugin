using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>What the run HUD showed: the token balance and what the player is carrying.</summary>
/// <param name="Tokens">The token balance.</param>
/// <param name="ItemIds">
/// The <c>XBMItem</c> id in each filled item slot, in slot order. A stack sits in one slot, so an
/// item held in two slots is listed twice; the reading does not total them.
/// </param>
/// <param name="GearIds">The <c>XBMItem</c> id in each filled gear slot, in slot order.</param>
// A positional `record` declares a small immutable data type in one line: each parameter becomes a
// read-only property, like a frozen TypeScript object built by a constructor. Unlike TypeScript
// objects, two records with equal properties compare equal (a list property is equal only when it
// is the same list object). `sealed` means nothing can subclass it. `IReadOnlyList<uint>` is a
// list that code receiving it cannot change, like `readonly number[]`; `uint` is a whole number
// that cannot be negative, which is how the game stores ids.
public sealed record CrucibleBagReading(int Tokens, IReadOnlyList<uint> ItemIds, IReadOnlyList<uint> GearIds);

/// <summary>Reads the run HUD (<c>XBMContentsMainHUD</c>) from the values the window was given.</summary>
/// <remarks>
/// <para>
/// The HUD is open for the whole run and carries the token balance as text, then ten item slots and
/// ten gear slots, each a run of five values that opens with a flag and carries the item's id three
/// values on. An empty item slot is still flagged as shown and carries id 0; an empty gear slot is
/// flagged unfilled and carries no id at all.
/// </para>
/// <para>
/// Every slot's kind of value is checked (see <see cref="AddonValue"/>), and any miss refuses the
/// whole reading.
/// </para>
/// </remarks>
public static class CrucibleHud
{
    /// <summary>Where the token balance sits, as text.</summary>
    private const int TokensIndex = 8;

    /// <summary>How many item slots and how many gear slots the HUD has.</summary>
    private const int SlotCount = 10;

    /// <summary>How many values each slot occupies.</summary>
    private const int SlotStride = 5;

    /// <summary>Where the first item slot's "shown" flag sits.</summary>
    private const int FirstItemSlot = 9;

    /// <summary>Where the first gear slot's "filled" flag sits.</summary>
    private const int FirstGearSlot = 60;

    /// <summary>Where a slot's item id sits, from the slot's flag.</summary>
    private const int IdOffset = 3;

    /// <summary>The highest position the layout reads: the last gear slot's id.</summary>
    private const int LastIndex = FirstGearSlot + ((SlotCount - 1) * SlotStride) + IdOffset;

    /// <summary>
    /// Reads the HUD, or null when the values do not have the layout this reader knows.
    /// </summary>
    /// <param name="values">Every value the window was handed, in order.</param>
    public static CrucibleBagReading? Read(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= LastIndex)
            return null;

        // `is not { } tokens` is true when the balance does not read as a number, and the `if`
        // returns; past it, `tokens` holds the number itself (see CrucibleText for the pattern).
        if (CrucibleText.ReadNumber(values[TokensIndex].Text) is not { } tokens)
            return null;

        var items = ReadSlots(values, FirstItemSlot);
        var gear = ReadSlots(values, FirstGearSlot);

        // `is null` on either list means a slot held the wrong kind of value: the layout has moved.
        if (items is null || gear is null)
            return null;

        return new CrucibleBagReading(tokens, items, gear);
    }

    /// <summary>
    /// The ids in a run of ten slots, or null when a slot holds the wrong kind of value.
    /// </summary>
    /// <param name="values">Every value the window was handed.</param>
    /// <param name="firstSlot">Where the first slot's flag sits.</param>
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
