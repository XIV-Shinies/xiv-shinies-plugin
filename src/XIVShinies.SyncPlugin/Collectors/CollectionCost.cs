using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>What one collection pass cost the frame it ran in, ready to be reported.</summary>
/// <remarks>
/// Collection runs on the framework thread, which has about 16.7ms per frame at 60fps, and every
/// collector spends part of that. Measuring the cost shows a contributor an expensive collector's
/// price on its first sweep. Reads no game state and logs nothing; the orchestrator logs the result.
/// </remarks>
public sealed record CollectionCost
{
    /// <summary>The cost at or above which a pass is a problem rather than a curiosity.</summary>
    /// <remarks>
    /// 8ms is half a 60fps frame, most of which the game itself needs, and past the point a player
    /// could feel the hitch. Not user-configurable: it is a developer-facing alarm, not a preference.
    /// </remarks>
    public static readonly TimeSpan FrameBudgetWarningThreshold = TimeSpan.FromMilliseconds(8);

    /// <summary>How long every collector in the pass took, together.</summary>
    public required TimeSpan Total { get; init; }

    /// <summary>True when the pass is slow enough to risk a visible stutter.</summary>
    public required bool OverBudget { get; init; }

    /// <summary>True when this pass overran the budget in a way worth raising an alarm about.</summary>
    /// <remarks>
    /// The session's first pass is exempt: first reads of game sheets and item lookups are one-time
    /// warm-up, about 21ms on a real client against 1 to 2ms for later passes, so counting it would fire
    /// every session. The alarm is for a pass that is repeatedly expensive, the shape an added collector
    /// or a grown manifest takes. Always implies <see cref="OverBudget"/>.
    /// </remarks>
    public required bool IsAlarming { get; init; }

    /// <summary>The per-collector breakdown, slowest first: <c>"quests 1.2ms, mounts 0.3ms"</c>.</summary>
    public required string Breakdown { get; init; }

    /// <summary>The category that took longest, or empty when no collector ran.</summary>
    /// <remarks>Ties break arbitrarily: the sort underneath is not stable.</remarks>
    public required string SlowestCategory { get; init; }

    /// <summary>True when no collector ran, so there is nothing worth reporting.</summary>
    public required bool IsEmpty { get; init; }

    /// <summary>Summarizes what a collection pass cost.</summary>
    /// <remarks>
    /// Names no category; it orders by duration, so the first name is the one worth acting on.
    /// </remarks>
    /// <param name="snapshot">The pass to summarize.</param>
    /// <param name="isFirstPassOfSession">
    /// Whether this is the first pass since the plugin loaded (see <see cref="IsAlarming"/>).
    /// </param>
    public static CollectionCost From(CollectionSnapshot snapshot, bool isFirstPassOfSession)
    {
        var durations = snapshot.Durations;

        if (durations.Count == 0)
        {
            return new CollectionCost
            {
                Total = TimeSpan.Zero,
                OverBudget = false,
                IsAlarming = false,
                Breakdown = string.Empty,
                SlowestCategory = string.Empty,
                IsEmpty = true,
            };
        }

        var total = TimeSpan.Zero;
        foreach (var duration in durations.Values)
            total += duration;

        var measured = new List<KeyValuePair<string, TimeSpan>>(durations);

        // Slowest first, so the first name in the line is the culprit.
        measured.Sort((a, b) => b.Value.CompareTo(a.Value));

        var parts = new List<string>(measured.Count);
        foreach (var (category, duration) in measured)
            parts.Add($"{category} {duration.TotalMilliseconds:F1}ms");

        var overBudget = total >= FrameBudgetWarningThreshold;

        return new CollectionCost
        {
            Total = total,
            OverBudget = overBudget,
            IsAlarming = overBudget && !isFirstPassOfSession,
            Breakdown = string.Join(", ", parts),
            SlowestCategory = measured[0].Key,
            IsEmpty = false,
        };
    }
}
