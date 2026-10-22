using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides which truncation warnings to log this pass, remembering what it has already said.
/// </summary>
/// <remarks>
/// Holds no Dalamud services, logger or clock: it returns the lines and the orchestrator logs them.
/// Not thread-safe; only the framework-thread collection pass calls it.
/// </remarks>
public sealed class ManifestTruncationWarnings
{
    /// <summary>
    /// Per category, the manifest version its warning was last logged for. A null value means it was
    /// logged under a config with no version.
    /// </summary>
    private readonly Dictionary<string, string?> warnedVersions = [];

    /// <summary>Categories whose warning tracks no version, and so is logged once.</summary>
    private readonly HashSet<string> warnedOnce = [];

    /// <summary>
    /// The lines to log for this pass, in catalog order. Usually empty: each clip is reported once,
    /// not on every sweep.
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

        // Catalog order, because a HashSet promises none; it keeps the log stable when two manifests
        // clip together. A key the catalog does not know has no wording, so it is skipped.
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
        // The version hash does not cover this manifest, so a new clip looks like the old one. It
        // gets one line per session.
        if (!descriptor.CoveredByManifestVersion)
            return !warnedOnce.Contains(descriptor.CategoryKey);

        return !warnedVersions.TryGetValue(descriptor.CategoryKey, out var warnedFor)
            || warnedFor != manifestVersion;
    }

    /// <summary>Records that this category's warning was logged.</summary>
    private void Remember(ManifestDescriptor descriptor, string? manifestVersion)
    {
        if (descriptor.CoveredByManifestVersion)
            warnedVersions[descriptor.CategoryKey] = manifestVersion;
        else
            warnedOnce.Add(descriptor.CategoryKey);
    }
}
