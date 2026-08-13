using System.Collections.Generic;
using Dalamud.Plugin.Services;
// The occult instance director, whose state block carries per-job levels and EXP.
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the local player's phantom job progress — all 24 support jobs' levels and EXP —
/// plus the knowledge-level sighting the <see cref="KnowledgeObserver"/> may hold.
/// </summary>
/// <remarks>
/// <para>
/// Job progress lives in the occult instance director's state block
/// (<c>OccultCrescentState</c>), which exists only while the character is inside an Occult
/// Crescent instance — outside one this collector skips with
/// <see cref="CollectSkipReasons.NotInOccultInstance"/>. The values are character-wide (both
/// zones report the same array) and monotonic: job levels and EXP can never decrease.
/// </para>
/// <para>
/// The knowledge sighting rides along when one exists; see <see cref="KnowledgeObservation"/>
/// for why it carries an observation time.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and
/// is verified by in-game QA. Everything read describes the local player's own progression.
/// </para>
/// </remarks>
// `unsafe` because the director's state is reached through a raw pointer — C#'s references
// and bounds checks do not apply, so the null gate below is what stands between a read and a
// crash.
public sealed unsafe class OccultProgressionCollector : ICollector
{
    private readonly IFramework framework;
    private readonly KnowledgeObserver knowledgeObserver;

    // How this collection names and describes itself to the user.
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
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public string? Details => info.Details;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    // This collector needs nothing from the context: the director's state is self-contained,
    // scoped by no server manifest.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(OccultProgressionCollector));

        // The static accessor resolves only while the occult director exists — that is, inside
        // an instance.
        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
            return CollectResult.Skipped(CollectSkipReasons.NotInOccultInstance);

        // Both arrays are fixed at 24 entries — one per MKDSupportJob row, index == row id,
        // and row 0 (Freelancer) is a real job. They are fixed-size arrays embedded in the
        // C++ struct (not managed arrays), indexed through the state pointer; Length comes
        // from the struct definition, so the loop can never leave the block it reads.
        var jobs = new Dictionary<byte, OccultJobProgress>(state->SupportJobLevels.Length);
        for (var i = 0; i < state->SupportJobLevels.Length; i++)
        {
            jobs[(byte)i] = new OccultJobProgress
            {
                Exp = state->SupportJobExperience[i],
                Level = state->SupportJobLevels[i],
            };
        }

        return CollectResult.Progression(jobs, knowledgeObserver.Current);
    }
}
