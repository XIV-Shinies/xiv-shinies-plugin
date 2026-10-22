using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>What one page of the Master's Bestiary said.</summary>
/// <param name="Seen">Every bestiary number the page listed, whether held or not.</param>
/// <param name="Captured">The numbers the page marked as held.</param>
/// <param name="CapturedTotal">
/// How many beasts the character holds altogether, by the window's own tally, or null when unread.
/// </param>
/// <param name="BeastTotal">How many beasts the bestiary has altogether, or null when unread.</param>
public sealed record BestiaryPageReading(
    IReadOnlyList<int> Seen,
    IReadOnlyList<int> Captured,
    int? CapturedTotal,
    int? BeastTotal);

/// <summary>Reads which beasts a page of the Master's Bestiary says the character holds.</summary>
/// <remarks>
/// The window's values hold a run of fixed-size records, one per tile, each with the bestiary number,
/// a held flag and the tile's caption, plus a "held out of total" tally. A page is only what is on
/// screen, and the tally alone cannot say the window shows everything, since a filtered view keeps
/// the true size while shrinking the held count; <see cref="TamedBeastLedger.IsComplete"/> weighs it
/// before any claim. The layout is checked against the shape of what arrives, so a patch that moves
/// the records stops the read instead of returning numbers from the wrong slots, which matters for a
/// collection nothing can unmark.
/// </remarks>
public static class BestiaryPage
{
    /// <summary>Where the window keeps its "held out of total" tally.</summary>
    private const int TallyIndex = 7;

    /// <summary>Where the run of per-beast records begins.</summary>
    private const int FirstRecordIndex = 24;

    /// <summary>How many slots each record occupies.</summary>
    private const int RecordStride = 8;

    // Offsets within one record; the slots between hold nothing this reader uses.
    private const int NumberOffset = 0;
    private const int CapturedOffset = 2;
    private const int CaptionOffset = 5;

    /// <summary>A ceiling on bestiary numbers for when the window's tally is unreadable.</summary>
    /// <remarks>
    /// Far above the bestiary's real size: it only stops a misread from producing an absurd number,
    /// and does not encode how many beasts the game has.
    /// </remarks>
    private const uint UnboundedNumberCeiling = 1000;

    /// <summary>Reads a page of the bestiary from the values the window was given.</summary>
    public static BestiaryPageReading Read(IReadOnlyList<AddonValue> values)
    {
        var (capturedTotal, beastTotal) = ReadTally(values);

        // A record claiming a beast beyond the bestiary's own size is a misread.
        var ceiling = beastTotal is > 0 ? (uint)beastTotal.Value : UnboundedNumberCeiling;

        var seen = new List<int>();
        var captured = new List<int>();

        for (var index = FirstRecordIndex; index + RecordStride <= values.Count; index += RecordStride)
        {
            var number = values[index + NumberOffset].Number;
            var isCaptured = values[index + CapturedOffset].Flag;
            var caption = values[index + CaptionOffset].Text;

            // The records end where the window's other data begins, which shows as a slot not
            // holding what a record holds. Nothing after that point may be read as beasts.
            if (number is null || isCaptured is null || caption is null)
                break;

            if (number is 0 || number > ceiling)
                break;

            // The caption names the beast's number, which proves the record is aligned: slots that
            // merely look like a record will not also caption themselves correctly.
            if (!CaptionNames(caption, number.Value))
                break;

            seen.Add((int)number.Value);
            if (isCaptured.Value)
                captured.Add((int)number.Value);
        }

        return new BestiaryPageReading(seen, captured, capturedTotal, beastTotal);
    }

    /// <summary>Reads the window's "held out of total" tally, or nulls when it is not there.</summary>
    private static (int? Captured, int? Total) ReadTally(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= TallyIndex)
            return (null, null);

        var text = values[TallyIndex].Text;
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        // Split, not pattern-matched, so the separator is all that is assumed about the game's
        // wording.
        var parts = text.Split('/');
        if (parts.Length != 2)
            return (null, null);

        if (!int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var held) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
        {
            return (null, null);
        }

        // A tally that claims more held than exist is not one this reader understands.
        if (total <= 0 || held < 0 || held > total)
            return (null, null);

        return (held, total);
    }

    /// <summary>Whether a tile's caption names this bestiary number.</summary>
    /// <remarks>
    /// Compared by digits alone, since the words around them are in the client's language. A caption
    /// with no digits fails the record, which stops the read: the safe direction.
    /// </remarks>
    private static bool CaptionNames(string caption, uint number)
    {
        var digits = new StringBuilder(caption.Length);
        foreach (var character in caption)
        {
            if (char.IsAsciiDigit(character))
                digits.Append(character);
        }

        if (digits.Length == 0)
            return false;

        return uint.TryParse(
                   digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var named)
               && named == number;
    }
}
