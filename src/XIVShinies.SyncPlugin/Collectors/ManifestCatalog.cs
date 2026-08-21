using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One server-supplied manifest: the category it belongs to, how to read it out of a
/// <see cref="CollectContext"/>, and the words the orchestrator uses when it is clipped.
/// </summary>
/// <remarks>
/// <para>
/// The two <c>Func</c> members are the C# equivalent of storing a function in an object — the
/// closest TypeScript analog is <c>{ read: (ctx) =&gt; number[] }</c>. They live in the table
/// rather than in a <c>switch</c> so that the code reading manifests never names a category:
/// it walks <see cref="ManifestCatalog.All"/> and calls whatever it finds.
/// </para>
/// <para>
/// <paramref name="Truncated"/> cannot be derived from <paramref name="Read"/>, because
/// <c>Read</c> already returns the clipped list — by then the evidence of clipping is gone. Each
/// manifest therefore states both, side by side, where a reader can check they agree.
/// </para>
/// </remarks>
/// <param name="CategoryKey">The collector category this manifest feeds (see <see cref="CategoryKeys"/>).</param>
/// <param name="Noun">Names the manifest in the warning, e.g. <c>"item"</c>.</param>
/// <param name="Unit">What the manifest's entries are, e.g. <c>"ids"</c>.</param>
/// <param name="Verb">What the plugin does with an entry, e.g. <c>"scanned"</c>.</param>
/// <param name="Read">Returns the manifest's ids, already bounded to the ceiling.</param>
/// <param name="Truncated">True when the server asked about more than the ceiling allows.</param>
/// <param name="CoveredByManifestVersion">
/// Whether <c>/config</c>'s <c>manifestVersion</c> hash changes when this manifest's content
/// does. True for the item manifest, whose content the hash is computed from; false for the
/// quest-sequence manifest, which the contract places deliberately outside it. Anything that
/// wants to notice "this manifest changed" has to know which of the two it is holding.
/// </param>
internal sealed record ManifestDescriptor(
    string CategoryKey,
    string Noun,
    string Unit,
    string Verb,
    Func<CollectContext, IReadOnlyList<uint>> Read,
    Func<CollectContext, bool> Truncated,
    bool CoveredByManifestVersion);

/// <summary>
/// Every manifest the server can send, in one table.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place that knows a manifest exists. <see cref="CollectContext"/> reads
/// through it and the truncation warning is generated from it, so the code that consumes
/// manifests never changes when one is added — a new descriptor is how they discover it.
/// </para>
/// <para>
/// It is a table and not a dictionary keyed by the wire because the server names each manifest
/// as its own field on <c>/config</c> (<c>itemManifest</c>, <c>questSequenceManifest</c>, …).
/// Until the contract carries manifests keyed by category, projecting those named fields into
/// keyed entries is work that has to happen somewhere; doing it here confines it to one place.
/// </para>
/// </remarks>
internal static class ManifestCatalog
{
    /// <summary>
    /// Every known manifest. Anything walking the whole table sees them in this order, which is
    /// what makes a pass that clips two manifests log them in a stable order.
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
    /// The log line for a clipped manifest, or null for a category with no manifest. Built here
    /// so the orchestrator can warn about a manifest it knows nothing about beyond its key.
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
