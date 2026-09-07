using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides whether an expensive collection pass is worth a warning, remembering what it already
/// said.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CollectionCost.IsAlarming"/> already exempts the session's warm-up, which is the pass
/// nobody can make cheaper. What is left is a pass that overruns the frame budget repeatedly — on a
/// slow machine or with a large manifest, that is every sweep and every unlock burst, all naming
/// the same collector, at a user who has no way to act on it.
/// </para>
/// <para>
/// Not reset at the character switch: what this describes is the machine and the set of
/// collectors, neither of which changes when a character does.
/// </para>
/// <para>
/// Not thread-safe: the collection pass that drives it is framework thread only.
/// </para>
/// </remarks>
public sealed class FrameBudgetWarnings
{
    /// <summary>The collectors already named as the culprit in a warning.</summary>
    /// <remarks>
    /// Keyed on the collector that took longest, so a pass that is expensive for a new reason names
    /// a new culprit and earns its own warning, while one expensive for the reason already reported
    /// does not.
    /// </remarks>
    private readonly HashSet<string> warned = [];

    /// <summary>True when this pass is worth warning about and has not already been reported.</summary>
    /// <remarks>Records as it tests, so a caller warns on true and stays quiet afterwards.</remarks>
    // `&&` short-circuits, so `warned.Add` never runs on a pass that is not alarming. That order is
    // the rule, not a style choice: a cheap pass or the session's warm-up would otherwise record the
    // collector's name and spend the one warning it gets, leaving a genuinely slow pass by that same
    // collector silent afterwards.
    public bool ShouldReport(CollectionCost cost) => cost.IsAlarming && warned.Add(cost.SlowestCategory);
}
