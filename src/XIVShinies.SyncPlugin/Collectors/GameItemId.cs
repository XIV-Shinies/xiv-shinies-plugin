namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>How the item finder's stored item ids encode quality, and how to read them back.</summary>
/// <remarks>
/// The item finder's copies (glamour dresser, saddlebags, retainers) and the dresser's live
/// <c>MirageManager</c> copy store a high-quality item as its id plus one million, with no quality
/// flag. Live inventory slots carry the base id beside a separate flag, so they need no decoding. A
/// bare one million is an empty slot, not a high-quality copy of item 0: it splits to base id 0, as
/// a stored 0 does, so skipping base id 0 skips both.
/// </remarks>
public static class GameItemId
{
    // The offset the game adds to a high-quality item's id.
    private const uint HqOffset = 1_000_000;

    // Event items start here. They are never high quality, so they must not be shifted down.
    private const uint HqRangeEnd = 2_000_000;

    /// <summary>Splits a stored id into its base item id and whether the copy is high quality.</summary>
    public static (uint BaseId, bool IsHq) Split(uint storedId) =>
        storedId is >= HqOffset and < HqRangeEnd
            ? (storedId - HqOffset, true)
            : (storedId, false);
}
