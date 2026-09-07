using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides which payload-cap warnings to log this pass, remembering what it has already said.
/// </summary>
/// <remarks>
/// <para>
/// Pure and outside the orchestrator, like <see cref="Collectors.ManifestTruncationWarnings"/>:
/// a decision a test can reach, rather than plumbing the orchestrator buries.
/// </para>
/// <para>
/// One character's collection only grows against a ceiling the contract fixes, so a category that
/// crosses a cap stays across it on every later full sweep, and one line carries everything a
/// reader learns from that.
/// </para>
/// <para>
/// Not thread-safe: both writers — the collection pass and the logout reset — run on the game's
/// main thread, so there is a single writer and no lock is required.
/// </para>
/// </remarks>
public sealed class PayloadCapWarnings
{
    /// <summary>The categories whose cap warning has already been logged.</summary>
    private readonly HashSet<string> warned = [];

    /// <summary>
    /// The lines to log for this pass, in the order the cuts are given. Empty when every cut
    /// category has already been reported, which is the usual answer once one has been.
    /// </summary>
    /// <param name="drops">Every category this pass had to cut (see <see cref="PayloadCaps"/>).</param>
    public IReadOnlyList<string> LinesFor(IReadOnlyList<PayloadCapDrop> drops)
    {
        var lines = new List<string>();

        foreach (var drop in drops)
        {
            // Only the category identifies a warning. The number of entries dropped climbs as the
            // collection grows, so keying on that would report the same standing condition forever.
            // `Add` answers false for a category already reported, so this tests and records in one
            // step.
            if (warned.Add(drop.CategoryKey))
                lines.Add(drop.Line);
        }

        return lines;
    }

    /// <summary>Forgets every category reported so far.</summary>
    /// <remarks>For the character switch — see the call in <c>SyncManager.OnLogout</c>.</remarks>
    public void Reset() => warned.Clear();
}
