using System.Collections.Generic;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// The merge rules for the settings window's per-category status memory: which skip reasons,
/// partial-read phrases and chip hover copy survive from pass to pass.
/// </summary>
/// <remarks>
/// An unlock pass runs only the collectors whose categories changed, so each merge lets the new pass
/// overrule only the categories it ran; overwriting the whole memory would make the window call every
/// absent category fine. Every merge returns a fresh dictionary and never touches its inputs, so
/// <see cref="SyncManager"/> can publish the result to the draw thread without a lock.
/// </remarks>
public static class CollectionStatusMemory
{
    /// <summary>
    /// Merges a pass's skip reasons over the remembered ones: a category skipped this pass gets
    /// its new reason, one collected this pass loses its old one, and one this pass never ran
    /// keeps whatever it last said.
    /// </summary>
    /// <param name="previous">The memory before this pass.</param>
    /// <param name="snapshot">The pass to merge in.</param>
    public static IReadOnlyDictionary<string, string> MergeSkipReasons(
        IReadOnlyDictionary<string, string> previous, CollectionSnapshot snapshot)
    {
        var merged = new Dictionary<string, string>(previous);

        foreach (var (category, reason) in snapshot.Skipped)
            merged[category] = reason;

        foreach (var category in snapshot.Collections.Keys)
            merged.Remove(category);

        return merged;
    }

    /// <summary>
    /// Merges a pass's partial-read phrases over the remembered ones: a category collected with a
    /// phrase keeps or gains it, one collected without a phrase was read in full and loses its old
    /// one, and one this pass never collected keeps whatever it last said.
    /// </summary>
    /// <remarks>
    /// A category skipped this pass also keeps its stale phrase, which stays hidden: the read-status
    /// panel shows a skip reason ahead of a partial phrase.
    /// </remarks>
    /// <param name="previous">The memory before this pass.</param>
    /// <param name="snapshot">The pass to merge in.</param>
    public static IReadOnlyDictionary<string, string> MergePartialNotes(
        IReadOnlyDictionary<string, string> previous, CollectionSnapshot snapshot) =>
        MergeCollectedValues(previous, snapshot, snapshot.PartialNotes);

    /// <summary>
    /// Merges a pass's healthy-chip hover copy over the remembered set, by
    /// <see cref="MergePartialNotes"/>'s rules.
    /// </summary>
    /// <param name="previous">The memory before this pass.</param>
    /// <param name="snapshot">The pass to merge in.</param>
    public static IReadOnlyDictionary<string, string> MergeCollectedDetails(
        IReadOnlyDictionary<string, string> previous, CollectionSnapshot snapshot) =>
        MergeCollectedValues(previous, snapshot, snapshot.CollectedDetails);

    // The body of both collected-value merges; MergePartialNotes states the rule.
    private static IReadOnlyDictionary<string, string> MergeCollectedValues(
        IReadOnlyDictionary<string, string> previous,
        CollectionSnapshot snapshot,
        IReadOnlyDictionary<string, string> values)
    {
        var merged = new Dictionary<string, string>(previous);

        foreach (var category in snapshot.Collections.Keys)
        {
            if (values.TryGetValue(category, out var value))
                merged[category] = value;
            else
                merged.Remove(category);
        }

        return merged;
    }
}
