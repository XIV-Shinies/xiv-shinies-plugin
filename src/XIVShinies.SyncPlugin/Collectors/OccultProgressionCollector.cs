using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the local player's level and EXP for all 24 phantom jobs, plus any knowledge-level
/// sighting <see cref="KnowledgeObserver"/> holds (see <see cref="KnowledgeObservation"/>).
/// </summary>
/// <remarks>
/// Job progress lives in the occult director's state block (<c>OccultCrescentState</c>), which exists
/// only inside an Occult Crescent instance; it is character-wide, since both zones report the same
/// array, and never decreases. Outside an instance a held sighting goes alone with an empty jobs map,
/// and with none the pass skips with <see cref="CollectSkipReasons.NotInOccultInstance"/>. A pass
/// that read the jobs without a sighting puts where the knowledge level comes from on the chip's
/// hover, never a visible line, because the sighting clears at every logout and a line would nag
/// every session.
/// </remarks>
// Unsafe because the director's state is a raw pointer; the null check below guards every read.
public sealed unsafe class OccultProgressionCollector : ICollector
{
    // Shared by every knowledge-only payload. Safe because no mutable reference escapes this field.
    private static readonly IReadOnlyDictionary<byte, OccultJobProgress> NoJobs =
        new Dictionary<byte, OccultJobProgress>();

    private readonly IFramework framework;
    private readonly KnowledgeObserver knowledgeObserver;

    // The observer's session generation when a pass last read the jobs, or null. A relog moves the
    // generation, so the jobs count as unread again in the new session. Framework thread only.
    private int? jobsReadGeneration;

    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">The category's wire key and its user-facing copy.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="knowledgeObserver">The passive knowledge-level capture this collector reads from.</param>
    public OccultProgressionCollector(
        CategoryInfo info, IFramework framework, KnowledgeObserver knowledgeObserver)
    {
        this.info = info;
        this.framework = framework;
        this.knowledgeObserver = knowledgeObserver;
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
        GameThread.EnsureFrameworkThread(framework, nameof(OccultProgressionCollector));

        // Read once, so both branches decide by the same sighting.
        var sighting = knowledgeObserver.Current;

        // Null outside an instance, where the occult director does not exist.
        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            // The review window is in Phantom Village, outside the instances, so a sighting often
            // waits here. The contract accepts it with an empty jobs map.
            var jobsReadThisSession = jobsReadGeneration == knowledgeObserver.SessionGeneration;

            // Sending clears the skip hint (Sync.CollectionStatusMemory.MergeSkipReasons), so a
            // partial note says the jobs are unread until this login session has read them.
            return sighting is not null
                ? CollectResult.Progression(
                    NoJobs,
                    sighting,
                    partialNote: jobsReadThisSession
                        ? null
                        : "knowledge level read — job levels are read by the automatic sync or " +
                          "Sync now while you are inside the Occult Crescent.")
                : CollectResult.Skipped(CollectSkipReasons.NotInOccultInstance);
        }

        // One entry per MKDSupportJob row, indexed by row id; row 0 (Freelancer) is a real job.
        // Length comes from the struct definition, so the loop cannot leave the block.
        var jobs = new Dictionary<byte, OccultJobProgress>(state->SupportJobLevels.Length);
        for (var i = 0; i < state->SupportJobLevels.Length; i++)
        {
            jobs[(byte)i] = new OccultJobProgress
            {
                Exp = state->SupportJobExperience[i],
                Level = state->SupportJobLevels[i],
            };
        }

        // A later knowledge-only pass this session has nothing partial to report.
        jobsReadGeneration = knowledgeObserver.SessionGeneration;

        // A hover, not a visible line (see the class remarks); the server's Lodestone sync also
        // covers the value.
        return CollectResult.Progression(
            jobs,
            sighting,
            collectedDetail: sighting is null
                ? "Your knowledge level is read when you open the review window at Jeffroy in " +
                  "Phantom Village, and sent by the next automatic sync or Sync now. Logging out " +
                  "first drops it."
                : null);
    }
}
