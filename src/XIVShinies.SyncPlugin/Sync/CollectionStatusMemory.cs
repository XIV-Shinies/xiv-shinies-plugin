using System.Collections.Generic;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// The merge rules for the settings window's per-category status memory: which skip reasons,
/// partial-read phrases, and chip hover copy survive from pass to pass.
/// </summary>
/// <remarks>
/// <para>
/// The rules exist because an unlock pass runs only the collectors whose categories changed, so
/// it can speak for those and no others — overwriting the whole memory with its results would
/// erase every absent category's status and make the window claim it is fine. Each merge starts
/// from the previous memory and lets the new pass overrule only the categories it actually ran.
/// </para>
/// <para>
/// Pure and Dalamud-free, like the other per-pass rules (<see cref="Collectors.CollectorSelection"/>,
/// <see cref="PayloadCaps"/>): <see cref="SyncManager"/> holds the fields and the thread
/// discipline, this class holds the arithmetic, and the tests hold the arithmetic to account.
/// Every merge returns a fresh dictionary and never touches its inputs, which is what lets the
/// manager publish the result to the draw thread without a lock.
/// </para>
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

        // Whatever was read successfully this pass is no longer skipped.
        foreach (var category in snapshot.Collections.Keys)
            merged.Remove(category);

        return merged;
    }

    /// <summary>
    /// Merges a pass's partial-read phrases over the remembered ones: a category collected WITH
    /// a phrase keeps (or gains) it, one collected without a phrase was read in full and loses
    /// its old one, and one this pass never collected keeps whatever it last said.
    /// </summary>
    /// <remarks>
    /// A category that was <i>skipped</i> this pass also keeps its stale phrase: the
    /// read-status panel shows a skip reason ahead of a partial phrase, so the stale entry is
    /// invisible until a collected pass settles it one way or the other.
    /// </remarks>
    /// <param name="previous">The memory before this pass.</param>
    /// <param name="snapshot">The pass to merge in.</param>
    public static IReadOnlyDictionary<string, string> MergePartialNotes(
        IReadOnlyDictionary<string, string> previous, CollectionSnapshot snapshot) =>
        MergeCollectedValues(previous, snapshot, snapshot.PartialNotes);

    /// <summary>
    /// Merges a pass's healthy-chip hover copy over the remembered set — the same rules as
    /// <see cref="MergePartialNotes"/>, applied to <see cref="CollectionSnapshot.CollectedDetails"/>.
    /// </summary>
    /// <param name="previous">The memory before this pass.</param>
    /// <param name="snapshot">The pass to merge in.</param>
    public static IReadOnlyDictionary<string, string> MergeCollectedDetails(
        IReadOnlyDictionary<string, string> previous, CollectionSnapshot snapshot) =>
        MergeCollectedValues(previous, snapshot, snapshot.CollectedDetails);

    // The shared body behind both collected-value merges — MergePartialNotes states the rule;
    // this method only applies it to whichever dictionary it is handed.
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
