using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports the beasts the character is known to have tamed.
/// </summary>
/// <remarks>
/// <para>
/// This collection reads no game state during a pass. Its source is
/// <see cref="TamedBeastObserver"/>, which reads the Master's Bestiary whenever the player opens
/// it; by the time a pass runs, the facts are already sitting in memory. The pass exists to hand
/// them to the payload, and to keep doing so until an upload succeeds.
/// </para>
/// <para>
/// There is no in-memory sweep to run — the structure holding the bestiary's flags is unmapped —
/// so the window the player opens is where the character's collection is legible. What this
/// reports is a subset until they have paged through it, and the whole of it once the counts the
/// window reports agree, which is when the completeness claim becomes available.
/// </para>
/// </remarks>
public sealed class TamedBeastCollector : ICollector
{
    private readonly IFramework framework;
    private readonly TamedBeastObserver observer;

    // How this collection names and describes itself to the user.
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

    // Nothing from the context: what the window said is self-contained, scoped by no server
    // manifest.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        // Every collector asserts this, and this one keeps the habit even though its own read is of
        // plain managed memory: the ledger guards itself, so nothing here would tear. What the
        // assertion buys is that a pass which drifted off the framework thread is caught at the
        // collection that noticed rather than at whichever collector later reads live game memory.
        GameThread.EnsureFrameworkThread(framework, nameof(TamedBeastCollector));

        // Both halves are required, and they mean different things. The category's declaration is
        // its standing entitlement to make the claim at all — the same switch every other
        // collection has, and turning it off here would silence the claim outright. The observer's
        // answer is whether this particular pass has earned it.
        var complete = info.EnumeratesCompleteDomain && observer.IsComplete;
        var (seen, total) = observer.Progress;

        return CollectResult.TamedBeasts(
            observer.Snapshot(),
            complete,

            // Only once the whole bestiary has been read is there a reassuring thing to say; before
            // that the partial note below is carrying the message instead.
            collectedDetail: complete ? WholeBestiaryRead : null,

            // Names the one in-game action that finishes the read, the way a skip hint does. Until
            // the bestiary has been opened this collection has nothing to report at all, and a
            // player who never learns that reads the silence as a fault.
            partialNote: complete
                ? null
                : DescribeProgress(seen, total));
    }

    /// <summary>The hover copy for a bestiary read in full.</summary>
    /// <remarks>
    /// <see cref="HostPlaceholder.Token"/> stands for the configured website's address, filled in where
    /// the note is drawn. Internal so a test can check the placeholder is one the fill knows.
    /// </remarks>
    // A `const` built from another `const` with `+` is still fixed when the code compiles.
    internal const string WholeBestiaryRead =
        "Your whole bestiary has been read, so " + HostPlaceholder.Token +
        " knows which beasts you are missing.";

    /// <summary>How to say what is still unread, in the read-status panel's voice.</summary>
    /// <remarks>
    /// Drawn after the category's display name, so it completes "<c>Tamed beasts: …</c>" and has to
    /// name the action that resolves it. The count is included once there is one, because "17 of
    /// 50" tells a player part-way through that turning the page is working.
    /// </remarks>
    // Internal rather than private so the test that pins this copy can reach it: the wording is the
    // only thing telling a player how to finish the read, and a pass that silently stopped saying
    // it would look exactly like one that had nothing to say.
    internal static string DescribeProgress(int seen, int? total)
    {
        if (total is null || seen == 0)
        {
            return "not read yet — open your Master's Bestiary in game to record the beasts you " +
                   "have tamed.";
        }

        // Every number accounted for, yet the read is not settled — the counts the bestiary
        // reported have not lined up. Telling the player to page through for "the rest" would name
        // work that does not exist, so this asks for the one thing that can still change: another
        // look, which re-reads the tally.
        //
        // Clearing the filter is the advice because a filtered view is the cause the player can do
        // anything about: its tally counts only the held beasts the filter shows, so it never
        // reaches the true held count and only an unfiltered look settles it. Reopening a
        // still-filtered window would leave them repeating the one step that cannot work.
        if (seen >= total)
        {
            return $"{seen} of {total} read — open your Master's Bestiary once more with no " +
                   "filter applied to confirm.";
        }

        // The filter is named because the bestiary remembers the last one the player set, so a
        // window opened days later can still be showing a handful of beasts. Paging through that
        // records only what the filter shows and never finishes the read, which looks like a broken
        // sync rather than like a filter left on.
        return $"{seen} of {total} read — open your Master's Bestiary, clear any filter, and page " +
               "through it to record the rest.";
    }
}
