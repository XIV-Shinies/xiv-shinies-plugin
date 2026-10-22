using System;
using System.Collections;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// A game window's values as <see cref="AddonValue"/>s, each narrowed only when a reader asks for it.
/// </summary>
/// <remarks>
/// A window can hold tens of thousands of values (the Crucible's board window has over 40,000) and
/// redraw many times a second, so nothing is copied up front. It reads the window's memory directly,
/// so it is valid only during the call that wrapped it, a lifecycle callback or a read of a window
/// open this frame, and must never be kept past it.
/// </remarks>
public sealed class AtkValueList : IReadOnlyList<AddonValue>
{
    private readonly unsafe AtkValue* values;

    /// <summary>Wraps a window's values.</summary>
    /// <param name="values">The first of the window's values; never null.</param>
    /// <param name="count">How many values the window holds.</param>
    public unsafe AtkValueList(AtkValue* values, int count)
    {
        this.values = values;
        Count = count;
    }

    /// <summary>How many values the window holds.</summary>
    public int Count { get; }

    /// <summary>The value at this position, narrowed.</summary>
    /// <param name="index">The position, from 0.</param>
    public unsafe AddonValue this[int index] =>
        (uint)index < (uint)Count
            ? Narrow(&values[index])
            : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Walks every value, narrowing each in turn.</summary>
    public IEnumerator<AddonValue> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Reduces one of a window's values to the three shapes the readers read.</summary>
    /// <remarks>
    /// Anything else becomes an unreadable slot, not a guess. That lets a reader see where a run of
    /// records stops, since the values after it are kinds a record never holds.
    /// </remarks>
    /// <param name="value">The value; never null.</param>
    public static unsafe AddonValue Narrow(AtkValue* value)
    {
        switch (value->Type)
        {
            case AtkValueType.Int:
                // Negative values are not ids or counts, and would wrap if widened.
                return value->Int >= 0 ? AddonValue.FromInteger((uint)value->Int) : AddonValue.Unreadable;

            case AtkValueType.UInt:
                return AddonValue.FromInteger(value->UInt);

            case AtkValueType.Bool:
                return AddonValue.FromBoolean(value->Byte != 0);

            case AtkValueType.String:
            case AtkValueType.ConstString:
            case AtkValueType.ManagedString:
                return value->String.Value == null
                    ? AddonValue.Unreadable
                    : AddonValue.FromText(value->String.ToString());

            default:
                return AddonValue.Unreadable;
        }
    }
}
