using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports the current sequence, a byte the game advances as objectives complete, of each quest the
/// server asks about that is in the local player's journal.
/// </summary>
/// <remarks>
/// The server asks about quests with several turn-ins inside one quest, whose progress the
/// completed-quests category cannot show until the whole chain is done. The bytes are opaque
/// per-quest values sent raw for the server's tables to interpret, and a quest outside the journal is
/// absent, which carries no information. Reads only the local player's journal, never the object
/// table (a Dalamud rule).
/// </remarks>
// Unsafe because FFXIVClientStructs' Instance() methods return pointers into live game memory.
public sealed unsafe class QuestSequenceCollector : ICollector
{
    private readonly IFramework framework;

    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy, from the registry, which describes every
    /// category in one file.
    /// </param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public QuestSequenceCollector(CategoryInfo info, IFramework framework)
    {
        this.info = info;
        this.framework = framework;
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

    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(QuestSequenceCollector));

        // Not knowing which quests to look at is a failed read, not an empty one.
        if (context.RemoteConfig is null)
            return CollectResult.Skipped(CollectSkipReasons.NoRemoteConfig);

        // A server without the field (an older server is a supported peer) does not ask for this
        // collection and would strip it anyway: an answer, not a wait.
        if (context.RemoteConfig.QuestSequenceManifest is null)
            return CollectResult.Skipped(CollectSkipReasons.ManifestNotOffered);

        var questManager = QuestManager.Instance();
        if (questManager is null)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        // The bounded funnel caps a hostile backend's manifest. An empty one is a real answer.
        var sequences = new Dictionary<uint, byte>();
        foreach (var questId in context.ManifestFor(CategoryKeys.QuestSequences))
        {
            // Both calls mask the full row id to the low 16 bits the game stores. Without
            // IsQuestAccepted, an absent quest's sequence 0 would look like a quest just picked up.
            if (questManager->IsQuestAccepted(questId))
                sequences[questId] = QuestManager.GetQuestSequence(questId);
        }

        return CollectResult.Sequences(sequences);
    }
}
