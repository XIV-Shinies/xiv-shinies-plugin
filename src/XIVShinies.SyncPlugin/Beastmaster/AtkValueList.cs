using System;
using System.Collections;
using System.Collections.Generic;
// AtkValue, the game's own value type: what a window is handed to draw itself from.
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// The values a game window was handed, seen as <see cref="AddonValue"/>s and narrowed one at a time
/// as a reader asks for them.
/// </summary>
/// <remarks>
/// <para>
/// A window can carry tens of thousands of values (the Crucible's board window has over 40,000) and
/// redraw many times a second, so nothing is copied up front: a reader that looks at a few hundred of
/// them narrows only those.
/// </para>
/// <para>
/// It reads straight from the window's memory, so it is valid only during the call that wrapped it (a
/// lifecycle callback, or a read of a window that is open on this frame), and must never be kept past
/// it.
/// </para>
/// </remarks>
// `IReadOnlyList<T>` is the interface for a list its holder cannot change, like `readonly T[]`; the
// readers take one, so they cannot tell this from an ordinary list. `sealed` means nothing can
// subclass it.
public sealed class AtkValueList : IReadOnlyList<AddonValue>
{
    // `unsafe` marks code that uses raw pointers into the game's memory, where C#'s references and
    // bounds checks do not apply. `AtkValue*` is a pointer to the first of the window's values.
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
    // An indexer: what `list[index]` calls. Comparing as `uint` folds "below zero" and "past the end"
    // into one check, because a negative number cast to `uint` wraps to a very large one.
    // `&values[index]` is the address of that value, a pointer to it rather than a copy. `throw` can
    // stand where a value is expected, and `nameof(index)` is the parameter's name as text.
    public unsafe AddonValue this[int index] =>
        (uint)index < (uint)Count
            ? Narrow(&values[index])
            : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Walks every value, narrowing each in turn.</summary>
    // `yield return` hands back one value at a time, like a JavaScript generator's `yield`.
    public IEnumerator<AddonValue> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index];
    }

    // The older, untyped form of the same walk, which every list must also offer. Naming the
    // interface in front of the method (`IEnumerable.GetEnumerator`) makes it reachable only through
    // that interface, so it never competes with the typed one above.
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Reduces one of a window's values to the three shapes the readers read.</summary>
    /// <remarks>
    /// Anything else becomes an unreadable slot rather than a guess. That is what lets a reader
    /// recognize where a run of records stops, since the values after it are of kinds a record never
    /// holds.
    /// </remarks>
    /// <param name="value">The value; never null.</param>
    public static unsafe AddonValue Narrow(AtkValue* value)
    {
        // `->` reaches a member through a pointer, as `.` does through a reference.
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
