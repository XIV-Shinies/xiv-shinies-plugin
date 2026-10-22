using System.Collections.Generic;
using System.Linq;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Chooses which collectors an upload should run.
/// </summary>
/// <remarks>
/// An unlock upload names the categories it carries, and the server dates them from that. Selecting
/// one category too many would date a collection the player did not just acquire, and dates are
/// never revised.
/// </remarks>
public static class CollectorSelection
{
    /// <summary>The collectors to run for one upload.</summary>
    /// <param name="collectors">Every registered collector.</param>
    /// <param name="categories">
    /// The categories to restrict to, or <c>null</c> for a full sweep. An empty set selects nothing.
    /// </param>
    /// <remarks>
    /// A key naming no registered collector matches nothing, so it is ignored rather than throwing.
    /// </remarks>
    public static IEnumerable<ICollector> For(
        IEnumerable<ICollector> collectors, IReadOnlySet<string>? categories) =>
        categories is null
            ? collectors
            : collectors.Where(collector => categories.Contains(collector.CategoryKey));
}
