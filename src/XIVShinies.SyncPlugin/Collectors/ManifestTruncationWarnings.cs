using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides which truncation warnings to log this pass, remembering what it has already said.
/// </summary>
/// <remarks>
/// <para>
/// Lives outside the orchestrator because it is a decision, not plumbing: given a set of clipped
/// categories, the current manifest version, and what was said before, which lines should be
/// logged. That makes it pure — no Dalamud services, no logger, no clock — and therefore
/// unit-testable, which the orchestrator itself is not.
/// </para>
/// <para>
/// Not thread-safe, and does not need to be: the collection pass that drives it is framework
/// thread only, so there is a single writer and no lock is required.
/// </para>
/// </remarks>
public sealed class ManifestTruncationWarnings
{
    /// <summary>
    /// Per category, the manifest version its warning was last logged for. A category present
    /// with a null value was warned about under a config that carried no version.
    /// </summary>
    private readonly Dictionary<string, string?> warnedVersions = [];

    /// <summary>Categories whose warning does not track a version, and so is logged once.</summary>
    private readonly HashSet<string> warnedOnce = [];

    /// <summary>
    /// The lines to log for this pass, in catalog order. Empty when every clipped category has
    /// already been reported, which is the usual answer — the sweep cadence would otherwise
    /// repeat the same warning every pass.
    /// </summary>
    /// <param name="truncatedCategoryKeys">
    /// The categories whose manifest the server sent over the ceiling (see
    /// <see cref="CollectContext.TruncatedManifests"/>).
    /// </param>
    /// <param name="manifestVersion">The config's manifest version, or null when it carries none.</param>
    public IReadOnlyList<string> LinesFor(
        IReadOnlySet<string> truncatedCategoryKeys, string? manifestVersion)
    {
        var lines = new List<string>();

        // Walked in catalog order rather than in the set's, because a HashSet does not promise
        // one — this is what makes the log deterministic when two manifests clip together. A
        // clipped key the catalog does not know has no wording to log, so it is passed over.
        foreach (var descriptor in ManifestCatalog.All)
        {
            if (!truncatedCategoryKeys.Contains(descriptor.CategoryKey))
                continue;

            if (!ShouldWarn(descriptor, manifestVersion))
                continue;

            Remember(descriptor, manifestVersion);
            lines.Add(ManifestCatalog.TruncationWarning(descriptor.CategoryKey)!);
        }

        return lines;
    }

    /// <summary>True when this clip has not yet been reported under the terms that identify it.</summary>
    private bool ShouldWarn(ManifestDescriptor descriptor, string? manifestVersion)
    {
        // A manifest the version hash does not cover cannot tell a new clip from the one already
        // reported, so it gets one line for the session rather than a promise it cannot keep.
        if (!descriptor.CoveredByManifestVersion)
            return !warnedOnce.Contains(descriptor.CategoryKey);

        return !warnedVersions.TryGetValue(descriptor.CategoryKey, out var warnedFor)
            || warnedFor != manifestVersion;
    }

    /// <summary>Records that this category's warning has now been logged.</summary>
    private void Remember(ManifestDescriptor descriptor, string? manifestVersion)
    {
        if (descriptor.CoveredByManifestVersion)
            warnedVersions[descriptor.CategoryKey] = manifestVersion;
        else
            warnedOnce.Add(descriptor.CategoryKey);
    }
}
