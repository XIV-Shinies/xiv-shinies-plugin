using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>What one page of the Master's Bestiary said.</summary>
/// <param name="Seen">Every bestiary number the page listed, whether held or not.</param>
/// <param name="Captured">The numbers the page marked as held.</param>
/// <param name="CapturedTotal">
/// How many beasts the character holds altogether, as the window's own tally reports it, or null
/// when that tally could not be read.
/// </param>
/// <param name="BeastTotal">How many beasts the bestiary has altogether, or null when unread.</param>
public sealed record BestiaryPageReading(
    IReadOnlyList<int> Seen,
    IReadOnlyList<int> Captured,
    int? CapturedTotal,
    int? BeastTotal);

/// <summary>
/// Reads which beasts a page of the Master's Bestiary says the character holds.
/// </summary>
/// <remarks>
/// <para>
/// The window is handed a flat list of values to draw itself from, and the part of it that matters
/// is a run of fixed-size records — one per tile on the page — each carrying the bestiary number,
/// a flag saying whether the beast is held, the tile's caption, and slots this reader has no use
/// for. The window also carries its own tally of how many beasts are held out of how many exist,
/// which is what makes reading it worth more than the sum of the pages.
/// </para>
/// <para>
/// The page is only what is on screen, so one read is a fragment. The tally is what lets fragments
/// add up — but it is the window's own figure, and a window showing a filtered slice can report a
/// smaller bestiary than the game has, so the ledger checks it against the game's own bestiary size
/// before any claim is made. See <see cref="TamedBeastLedger.IsComplete"/>.
/// </para>
/// <para>
/// Nothing here knows the game. The layout is expressed as offsets and checked against the shape
/// of what arrives, so a patch that moves the records makes the read stop rather than hand back
/// numbers from the wrong slots — which, for a collection nothing can unmark, is the failure worth
/// engineering for.
/// </para>
/// </remarks>
public static class BestiaryPage
{
    /// <summary>Where the window keeps its "held out of total" tally.</summary>
    private const int TallyIndex = 7;

    /// <summary>Where the run of per-beast records begins.</summary>
    private const int FirstRecordIndex = 24;

    /// <summary>How many slots each record occupies.</summary>
    private const int RecordStride = 8;

    // Offsets within one record. The slots between them hold things this reader has no use for.
    private const int NumberOffset = 0;
    private const int CapturedOffset = 2;
    private const int CaptionOffset = 5;

    /// <summary>
    /// A ceiling on bestiary numbers for when the window's tally could not be read.
    /// </summary>
    /// <remarks>
    /// Deliberately far above the bestiary's real size: its job is to stop a misread from
    /// producing an absurd number, not to encode how many beasts the game has. The tally supplies
    /// the real bound whenever it is readable, so this only applies when it is not.
    /// </remarks>
    private const uint UnboundedNumberCeiling = 1000;

    /// <summary>Reads a page of the bestiary from the values the window was given.</summary>
    public static BestiaryPageReading Read(IReadOnlyList<AddonValue> values)
    {
        var (capturedTotal, beastTotal) = ReadTally(values);

        // The tally bounds the numbers when it is readable, which is the tightest honest bound
        // available: a record claiming a beast beyond the bestiary's own size is a misread.
        var ceiling = beastTotal is > 0 ? (uint)beastTotal.Value : UnboundedNumberCeiling;

        var seen = new List<int>();
        var captured = new List<int>();

        for (var index = FirstRecordIndex; index + RecordStride <= values.Count; index += RecordStride)
        {
            var number = values[index + NumberOffset].Number;
            var isCaptured = values[index + CapturedOffset].Flag;
            var caption = values[index + CaptionOffset].Text;

            // The run of records ends where the window's other data begins, and that shows up as a
            // slot not holding what a record holds. Stopping is the whole check: everything after
            // this point belongs to something else and must not be read as beasts.
            if (number is null || isCaptured is null || caption is null)
                break;

            if (number is 0 || number > ceiling)
                break;

            // The caption names the beast's number, so it proves the record is aligned — a run of
            // slots that merely looks like a record will not also caption itself correctly.
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

        // Written as one over the other. Split rather than pattern-matched, so the separator is the
        // only thing assumed about how the game spells it.
        var parts = text.Split('/');
        if (parts.Length != 2)
            return (null, null);

        if (!int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var held) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
        {
            return (null, null);
        }

        // A tally that claims more held than exist is not a tally this reader understands.
        if (total <= 0 || held < 0 || held > total)
            return (null, null);

        return (held, total);
    }

    /// <summary>Whether a tile's caption names this bestiary number.</summary>
    /// <remarks>
    /// Compared by digits alone, because the words around them are the client's language and the
    /// digits are not. A caption with no digits fails, which fails the record, which stops the
    /// read — the safe direction.
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
