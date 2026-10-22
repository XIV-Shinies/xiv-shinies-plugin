using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One server-supplied manifest: the category it feeds, how to read it from a
/// <see cref="CollectContext"/>, and the words the orchestrator uses when it is clipped.
/// </summary>
/// <remarks>
/// The <c>Func</c> members sit in the table rather than a <c>switch</c>, so code reading manifests
/// never names a category. <paramref name="Truncated"/> cannot come from <paramref name="Read"/>,
/// which already returns the clipped list, so each manifest states both side by side.
/// </remarks>
/// <param name="CategoryKey">
/// The collector category this manifest feeds (see <see cref="CategoryKeys"/>).
/// </param>
/// <param name="Noun">Names the manifest in the warning, for example <c>"item"</c>.</param>
/// <param name="Unit">What the manifest's entries are, for example <c>"ids"</c>.</param>
/// <param name="Verb">What the plugin does with an entry, for example <c>"scanned"</c>.</param>
/// <param name="Read">Returns the manifest's ids, already bounded to the ceiling.</param>
/// <param name="Truncated">True when the server asked about more than the ceiling allows.</param>
/// <param name="CoveredByManifestVersion">
/// Whether <c>/config</c>'s <c>manifestVersion</c> hash changes when this manifest's content does:
/// true for the item manifest, false for the quest-sequence manifest, which the contract leaves
/// outside the hash. Anything that watches for a changed manifest must check it.
/// </param>
internal sealed record ManifestDescriptor(
    string CategoryKey,
    string Noun,
    string Unit,
    string Verb,
    Func<CollectContext, IReadOnlyList<uint>> Read,
    Func<CollectContext, bool> Truncated,
    bool CoveredByManifestVersion);

/// <summary>Every manifest the server can send, in one table.</summary>
/// <remarks>
/// The single place that ties a manifest to its category: <see cref="CollectContext"/> reads through it
/// and the truncation warning comes from it, so the code that consumes manifests never changes when
/// one is added. A table, not a dictionary keyed off the wire, because <c>/config</c> names each
/// manifest as its own field (<c>itemManifest</c>, <c>questSequenceManifest</c>), and this keeps
/// mapping those to categories in one place.
/// </remarks>
internal static class ManifestCatalog
{
    /// <summary>
    /// Every known manifest. A walk of the table sees this order, so a pass that clips two manifests
    /// logs them in a stable order.
    /// </summary>
    public static IReadOnlyList<ManifestDescriptor> All { get; } =
    [
        new ManifestDescriptor(
            CategoryKeys.Items,
            Noun: "item",
            Unit: "ids",
            Verb: "scanned",
            Read: static context => context.ReadItemManifest(),
            Truncated: static context => context.ItemManifestTruncated(),
            CoveredByManifestVersion: true),
        new ManifestDescriptor(
            CategoryKeys.QuestSequences,
            Noun: "quest-sequence",
            Unit: "quests",
            Verb: "looked up",
            Read: static context => context.ReadQuestSequenceManifest(),
            Truncated: static context => context.QuestSequenceManifestTruncated(),
            CoveredByManifestVersion: false),
    ];

    /// <summary>The descriptor for a category, or null when that category has no manifest.</summary>
    public static ManifestDescriptor? For(string categoryKey)
    {
        foreach (var descriptor in All)
        {
            if (descriptor.CategoryKey == categoryKey)
                return descriptor;
        }

        return null;
    }

    /// <summary>
    /// The log line for a clipped manifest, or null for a category with no manifest. Built here so
    /// the orchestrator can warn about a manifest it knows only by key.
    /// </summary>
    public static string? TruncationWarning(string categoryKey)
    {
        if (For(categoryKey) is not { } descriptor)
            return null;

        return $"The server's {descriptor.Noun} manifest exceeds the " +
            $"{CollectContext.MaxManifestItems}-id ceiling; {descriptor.Unit} past the first " +
            $"{CollectContext.MaxManifestItems} will not be {descriptor.Verb} or reported.";
    }
}
