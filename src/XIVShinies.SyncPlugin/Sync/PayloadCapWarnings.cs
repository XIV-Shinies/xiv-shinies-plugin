using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides which payload-cap warnings to log this pass, remembering what it has already said.
/// </summary>
/// <remarks>
/// A collection only grows, so a category over a cap stays over it on every later full sweep, and one
/// line says all a reader learns from that. Not thread-safe: its writers, the collection pass and
/// the logout reset, both run on the game's main thread.
/// </remarks>
public sealed class PayloadCapWarnings
{
    /// <summary>The categories whose cap warning has already been logged.</summary>
    private readonly HashSet<string> warned = [];

    /// <summary>
    /// The lines to log for this pass, in the order the cuts are given. Empty when every cut category
    /// has already been reported.
    /// </summary>
    /// <param name="drops">Every category this pass had to cut (see <see cref="PayloadCaps"/>).</param>
    public IReadOnlyList<string> LinesFor(IReadOnlyList<PayloadCapDrop> drops)
    {
        var lines = new List<string>();

        foreach (var drop in drops)
        {
            // Keyed on the category alone: the dropped count climbs as the collection grows, so keying
            // on it would repeat the same standing condition forever.
            if (warned.Add(drop.CategoryKey))
                lines.Add(drop.Line);
        }

        return lines;
    }

    /// <summary>Forgets every category reported so far, for a character switch.</summary>
    public void Reset() => warned.Clear();
}
