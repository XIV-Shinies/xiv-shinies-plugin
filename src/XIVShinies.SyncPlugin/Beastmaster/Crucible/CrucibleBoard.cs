using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>Which of its three views the board window is showing.</summary>
/// <remarks>The values are the window's own numbers for each view.</remarks>
public enum CrucibleBoardView
{
    /// <summary>The whole board, opened from the roster pick at the entrance, before entering.</summary>
    PreEntry = 0,

    /// <summary>The whole board, opened as Board Layout (or beside the team window) in the duty.</summary>
    WholeBoard = 1,

    /// <summary>Scoped to the one piece being played, opened when choosing a lineup.</summary>
    Scoped = 2,
}

/// <summary>How far a run has gotten with one piece of the board.</summary>
/// <remarks>The values are the window's own numbers.</remarks>
public enum CrucibleProgress
{
    /// <summary>Not reached yet.</summary>
    NotReached = 0,

    /// <summary>Played and cleared.</summary>
    Cleared = 1,

    /// <summary>The other side of a fork, the branch the run did not take.</summary>
    NotTaken = 2,
}

/// <summary>One piece of the board.</summary>
/// <param name="NodeIndex">The piece's node index, numbered from 1.</param>
/// <param name="Progress">How far the run has gotten with it.</param>
public sealed record CrucibleBoardPiece(uint NodeIndex, CrucibleProgress Progress);

/// <summary>What the board window showed.</summary>
/// <param name="View">Which view the window was in.</param>
/// <param name="Pieces">The pieces listed, in the window's order.</param>
/// <param name="CurrentNodeIndex">
/// The piece being played, in the scoped view, where the window lists exactly that piece; null in
/// the whole-board views, whose selection follows whichever card the player opened.
/// </param>
/// <param name="ModeText">
/// The board's mode as the window drew it ("Standard" at Degree 0), formatting codes removed, or
/// null when the window carries none (before entering).
/// </param>
public sealed record CrucibleBoardReading(
    CrucibleBoardView View,
    IReadOnlyList<CrucibleBoardPiece> Pieces,
    uint? CurrentNodeIndex,
    string? ModeText);

/// <summary>
/// Reads the board window (<c>XBMStageDetailList</c>) from the values the window was given.
/// </summary>
/// <remarks>
/// <para>
/// The window carries its view and a count of records, then the records, 40 values each. A record's
/// first value is its type: a piece with no fight, a fight piece (collapsed or expanded), or an
/// enemy row. An expanded fight card is followed by one enemy row per enemy in the fight. An unknown
/// record type or progress, or a piece listed twice, refuses the whole reading.
/// </para>
/// <para>
/// <b>Enemy rows are skipped by their type and nothing else in them is read.</b> The plugin reads
/// only what the player's own run is made of, and an enemy's name, stats and weaknesses are not
/// that. Skipping by type rather than by position is what keeps an expanded card from shifting the
/// read onto enemy values.
/// </para>
/// </remarks>
public static class CrucibleBoard
{
    /// <summary>Where the window keeps its view.</summary>
    private const int ViewIndex = 1;

    /// <summary>Where the window keeps how many records it lists.</summary>
    private const int CountIndex = 5;

    /// <summary>Where the first record starts.</summary>
    private const int FirstRecord = 6;

    /// <summary>How many values each record spans.</summary>
    private const int RecordStride = 40;

    /// <summary>Where the window keeps the board's mode as text, in the duty's views.</summary>
    private const int ModeTextIndex = 40047;

    // Record types: the first value of every record.
    private const uint PlainPiece = 0;
    private const uint ExpandedFight = 1;
    private const uint CollapsedFight = 2;
    private const uint EnemyRow = 3;

    // Offsets within a piece record.
    private const int NodeOffset = 1;
    private const int ProgressOffset = 6;

    /// <summary>
    /// Reads the board window, or null when the values are not the layout this reader knows.
    /// </summary>
    /// <param name="values">Every value the window was handed, in order.</param>
    public static CrucibleBoardReading? Read(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= CountIndex)
            return null;

        // `is not { } viewNumber` is true when the place holds no number, and the `if` returns;
        // past it, `viewNumber` is the number (see CrucibleText for the pattern).
        if (values[ViewIndex].Number is not { } viewNumber || viewNumber > (uint)CrucibleBoardView.Scoped)
            return null;

        if (values[CountIndex].Number is not { } count)
            return null;

        // Where the counted records end. `(long)` widens the count before multiplying: a `uint`
        // times 40 stays a `uint` and can wrap around past its maximum, where a `long` has room.
        var recordsEnd = FirstRecord + ((long)count * RecordStride);

        // Every counted record has to fit inside the values handed, and none may reach the mode
        // text, so the fixed read of the mode can never land inside an enemy row.
        if (values.Count < recordsEnd || recordsEnd > ModeTextIndex)
            return null;

        // An enum is a named number underneath; the cast reads the window's number as its view.
        var view = (CrucibleBoardView)viewNumber;
        var pieces = new List<CrucibleBoardPiece>();
        var seen = new HashSet<uint>();

        for (var index = 0; index < count; index++)
        {
            var record = FirstRecord + (index * RecordStride);

            switch (values[record].Number)
            {
                // An enemy row: skipped, and nothing past its type is looked at.
                case EnemyRow:
                    continue;

                // `case A or B or C:` matches any of the three piece types. `seen.Add` returns false
                // for a node already in the set, so `!seen.Add(...)` catches a piece listed twice.
                case PlainPiece or ExpandedFight or CollapsedFight:
                    if (ReadPiece(values, record) is not { } piece || !seen.Add(piece.NodeIndex))
                        return null;

                    pieces.Add(piece);
                    break;

                // A record type this reader does not know: the layout has moved.
                default:
                    return null;
            }
        }

        // The scoped view lists the one piece being played, which is where the player is.
        uint? current = null;
        if (view == CrucibleBoardView.Scoped)
        {
            if (pieces.Count != 1)
                return null;

            current = pieces[0].NodeIndex;
        }

        return new CrucibleBoardReading(view, pieces, current, ReadModeText(values));
    }

    /// <summary>Reads one piece record, or null when its values are not the kinds expected.</summary>
    /// <param name="values">Every value the window was handed.</param>
    /// <param name="record">Where the record starts.</param>
    private static CrucibleBoardPiece? ReadPiece(IReadOnlyList<AddonValue> values, int record)
    {
        // Node indexes start at 1, so a 0 is not a piece.
        if (values[record + NodeOffset].Number is not { } node || node == 0)
            return null;

        if (values[record + ProgressOffset].Number is not { } progress
            || progress > (uint)CrucibleProgress.NotTaken)
        {
            return null;
        }

        return new CrucibleBoardPiece(node, (CrucibleProgress)progress);
    }

    /// <summary>The board's mode text, formatting codes removed, or null when there is none.</summary>
    /// <param name="values">Every value the window was handed.</param>
    private static string? ReadModeText(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= ModeTextIndex || values[ModeTextIndex].Text is not { } raw)
            return null;

        var text = CrucibleText.StripCodes(raw);
        return text.Length > 0 ? text : null;
    }
}
