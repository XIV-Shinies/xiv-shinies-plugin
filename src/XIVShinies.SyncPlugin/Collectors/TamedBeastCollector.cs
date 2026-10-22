using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports the beasts the character is known to have tamed.
/// </summary>
/// <remarks>
/// Reads no game state during a pass. The structure holding the bestiary's flags is unmapped, so
/// <see cref="TamedBeastObserver"/> records the Master's Bestiary when the player opens it and each
/// beast's rank when they talk to the Crucible's NPC, and each pass hands those facts to the payload
/// until an upload succeeds. The list is a subset until the player has paged through the window, and
/// a pass earns the completeness claim only once the counts the window reports agree.
/// </remarks>
public sealed class TamedBeastCollector : ICollector
{
    private readonly IFramework framework;
    private readonly TamedBeastObserver observer;

    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">The category's wire key and its user-facing copy.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="observer">The bestiary capture this collector reads from.</param>
    public TamedBeastCollector(CategoryInfo info, IFramework framework, TamedBeastObserver observer)
    {
        this.info = info;
        this.framework = framework;
        this.observer = observer;
    }

    /// <inheritdoc/>
    public string CategoryKey => info.Key;

    /// <inheritdoc/>
    public string DisplayName => info.DisplayName;

    /// <inheritdoc/>
    public string Section => info.Section;

    /// <inheritdoc/>
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public string? Details => info.Details;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    // Ignores the context: no server manifest scopes this collection.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        // This read is of managed memory and cannot tear, but asserting here catches a pass that
        // drifted off the framework thread before a later collector reads live game memory.
        GameThread.EnsureFrameworkThread(framework, nameof(TamedBeastCollector));

        // The category's declaration entitles it to the claim; the observer says whether this pass
        // has earned it. Both are required.
        var complete = info.EnumeratesCompleteDomain && observer.IsComplete;
        var (seen, total) = observer.Progress;

        // Held beasts from the window and the record set together.
        var pacts = observer.Pacts();

        // Filled once the player has talked to the Crucible's NPC this session.
        var ranks = observer.Ranks();

        return CollectResult.TamedBeasts(
            pacts,
            complete,
            collectedDetail: complete ? WholeBestiaryRead : null,

            // Names the in-game actions that record this collection; without it, silence reads as a
            // fault. The record set's share is what the list holds beyond the window's beasts.
            partialNote: complete
                ? null
                : DescribeProgress(seen, total, pacts.Count - observer.HeldCount, ranks.Count > 0),
            ranks: ranks);
    }

    /// <summary>The hover copy for a bestiary read in full.</summary>
    /// <remarks>
    /// <see cref="HostPlaceholder.Token"/> is filled with the configured website's address where the
    /// note is drawn. <c>HostPlaceholderTests</c> checks it is a placeholder the fill knows.
    /// </remarks>
    internal const string WholeBestiaryRead =
        "Your whole bestiary has been read, so " + HostPlaceholder.Token +
        " knows which beasts you are missing.";

    /// <summary>How to say what is still unread, in the read-status panel's voice.</summary>
    /// <remarks>
    /// Completes "<c>Tamed beasts: …</c>" and names the action that resolves it (see
    /// <see cref="CollectSkipReasons.Describe"/>). The count shows a player part-way through that
    /// paging is working.
    /// </remarks>
    /// <param name="seen">How many bestiary numbers the window has listed this session.</param>
    /// <param name="total">The window's count of beasts that exist, or null when unread.</param>
    /// <param name="recordedAtCrucible">
    /// How many beasts the record set added beyond the window's.
    /// </param>
    /// <param name="ranksRead">
    /// Whether the current reading of the record set ranks any beast, whatever it added.
    /// </param>
    // TamedBeastProgressTests pins this copy: it is the only thing telling a player how to finish
    // the read, and losing it would look like having nothing to say.
    internal static string DescribeProgress(
        int seen, int? total, int recordedAtCrucible = 0, bool ranksRead = false)
    {
        // Beasts recorded at the Crucible are already going up, so "not read yet" would be false. The
        // bestiary still confirms the whole set and adds beasts the record set cannot.
        if (seen == 0 && recordedAtCrucible > 0)
        {
            return $"{recordedAtCrucible} recorded at the Crucible — page through your Master's " +
                   "Bestiary once to confirm the whole set.";
        }

        if (total is null || seen == 0)
        {
            // Ranks read means the NPC was talked to; naming it again would send the player in a circle.
            // The record set adds only beasts raised past a fresh pact; the rest wait for the bestiary.
            if (ranksRead)
            {
                return "ranks read at the Crucible — open your Master's Bestiary in game to record " +
                       "the beasts you have tamed.";
            }

            return "not read yet — talk to the NPC at the Crucible of the Unbroken's entrance, or " +
                   "open your Master's Bestiary in game, to record the beasts you have tamed.";
        }

        // Every number is seen but the counts disagree. A filtered view tallies only the held beasts
        // it shows, so only another look with no filter can settle the read.
        if (seen >= total)
        {
            return $"{seen} of {total} read — open your Master's Bestiary once more with no " +
                   "filter applied to confirm.";
        }

        // The bestiary remembers its last filter, and paging through a filtered window never
        // finishes the read, which looks like a broken sync.
        return $"{seen} of {total} read — open your Master's Bestiary, clear any filter, and page " +
               "through it to record the rest.";
    }
}
