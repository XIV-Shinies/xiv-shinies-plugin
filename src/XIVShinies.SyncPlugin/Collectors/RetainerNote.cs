using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns the two retainer counts the game can give into the retainer source note, and decides
/// whether the retainers can be read at all.
/// </summary>
/// <remarks>
/// The remembered count is how many retainers' contents the item finder holds a copy of; the manager
/// total is how many retainers the game's retainer list reports. Both are counts only: nothing that
/// identifies a retainer is read to produce them or travels in the note.
/// </remarks>
public static class RetainerNote
{
    /// <summary>True when the item finder remembers at least one retainer's contents.</summary>
    /// <param name="rememberedCount">How many retainers' contents the item finder holds.</param>
    /// <remarks>
    /// Both the read gate (<see cref="StorageSources.HasRememberedRetainers"/>) and
    /// <see cref="Build"/> use this rule, so a collector never reads the retainers while reporting
    /// them unscanned, or reports them cached without reading them.
    /// </remarks>
    public static bool HasRemembered(int rememberedCount) => rememberedCount > 0;

    /// <summary>Builds the retainer source note from the two counts.</summary>
    /// <param name="rememberedCount">How many retainers' contents the item finder holds.</param>
    /// <param name="managerTotal">
    /// How many retainers the game's retainer list reports; 0 until the game fills that list in.
    /// </param>
    /// <remarks>
    /// Cached, not live: the item finder saves its copy to a local file, so a copy can be present
    /// after login without a summon and may be stale. The total lets the note say "3 of 5" rather than
    /// hide never-summoned retainers; a 0 would read as owning none, so it leaves the total off.
    /// </remarks>
    public static ItemSourceStatus Build(int rememberedCount, int managerTotal)
    {
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
