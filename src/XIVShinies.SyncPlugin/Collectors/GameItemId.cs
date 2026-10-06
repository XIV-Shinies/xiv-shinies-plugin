namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>How the item finder's stored item ids encode quality, and how to read them back.</summary>
/// <remarks>
/// <para>
/// Every copy the item finder keeps — the glamour dresser, the saddlebags, and the retainers —
/// stores a high-quality item as its id plus one million rather than carrying a separate quality
/// flag, and so does the dresser's live <c>MirageManager</c> copy. <see cref="Split"/> reads that
/// encoding back out, and leaves an id outside the high-quality range as a normal-quality copy of
/// itself. Live inventory containers are different: their slots carry the base id beside a
/// separate high-quality flag, so their ids need no decoding.
/// </para>
/// <para>
/// A base id of 0 is an empty slot. An empty slot stores 0, and a bare one million (the
/// high-quality offset over nothing) splits to base id 0 as well, so a caller that skips base id 0
/// skips both.
/// </para>
/// </remarks>
// `static class` is a class that can never be instantiated and holds only static members: the
// closest C# analog to a TypeScript module that exports plain functions and constants.
public static class GameItemId
{
    // The offset the game adds to a high-quality item's id.
    // `const` is a compile-time constant, like a module-level `const` in TypeScript, except that the
    // compiler copies its value into every caller at build time. `1_000_000` is the same number as
    // 1000000; the underscores are only digit separators for readability. `private` keeps it
    // internal to this class, as an un-exported constant would be in a TypeScript module.
    private const uint HqOffset = 1_000_000;

    // The first id beyond the HQ range. Event items live from two million up; they are never
    // high-quality gear, so they must not be shifted down into the item range.
    private const uint HqRangeEnd = 2_000_000;

    /// <summary>Splits a stored id into its base item id and whether the copy is high quality.</summary>
    // The return type is a named tuple: a lightweight value type (a struct, not a class) that groups
    // values and labels its fields. It is the C# counterpart of returning `{ baseId, isHq }` from
    // a TypeScript function, and a caller can unpack it with
    // `var (baseId, isHq) = GameItemId.Split(id);`.
    //
    // `storedId is >= HqOffset and < HqRangeEnd` is a relational pattern: it reads as
    // `storedId >= HqOffset && storedId < HqRangeEnd`, with the value named only once.
    public static (uint BaseId, bool IsHq) Split(uint storedId) =>
        storedId is >= HqOffset and < HqRangeEnd
            ? (storedId - HqOffset, true)
            : (storedId, false);
}
