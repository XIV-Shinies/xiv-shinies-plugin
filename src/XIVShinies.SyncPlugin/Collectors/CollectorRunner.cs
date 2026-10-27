using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One pass over the registered collectors: what was read, and what was not.
/// </summary>
public sealed record CollectionSnapshot
{
    /// <summary>
    /// The facts, keyed by category, for the sync payload's <c>collections</c> object. A category
    /// that could not be read is absent.
    /// </summary>
    public required Dictionary<string, JsonNode> Collections { get; init; }

    /// <summary>
    /// Why each omitted category was omitted, keyed by category. The settings UI turns these into
    /// hints, and the upload log uses them to tell a miss from a skip that is no failure (see
    /// <see cref="CollectSkipReasons.IsDeliberate"/>).
    /// </summary>
    public required IReadOnlyDictionary<string, string> Skipped { get; init; }

    /// <summary>
    /// Each collected category's partial-read phrase (see <see cref="CollectResult.PartialNote"/>),
    /// keyed by category; no entry when read in full. Only the settings UI reads these.
    /// </summary>
    public IReadOnlyDictionary<string, string> PartialNotes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Each collected category's healthy-chip hover copy (see
    /// <see cref="CollectResult.CollectedDetail"/>), keyed by category. Only the settings UI reads
    /// these.
    /// </summary>
    public IReadOnlyDictionary<string, string> CollectedDetails { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Each collected category's own count (see <see cref="CollectResult.FactCount"/>), keyed by
    /// category; no entry when the facts' shape gives the count. Only the upload log reads these.
    /// </summary>
    public IReadOnlyDictionary<string, int> FactCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// The categories that read none of their collection this pass but still had something to send
    /// (see <see cref="CollectResult.NothingReadThisPass"/>). The upload log names these without a
    /// count.
    /// </summary>
    /// <remarks>
    /// A category here was sent, unlike one in <see cref="Sync.UploadLogEntry.UnreadableCategoryKeys"/>.
    /// </remarks>
    public IReadOnlySet<string> NothingReadKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// How long each collector that ran took, keyed by category.
    /// </summary>
    /// <remarks>The orchestrator logs them; see <see cref="CollectionCost"/> for why they matter.</remarks>
    public IReadOnlyDictionary<string, TimeSpan> Durations { get; init; } =
        new Dictionary<string, TimeSpan>();

    /// <summary>
    /// Per-source scan status (inventory live, saddlebag cached, retainers unscanned), merged from
    /// every collector that reported any this pass.
    /// </summary>
    /// <remarks>
    /// Never null: empty means no collector reported a source this pass. The merge rule is at the
    /// merge site in <see cref="CollectorRunner.Run"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, ItemSourceStatus> SourceNotes { get; init; } =
        new Dictionary<string, ItemSourceStatus>();

    /// <summary>
    /// The categories whose collectors declare <see cref="ICollector.UsesItemManifest"/>.
    /// </summary>
    /// <remarks>
    /// The upload log copies this onto each category, where it decides the change signal (see
    /// <see cref="Sync.UploadLogCategory"/>). It is collector self-description, so no consumer
    /// compares keys against a name.
    /// </remarks>
    public IReadOnlySet<string> ManifestDrivenKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The categories whose facts this pass declared a <b>complete</b> enumeration (see
    /// <see cref="CollectResult.CompleteEnumeration"/>).
    /// </summary>
    /// <remarks>
    /// Only collected categories appear. The payload builder still intersects these with the carried
    /// categories before writing <c>collectionScopes</c>, since nothing here enforces it.
    /// </remarks>
    public IReadOnlySet<string> CompleteKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The categories whose manifest the server sent over the client's ceiling (see
    /// <see cref="CollectContext.TruncatedManifests"/>), so the tail is not being reported.
    /// </summary>
    /// <remarks>
    /// A fact about the config, not this pass's scan: a key holds even when the pass scanned nothing
    /// for that category. Carried here because the orchestrator, which holds the logger, sees only
    /// the snapshot.
    /// </remarks>
    public IReadOnlySet<string> TruncatedManifests { get; init; } = new HashSet<string>();
}

/// <summary>
/// Runs every registered collector and assembles the snapshot.
/// </summary>
/// <remarks>
/// Names no category: it gates each collector by its own key and files the answer under that key,
/// so adding a collection needs no change here.
/// </remarks>
public static class CollectorRunner
{
    /// <summary>Collects from every enabled collector.</summary>
    /// <param name="collectors">The registered collectors (see <see cref="CollectorRegistry"/>).</param>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if not fetched yet.</param>
    /// <remarks>Call on the framework thread: the collectors read game state.</remarks>
    public static CollectionSnapshot Run(
        IEnumerable<ICollector> collectors, PluginSettings settings, ConfigResponse? remoteConfig)
    {
        var collections = new Dictionary<string, JsonNode>();
        var skipped = new Dictionary<string, string>();
        var partialNotes = new Dictionary<string, string>();
        var collectedDetails = new Dictionary<string, string>();
        var factCounts = new Dictionary<string, int>();
        var nothingReadKeys = new HashSet<string>();
        var durations = new Dictionary<string, TimeSpan>();
        var sourceNotes = new Dictionary<string, ItemSourceStatus>();
        var manifestDrivenKeys = new HashSet<string>();
        var completeKeys = new HashSet<string>();

        // One context per pass, so every collector sees the same world. The group opt-ins are a copy
        // (see PluginSettings.SnapshotEnabledItemGroupKeys).
        var context = new CollectContext
        {
            RemoteConfig = remoteConfig,
            EnabledItemGroupKeys = settings.SnapshotEnabledItemGroupKeys(),
        };

        foreach (var collector in collectors)
        {
            var key = collector.CategoryKey;

            // Recorded before gating: it describes the collector, not this pass.
            if (collector.UsesItemManifest)
                manifestDrivenKeys.Add(key);

            // Ask before reading: a disabled category must cost nothing, not even a game lookup.
            if (!CollectorGate.IsEnabled(key, settings, remoteConfig))
            {
                skipped[key] = CollectSkipReasons.Disabled;
                continue;
            }

            // A raw timestamp, not a Stopwatch object: no allocation inside the game's per-frame loop.
            var startedAt = Stopwatch.GetTimestamp();

            CollectResult result;
            try
            {
                result = collector.Collect(context);
            }
            // Broad on purpose: one failing collector must not abort the pass, and the server reads its
            // omission as "not read this time". A bad memory read still ends the process (see GameThread).
            catch (Exception)
            {
                // Timed on failure too: a slow collector that throws is the one worth seeing.
                durations[key] = Stopwatch.GetElapsedTime(startedAt);
                skipped[key] = CollectSkipReasons.CollectorError;
                continue;
            }

            durations[key] = Stopwatch.GetElapsedTime(startedAt);

            if (result.WasCollected)
            {
                // Includes an empty list, a real fact ("I looked, there was nothing"), unlike a skip.
                collections[key] = result.Facts!;

                // The payload builder turns this into the category's `collectionScopes` entry.
                if (result.CompleteEnumeration)
                    completeKeys.Add(key);

                // The rest is self-description, filed under the collector's own key for the settings
                // UI and upload log; nothing here interprets it.
                if (result.PartialNote is { } partialNote)
                    partialNotes[key] = partialNote;

                if (result.CollectedDetail is { } collectedDetail)
                    collectedDetails[key] = collectedDetail;

                if (result.NothingReadThisPass)
                    nothingReadKeys.Add(key);

                if (result.FactCount is { } factCount)
                    factCounts[key] = factCount;

                // Source-keyed: two collectors reporting on one storage location describe the same
                // place, so the last in registration order wins.
                if (result.SourceNotes is not null)
                {
                    foreach (var (sourceKey, status) in result.SourceNotes)
                    {
                        sourceNotes[sourceKey] = status;
                    }
                }
            }
            else
            {
                skipped[key] = result.SkipReason ?? CollectSkipReasons.CollectorError;
            }
        }

        return new CollectionSnapshot
        {
            Collections = collections,
            Skipped = skipped,
            PartialNotes = partialNotes,
            CollectedDetails = collectedDetails,
            FactCounts = factCounts,
            NothingReadKeys = nothingReadKeys,
            Durations = durations,
            SourceNotes = sourceNotes,
            ManifestDrivenKeys = manifestDrivenKeys,
            CompleteKeys = completeKeys,
            TruncatedManifests = context.TruncatedManifests,
        };
    }
}
