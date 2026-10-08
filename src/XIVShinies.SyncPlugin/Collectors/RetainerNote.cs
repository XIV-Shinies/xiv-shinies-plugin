using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns the two retainer counts the game can give into the retainer source note, and decides
/// whether the retainers can be read at all.
/// </summary>
/// <remarks>
/// <para>
/// The two counts come from different places. The <b>remembered count</b> is how many retainers'
/// contents the game's item finder holds a copy of. The <b>manager total</b> is how many retainers
/// the character has, as the game's retainer list reports it. Reading both out of game memory is
/// <see cref="StorageSources.Retainers"/>' job; deciding what they mean is this class's, so the
/// rules are unit-tested.
/// </para>
/// <para>
/// Both are counts only. Nothing that identifies an individual retainer is read to produce them,
/// and nothing of the kind travels in the note.
/// </para>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports plain functions. It holds no game types, which is what lets the tests call it.
public static class RetainerNote
{
    /// <summary>True when the item finder remembers at least one retainer's contents.</summary>
    /// <param name="rememberedCount">How many retainers' contents the item finder holds.</param>
    /// <remarks>
    /// The single rule both halves of a retainer read go through: a collector gates its read on it
    /// (<see cref="StorageSources.HasRememberedRetainers"/>), and <see cref="Build"/> decides the
    /// note's state with it, so a collector can never read the retainers while reporting them
    /// unscanned, or report them cached without reading them.
    /// </remarks>
    public static bool HasRemembered(int rememberedCount) => rememberedCount > 0;

    /// <summary>Builds the retainer source note from the two counts.</summary>
    /// <param name="rememberedCount">How many retainers' contents the item finder holds.</param>
    /// <param name="managerTotal">
    /// How many retainers the character has, as the game's retainer list reports it; 0 when the
    /// game has not filled that list in yet.
    /// </param>
    /// <remarks>
    /// <para>
    /// Any remembered retainer makes the source <see cref="SourceStates.Cached"/>, with the
    /// remembered count beside it. The item finder saves its copy to a local file, so it can be
    /// present after a fresh login without the player summoning a retainer: genuinely a cache, and
    /// possibly stale. With nothing remembered there is nothing to read, so the source is
    /// <see cref="SourceStates.Unscanned"/>.
    /// </para>
    /// <para>
    /// The total is what lets the note say "3 of 5 scanned" rather than a bare "3" that hides
    /// never-summoned retainers. The game's retainer list reports 0 until the game has filled it in
    /// during the session, which is indistinguishable from "has no retainers", so zero is treated as
    /// unknown and the total is left off (null, which the serializer omits).
    /// </para>
    /// </remarks>
    public static ItemSourceStatus Build(int rememberedCount, int managerTotal)
    {
        // `int?` is a nullable int, `number | null` in TypeScript.
        int? total = managerTotal > 0 ? managerTotal : null;

        if (HasRemembered(rememberedCount))
        {
            return new ItemSourceStatus
            {
                State = SourceStates.Cached,
                Count = rememberedCount,
                Total = total,
            };
        }

        return new ItemSourceStatus
        {
            State = SourceStates.Unscanned,
            Total = total,
        };
    }
}
