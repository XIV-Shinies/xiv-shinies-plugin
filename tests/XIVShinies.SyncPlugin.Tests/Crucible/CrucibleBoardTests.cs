using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The board window (XBMStageDetailList): every piece of the board with its progress, or the one
// piece being played.
public class CrucibleBoardTests
{
    // Positions in the window's values.
    private const int View = 1;
    private const int Count = 5;
    private const int ModeText = 40047;
    private const int FirstRecord = 6;
    private const int RecordStride = 40;

    // Offsets within one record.
    private const int Type = 0;
    private const int Node = 1;
    private const int Progress = 6;

    /// <summary>The record type of an enemy row.</summary>
    private const uint EnemyRow = 3;

    // The window lists the pieces from node 12 down.
    [Fact]
    public void The_pre_entry_board_lists_every_piece_unreached()
    {
        var reading = CrucibleBoard.Read(WindowFixture.Load("stage-view0-pre-entry-board1.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleBoardView.PreEntry, reading.View);
        Assert.Equal(
            Enumerable.Range(1, 12).Reverse().Select(node => (uint)node),
            reading.Pieces.Select(piece => piece.NodeIndex));
        Assert.All(reading.Pieces, piece => Assert.Equal(CrucibleProgress.NotReached, piece.Progress));
        Assert.Null(reading.CurrentNodeIndex);
        Assert.Null(reading.ModeText);
    }

    [Fact]
    public void Board_layout_mid_run_reads_each_pieces_progress()
    {
        var reading = CrucibleBoard.Read(WindowFixture.Load("stage-view1-midrun-progress-board1.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleBoardView.WholeBoard, reading.View);
        Assert.Equal("Standard", reading.ModeText);

        var progress = reading.Pieces.ToDictionary(piece => piece.NodeIndex, piece => piece.Progress);
        Assert.Equal(CrucibleProgress.Cleared, progress[8]);
        Assert.Equal(CrucibleProgress.NotTaken, progress[4]);
        Assert.Equal(CrucibleProgress.NotTaken, progress[2]);
        Assert.Equal(CrucibleProgress.NotReached, progress[12]);
    }

    [Fact]
    public void Mode_text_of_only_formatting_codes_reads_as_none()
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json");
        values[ModeText] = AddonValue.FromText("\u0002H\u0002\u0001\u0003");

        Assert.Null(CrucibleBoard.Read(values)!.ModeText);
    }

    // The whole-board view's selection follows whichever card is open: here the boss, while the shop
    // before it is still unvisited.
    [Fact]
    public void The_whole_board_view_names_no_current_piece()
    {
        var reading = CrucibleBoard.Read(WindowFixture.Load("stage-view1-expanded-enemies-board1.json"));

        Assert.NotNull(reading);
        Assert.Null(reading.CurrentNodeIndex);
    }

    // An expanded fight card is followed by its enemy rows, told apart by their record type.
    [Fact]
    public void Enemy_rows_are_skipped_and_never_read_as_pieces()
    {
        var reading = CrucibleBoard.Read(WindowFixture.Load("stage-view1-expanded-enemies-board1.json"));

        Assert.NotNull(reading);
        Assert.Equal(12, reading.Pieces.Count);
        Assert.Equal(12, reading.Pieces.Select(piece => piece.NodeIndex).Distinct().Count());
    }

    [Fact]
    public void Nothing_in_an_enemy_row_but_its_type_is_read()
    {
        var values = WindowFixture.Load("stage-view1-expanded-enemies-board1.json");
        var before = CrucibleBoard.Read(values);

        // The first enemy row follows the expanded boss card.
        var enemyRow = FirstRecord + RecordStride;
        Assert.Equal(EnemyRow, values[enemyRow + Type].Number);
        for (var offset = 1; offset < RecordStride; offset++)
            values[enemyRow + offset] = AddonValue.FromText("not read");

        Assert.Equal(before!.Pieces, CrucibleBoard.Read(values)!.Pieces);
    }

    // Board 2's random piece reads like any other once resolved.
    [Fact]
    public void Board_two_reads_its_thirteen_pieces()
    {
        var reading = CrucibleBoard.Read(WindowFixture.Load("stage-view1-midrun-random-board2.json"));

        Assert.NotNull(reading);
        Assert.Equal(13, reading.Pieces.Count);
        Assert.Equal(
            CrucibleProgress.Cleared,
            reading.Pieces.Single(piece => piece.NodeIndex == 10).Progress);
    }

    // Scoped to a lineup, the window lists only the piece being played, which is where the player is.
    [Fact]
    public void The_scoped_view_names_the_piece_being_played()
    {
        var reading = CrucibleBoard.Read(WindowFixture.Load("stage-view2-scoped-board1.json"));

        Assert.NotNull(reading);
        Assert.Equal(CrucibleBoardView.Scoped, reading.View);
        Assert.Equal(new uint[] { 1 }, reading.Pieces.Select(piece => piece.NodeIndex));
        Assert.Equal(1u, reading.CurrentNodeIndex);
    }

    // The scoped view this reader knows lists exactly one piece. The fixture's piece is followed by
    // two enemy rows, and the first becomes a second piece here.
    [Fact]
    public void A_scoped_view_with_two_pieces_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view2-scoped-board1.json");
        var secondRecord = FirstRecord + RecordStride;
        values[secondRecord + Type] = AddonValue.FromInteger(0);
        values[secondRecord + Node] = AddonValue.FromInteger(2);
        values[secondRecord + Progress] = AddonValue.FromInteger(0);

        Assert.Null(CrucibleBoard.Read(values));
    }

    // Making the one piece an enemy row leaves no piece.
    [Fact]
    public void A_scoped_view_with_no_piece_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view2-scoped-board1.json");
        values[FirstRecord + Type] = AddonValue.FromInteger(EnemyRow);

        Assert.Null(CrucibleBoard.Read(values));
    }

    [Fact]
    public void A_record_type_this_reader_does_not_know_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json");
        values[FirstRecord + Type] = AddonValue.FromInteger(4);

        Assert.Null(CrucibleBoard.Read(values));
    }

    [Fact]
    public void A_progress_this_reader_does_not_know_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json");
        values[FirstRecord + Progress] = AddonValue.FromInteger(3);

        Assert.Null(CrucibleBoard.Read(values));
    }

    // Node indexes start at 1.
    [Fact]
    public void A_piece_with_node_zero_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json");
        values[FirstRecord + Node] = AddonValue.FromInteger(0);

        Assert.Null(CrucibleBoard.Read(values));
    }

    // One piece listed twice means the records are misaligned.
    [Fact]
    public void A_piece_listed_twice_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json");
        values[FirstRecord + RecordStride + Node] = AddonValue.FromInteger(12);

        Assert.Null(CrucibleBoard.Read(values));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(9u)]
    public void A_view_this_reader_does_not_know_refuses_the_reading(uint view)
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json");
        values[View] = AddonValue.FromInteger(view);

        Assert.Null(CrucibleBoard.Read(values));
    }

    // The reading is refused rather than cut short at the last value.
    [Fact]
    public void A_count_beyond_the_values_refuses_the_reading()
    {
        var values = WindowFixture.Load("stage-view1-midrun-progress-board1.json").GetRange(0, 200);

        Assert.Null(CrucibleBoard.Read(values));
    }

    // The mode text sits past the last record, so a count that reaches it would read the mode out of
    // a record. The window is built here: every record an enemy row, and text at the mode's place.
    [Fact]
    public void A_count_whose_records_reach_the_mode_text_refuses_the_reading()
    {
        const int records = 1002;
        var values = new List<AddonValue>();
        for (var index = 0; index < FirstRecord + (records * RecordStride); index++)
            values.Add(AddonValue.Unreadable);

        values[View] = AddonValue.FromInteger(1);
        values[Count] = AddonValue.FromInteger(records);
        for (var record = 0; record < records; record++)
            values[FirstRecord + (record * RecordStride) + Type] = AddonValue.FromInteger(EnemyRow);
        values[ModeText] = AddonValue.FromText("inside a record");

        Assert.Null(CrucibleBoard.Read(values));
    }

    // Before its first refresh the window has no values at all.
    [Fact]
    public void An_empty_window_reads_as_nothing()
    {
        Assert.Null(CrucibleBoard.Read(new List<AddonValue>()));
    }
}
