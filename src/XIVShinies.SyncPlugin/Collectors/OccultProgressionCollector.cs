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
/// Crescent instance. Outside one, a held knowledge sighting is sent alone with an empty jobs
/// map; with no sighting either, this collector skips with
/// <see cref="CollectSkipReasons.NotInOccultInstance"/>. The values are character-wide (both
/// zones report the same array) and monotonic: job levels and EXP can never decrease.
/// </para>
/// <para>
/// A knowledge-only pass clears the category's skip reason (any collected pass does — see
/// <c>Sync.CollectionStatusMemory.MergeSkipReasons</c>) but carries a partial note in its
/// place while the login session has not read the jobs, so the settings read-status panel
/// says the job levels still need an instance visit exactly when they do — a session that has
/// already read the jobs gets the healthy chip instead. The reverse gap is quieter: a pass
/// that read the jobs without a knowledge sighting puts where the knowledge level comes from
/// on the chip's hover, never a visible line, because the sighting clears at every logout and
/// a line would nag every session.
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
    // The empty jobs map handed to a knowledge-only payload. One shared instance is safe
    // because nothing ever mutates it: the field's IReadOnlyDictionary type is a read-only
    // view, and the only Dictionary reference lives here, so no caller can add to it.
    private static readonly IReadOnlyDictionary<byte, OccultJobProgress> NoJobs =
        new Dictionary<byte, OccultJobProgress>();

    private readonly IFramework framework;
    private readonly KnowledgeObserver knowledgeObserver;

    // The observer's session generation at the moment the in-instance path last read the jobs,
    // or null when no session has. A knowledge-only pass compares it against the current
    // generation to decide whether the jobs half is genuinely unread THIS login session (the
    // partial note is owed) or already read by an earlier in-instance pass (it is not).
    // The generation, rather than a plain bool, is what keeps the answer honest across a relog:
    // a new session moves the fence, so a stale "jobs read" can never suppress a note the new
    // character's sighting is owed. Written and read on the framework thread only.
    private int? jobsReadGeneration;

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
    public string Section => info.Section;

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

        // Read once for the whole pass: both branches below decide what to send by it.
        var sighting = knowledgeObserver.Current;

        // The static accessor resolves only while the occult director exists — that is, inside
        // an instance.
        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            // Outside an instance the director's state — this collector's one source for job
            // data — is gone, but a knowledge sighting may be waiting: the review window lives
            // in Phantom Village, outside the instances, so the natural "leave, then check
            // your level" flow captures one exactly where this collector cannot read the jobs.
            // The contract accepts an empty jobs map, so the sighting reaches the server on
            // the very next pass, wherever the character is standing.
            // The partial note keeps the read-status panel honest about the half this pass
            // could not read.
            var jobsReadThisSession = jobsReadGeneration == knowledgeObserver.SessionGeneration;

            return sighting is not null
                ? CollectResult.Progression(
                    NoJobs,
                    sighting,
                    partialNote: jobsReadThisSession
                        ? null
                        : "knowledge level read — job levels sync during your next " +
                          "Occult Crescent visit.")
                : CollectResult.Skipped(CollectSkipReasons.NotInOccultInstance);
        }

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

        // Fences the partial note above to this login session: the jobs have been read into
        // this pass's facts, so a later knowledge-only pass in the same session has nothing
        // partial to report.
        jobsReadGeneration = knowledgeObserver.SessionGeneration;

        // When no sighting is held, the chip carries a hover saying where the knowledge level
        // comes from — a visible line is not owed for it (see this class's remarks), and the
        // server's Lodestone sync covers the value anyway.
        return CollectResult.Progression(
            jobs,
            sighting,
            collectedDetail: sighting is null
                ? "Your knowledge level syncs when you open the review window at Jeffroy in " +
                  "Phantom Village."
                : null);
    }
}
