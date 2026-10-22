using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>Which of its three views the board window is showing.</summary>
/// <remarks>The values are the window's own numbers for each view.</remarks>
public enum CrucibleBoardView
{
    /// <summary>The whole board, opened from the roster pick at the entrance, before entering.</summary>
    PreEntry = 0,

    /// <summary>The whole board, opened in the duty as Board Layout or beside the team window.</summary>
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
public sealed record CrucibleBoardPiece(uint NodeIndex, CrucibleProgress Progress);

/// <summary>What the board window showed.</summary>
/// <param name="Pieces">The pieces listed, in the window's order.</param>
/// <param name="CurrentNodeIndex">
/// The piece being played, in the scoped view, which lists exactly that piece; null in the whole-board
/// views, whose selection follows whichever card the player opened.
/// </param>
/// <param name="ModeText">
/// The board's mode as drawn, such as "Standard" at Degree 0, formatting codes removed. Null when the
/// window carries none, as before entering.
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
/// The window carries its view and a record count, then 40-value records, each opening with its type:
/// a piece with no fight, a fight piece collapsed or expanded, or an enemy row, one per enemy after an
/// expanded fight. An unknown record type or progress, or a piece listed twice, refuses the whole
/// reading. <b>Enemy rows are skipped by their type and nothing else in them is read</b>: an enemy is
/// not part of the player's own run, and skipping by type rather than position keeps an expanded card
/// from shifting the read onto enemy values.
/// </remarks>
public static class CrucibleBoard
{
    // The window's layout.
    private const int ViewIndex = 1;
    private const int CountIndex = 5;
    private const int FirstRecord = 6;
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
    public static CrucibleBoardReading? Read(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= CountIndex)
            return null;

        if (values[ViewIndex].Number is not { } viewNumber || viewNumber > (uint)CrucibleBoardView.Scoped)
            return null;

        if (values[CountIndex].Number is not { } count)
            return null;

        // Multiplied as a `long` so a huge count cannot wrap.
        var recordsEnd = FirstRecord + ((long)count * RecordStride);

        // Every counted record must fit in the values handed and stop short of the mode text, so the
        // fixed read of the mode can never land inside an enemy row.
        if (values.Count < recordsEnd || recordsEnd > ModeTextIndex)
            return null;

        var view = (CrucibleBoardView)viewNumber;
        var pieces = new List<CrucibleBoardPiece>();
        var seen = new HashSet<uint>();

        for (var index = 0; index < count; index++)
        {
            var record = FirstRecord + (index * RecordStride);

            switch (values[record].Number)
            {
                // Nothing past an enemy row's type is read.
                case EnemyRow:
                    continue;

                // A node already in the set is a piece listed twice.
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
    private static string? ReadModeText(IReadOnlyList<AddonValue> values)
    {
        if (values.Count <= ModeTextIndex || values[ModeTextIndex].Text is not { } raw)
            return null;

        var text = CrucibleText.StripCodes(raw);
        return text.Length > 0 ? text : null;
    }
}
