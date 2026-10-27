using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Achievements;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports the progress of each achievement the player has opened in game this session.
/// </summary>
/// <remarks>
/// Reads no game state during a pass: <see cref="AchievementProgressObserver"/> takes each reading as
/// the game receives it, and every upload that includes this collection sends the held ones, up to the
/// newest <see cref="Api.AchievementProgressReading.MaxPerUpload"/>, until the website confirms storing
/// them. With none held the pass is a <see cref="CollectSkipReasons.NothingSeenYet"/> skip (see
/// <see cref="CollectSkipReasons.IsDeliberate"/>).
/// </remarks>
public sealed class AchievementProgressCollector : ICollector
{
    /// <summary>The read-status chip's hover.</summary>
    private const string ReadOnOpen =
        "Read from the achievements you open in your Achievements window this session.";

    private readonly CategoryInfo info;
    private readonly IFramework framework;
    private readonly AchievementProgressObserver observer;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">The category's wire key and its user-facing copy.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="observer">The capture this collector reads from.</param>
    public AchievementProgressCollector(
        CategoryInfo info, IFramework framework, AchievementProgressObserver observer)
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
        // The observer's ledger is written on the framework thread, so it is read there too.
        GameThread.EnsureFrameworkThread(framework, nameof(AchievementProgressCollector));

        return CollectResult.AchievementProgress(
            observer.Readings(),
            collectedDetail: ReadOnOpen,
            confirmedBy: info.StoredResponseKey,
            forgetSent: observer.DropStored);
    }
}
