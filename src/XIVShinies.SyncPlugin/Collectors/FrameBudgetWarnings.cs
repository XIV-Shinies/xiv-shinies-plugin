using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides whether an expensive collection pass is worth a warning, remembering what it already said.
/// </summary>
/// <remarks>
/// On a slow machine or with a large manifest, every sweep overruns and names the same collector, at a
/// user who cannot act on it. Not reset at a character switch: the machine and the collectors stay the
/// same. Not thread-safe; only the framework-thread collection pass calls it.
/// </remarks>
public sealed class FrameBudgetWarnings
{
    /// <summary>
    /// The collectors already named as the culprit. A pass slow for a new reason names a new culprit
    /// and earns its own warning.
    /// </summary>
    private readonly HashSet<string> warned = [];

    /// <summary>True when this pass is worth warning about and has not already been reported.</summary>
    /// <remarks>Records as it tests, so a caller warns on true and stays quiet afterwards.</remarks>
    // Only an alarming pass may reach warned.Add. A cheap pass or the warm-up would otherwise spend the
    // collector's one warning and silence a later, genuinely slow pass.
    public bool ShouldReport(CollectionCost cost) => cost.IsAlarming && warned.Add(cost.SlowestCategory);
}
