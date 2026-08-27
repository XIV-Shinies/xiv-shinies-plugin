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
    /// The facts, keyed by category. Goes straight into the sync payload's <c>collections</c>
    /// object. A category that could not be read is simply absent.
    /// </summary>
    public required Dictionary<string, JsonNode> Collections { get; init; }

    /// <summary>
    /// Why each omitted category was omitted, keyed by category. The settings UI turns these into
    /// hints (for example "open your Achievements window once"); nothing else interprets them.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Skipped { get; init; }

    /// <summary>
    /// Each collected category's partial-read phrase (see <see cref="CollectResult.PartialNote"/>),
    /// keyed by category. A category collected in full has no entry. The settings UI prints these
    /// beside the category's name; nothing else interprets them.
    /// </summary>
    // Not `required`, like the fields below: an empty dictionary is the honest default for a
    // test snapshot with nothing partially read.
    public IReadOnlyDictionary<string, string> PartialNotes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Each collected category's healthy-chip hover copy (see
    /// <see cref="CollectResult.CollectedDetail"/>), keyed by category. The settings UI shows
    /// these on hover; nothing else interprets them.
    /// </summary>
    // Not `required`, like its siblings: an empty dictionary is the honest default for a test
    // snapshot whose chips need no hover.
    public IReadOnlyDictionary<string, string> CollectedDetails { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Each collected category's own count of what it is about (see
    /// <see cref="CollectResult.FactCount"/>), keyed by category. A category that left the count
    /// to the facts' shape has no entry. The upload log prints these; nothing else interprets them.
    /// </summary>
    // Not `required`, like its siblings: an empty dictionary is the honest default for a test
    // snapshot where every category is counted from its shape.
    public IReadOnlyDictionary<string, int> FactCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// The categories that read none of what they are about this pass, though they still had
    /// something to send (see <see cref="CollectResult.NothingReadThisPass"/>). The upload log
    /// names these without a count.
    /// </summary>
    /// <remarks>
    /// Named for what it holds, to keep it distinct from
    /// <see cref="Sync.UploadLogEntry.UnreadableCategoryKeys"/>, which lists categories that were
    /// never sent at all. A category here <b>was</b> sent — it simply carried no reading of its
    /// own collection.
    /// </remarks>
    // Not `required`, like the sets below: an empty set is the honest default for a test snapshot
    // where every category read something.
    public IReadOnlySet<string> NothingReadKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// How long each collector took, keyed by category. Only collectors that actually ran appear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collection happens on the game's framework thread, which has roughly 16ms to produce a frame.
    /// Every collector spends part of that budget, and the plugin is expected to grow more of them —
    /// so the cost has to be <b>visible</b>, not assumed. The orchestrator logs these, which means a
    /// contributor who adds an expensive collector sees its price in <c>/xllog</c> on the first sweep
    /// rather than discovering it as a stutter report.
    /// </para>
    /// <para>
    /// Measured here rather than logged here on purpose: this class holds no Dalamud services, which
    /// is what keeps it unit-testable. It reports the numbers; the caller decides what to do with them.
    /// </para>
    /// </remarks>
    // Not `required`: a snapshot assembled in a test need not care about timings, and an empty
    // dictionary is the honest default for "nothing was measured".
    public IReadOnlyDictionary<string, TimeSpan> Durations { get; init; } =
        new Dictionary<string, TimeSpan>();

    /// <summary>
    /// Per-source scan status (inventory live, saddlebag cached, retainers unscanned, etc.),
    /// merged from every collector that reported source notes this pass.
    /// </summary>
    /// <remarks>
    /// A dictionary (never null) because empty is meaningful: "no collector reported any source
    /// status this pass" is a real answer, and callers can iterate it unconditionally. The merge
    /// rules live at the merge site in <see cref="CollectorRunner.Run"/>.
    /// </remarks>
    // Not `required`: a snapshot assembled in a test need not care about source notes, and an empty
    // dictionary is the honest default for "nothing was reported".
    public IReadOnlyDictionary<string, ItemSourceStatus> SourceNotes { get; init; } =
        new Dictionary<string, ItemSourceStatus>();

    /// <summary>
    /// The categories whose collectors declare <see cref="ICollector.UsesItemManifest"/> — their
    /// scope is the server's item manifest rather than fixed at compile time.
    /// </summary>
    /// <remarks>
    /// The upload log copies this onto each category it summarizes, where it decides the
    /// category's change signal — <see cref="Sync.UploadLogCategory"/> holds the full reasoning.
    /// Carried as collector self-description (the same idea as
    /// <see cref="ICollector.DisplayName"/>) so no consumer ever compares keys against a
    /// hardcoded name.
    /// </remarks>
    // Not `required`, like the fields above: an empty set is the honest default for a test
    // snapshot that has no manifest-driven categories.
    public IReadOnlySet<string> ManifestDrivenKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The categories whose facts this pass read as a <b>complete</b> enumeration — the
    /// collector's own claim, recorded from <see cref="CollectResult.CompleteEnumeration"/>.
    /// </summary>
    /// <remarks>
    /// The runner records a key only for a category it collected facts for, so a skipped category
    /// never appears here. The payload builder still intersects against the carried categories
    /// before turning these into <c>collectionScopes</c> declarations, since nothing on this
    /// record enforces that. A set of keys, not a flag per known category, so a future collector's
    /// claim flows through without anyone here learning its name.
    /// </remarks>
    // Not `required`, like the fields above: an empty set is the honest default for a test
    // snapshot that makes no completeness claims.
    public IReadOnlySet<string> CompleteKeys { get; init; } = new HashSet<string>();

    /// <summary>
    /// The categories whose manifest the server sent over the client's ceiling (see
    /// <see cref="CollectContext.TruncatedManifests"/>), so the tail is not being reported.
    /// </summary>
    /// <remarks>
    /// Facts about the config, not about this pass's scan: a key holds even when the pass ran no
    /// scan for that category at all. Carried here because the orchestrator, which holds the
    /// logger, sees only the snapshot — the runner reports the fact, the caller decides what to
    /// do with it. Keyed like <see cref="CompleteKeys"/> so a manifest added later needs no new
    /// member here.
    /// </remarks>
    public IReadOnlySet<string> TruncatedManifests { get; init; } = new HashSet<string>();
}

/// <summary>
/// Runs every registered collector and assembles the snapshot.
/// </summary>
/// <remarks>
/// Contains <b>no category names</b>. It gates each collector by its own key, asks it for facts,
/// and files the answer under that same key. Adding a collection therefore needs no change here.
/// </remarks>
public static class CollectorRunner
{
    /// <summary>Collects from every enabled collector.</summary>
    /// <param name="collectors">
    /// The registered collectors. Passed in rather than constructed here: the real ones hold
    /// Dalamud services, and building them inside this class would make it impossible to test.
    /// </param>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if not fetched yet.</param>
    /// <remarks>Reads game state through the collectors, so call this on the framework thread.</remarks>
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

        // Built once and shared: every collector sees the same view of the world for this pass.
        // EnabledItemGroupKeys carries the user's per-group opt-ins so the item collector scans only
        // the groups they consented to (the item manifest unions the enabled groups). Taken
        // as a snapshot, because the user can tick a group's checkbox on the UI thread while this pass
        // is running and the live list would not survive being read and written at once.
        var context = new CollectContext
        {
            RemoteConfig = remoteConfig,
            EnabledItemGroupKeys = settings.SnapshotEnabledItemGroupKeys(),
        };

        foreach (var collector in collectors)
        {
            var key = collector.CategoryKey;

            // Self-description, recorded before any gating: whether a category is manifest-driven
            // is a fact about its collector, not about whether this pass collected it.
            if (collector.UsesItemManifest)
                manifestDrivenKeys.Add(key);

            // Ask before reading: a disabled category must cost nothing, not even a game lookup.
            if (!CollectorGate.IsEnabled(key, settings, remoteConfig))
            {
                skipped[key] = CollectSkipReasons.Disabled;
                continue;
            }

            // A raw timestamp rather than a Stopwatch object: no allocation, and this runs inside the
            // game's per-frame loop. `Stopwatch.GetElapsedTime` converts the pair into a TimeSpan.
            var startedAt = Stopwatch.GetTimestamp();

            CollectResult result;
            try
            {
                result = collector.Collect(context);
            }
            // A deliberately broad catch: one misbehaving collector must not abort the whole
            // snapshot. Its category is simply omitted, which the server reads as "not read this
            // time" — never as "cleared".
            //
            // Note what this does NOT protect against. It catches ordinary managed exceptions only.
            // A corrupted-state exception — such as an AccessViolationException from a bad pointer
            // read inside a collector that walks game memory — is not delivered to a managed catch
            // in .NET, and terminates the process regardless. The guard against that is the
            // framework-thread check inside those collectors, not this try/catch.
            catch (Exception)
            {
                // Timed even on the failure path: a collector that is slow *and* throws is exactly
                // the one worth seeing in the log.
                durations[key] = Stopwatch.GetElapsedTime(startedAt);
                skipped[key] = CollectSkipReasons.CollectorError;
                continue;
            }

            durations[key] = Stopwatch.GetElapsedTime(startedAt);

            if (result.WasCollected)
            {
                // Note this includes an EMPTY list, which is a real fact ("I looked, there was
                // nothing"), unlike a skip.
                collections[key] = result.Facts!;

                // The collector's own completeness claim, recorded under its own key — the payload
                // builder turns it into this category's `collectionScopes` declaration.
                if (result.CompleteEnumeration)
                    completeKeys.Add(key);

                // The collector's own partial-read phrase, filed the same way: the settings UI
                // prints it beside the category's name without knowing which collection it is.
                if (result.PartialNote is { } partialNote)
                    partialNotes[key] = partialNote;

                // And its healthy-chip hover copy, filed under the collector's own key by the
                // same rule.
                if (result.CollectedDetail is { } collectedDetail)
                    collectedDetails[key] = collectedDetail;

                // And "I read none of what I am about this pass" — filed as a bare key, since there
                // is no number to file. The upload log names such a category without a count.
                if (result.NothingReadThisPass)
                    nothingReadKeys.Add(key);

                // And its own count of what it is about, when the facts' shape would not give the
                // number a reader expects. Same rule again: the collector says, nothing here reads it.
                if (result.FactCount is { } factCount)
                    factCounts[key] = factCount;

                // Merge source notes from this collector. Source-keyed: if two collectors both report
                // on the same source (e.g., both describe inventory), the last one wins because they
                // describe the same physical storage location. The snapshot iteration order means
                // "last" is the order collectors were registered.
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
