using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Pins the glamour dresser outfit mapping: Build turns sheet rows into a lookup from set id to
// piece array, and StoredPieces reads a slot's unlock bits to say which pieces are in the outfit.
public class MirageSetIndexTests
{
    // A stored outfit names only its set item id; the dresser cache does not list the pieces, so
    // the index carries them in column order.
    [Fact]
    public void Maps_each_set_to_its_pieces_in_column_order()
    {
        var index = MirageSetIndex.Build(new[]
        {
            (SetItemId: 30u, PieceItemIds: new uint[] { 101, 102, 103, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 40u, PieceItemIds: new uint[] { 201, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        });

        Assert.Equal(new uint[] { 101, 102, 103, 0, 0, 0, 0, 0, 0, 0, 0 }, index[30]);
        Assert.Equal(new uint[] { 201, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, index[40]);
        Assert.Equal(2, index.Count);
    }

    // Bit i maps to piece-array index i, so Build must never compact a row's array. Compacting a
    // Ring-only row would move the Ring piece to index 0, under the wrong bit.
    [Fact]
    public void Preserves_interior_zero_columns()
    {
        var ringOnly = new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 555 };

        var index = MirageSetIndex.Build(new[] { (SetItemId: 50u, PieceItemIds: ringOnly) });

        Assert.Equal(ringOnly, index[50]);
        Assert.Equal(555u, index[50][10]);
    }

    // A row with no set id or no pieces is sheet padding, not an outfit.
    [Fact]
    public void Skips_rows_with_no_set_id_or_no_pieces()
    {
        var index = MirageSetIndex.Build(new[]
        {
            (SetItemId: 0u, PieceItemIds: new uint[] { 101, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 60u, PieceItemIds: new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 70u, PieceItemIds: new uint[] { 301, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        });

        Assert.Single(index);
        Assert.Equal(301u, index[70][0]);
    }

    // Deterministic rather than throwing if the sheet ever lists a set id twice.
    [Fact]
    public void Keeps_the_first_row_when_a_set_appears_more_than_once()
    {
        var index = MirageSetIndex.Build(new[]
        {
            (SetItemId: 80u, PieceItemIds: new uint[] { 401, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            (SetItemId: 80u, PieceItemIds: new uint[] { 999, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        });

        Assert.Equal(401u, index[80][0]);
    }

    [Fact]
    public void An_empty_sheet_yields_an_empty_index()
    {
        Assert.Empty(MirageSetIndex.Build(Array.Empty<(uint, uint[])>()));
    }

    // A bit is set once its piece is withdrawn, so a complete outfit reads 0.
    [Fact]
    public void No_bits_set_yields_every_non_empty_piece()
    {
        var pieces = new uint[] { 101, 102, 103, 0, 0, 0, 0, 0, 0, 0, 0 };

        Assert.Equal(new uint[] { 101, 102, 103 }, MirageSetIndex.StoredPieces(pieces, 0).ToArray());
    }

    // Checks every column, so a bit-order slip anywhere in the 11 slots fails.
    [Fact]
    public void Each_single_bit_withdraws_its_own_column()
    {
        var pieces = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        for (var column = 0; column < 11; column++)
        {
            var bits = (ushort)(1 << column);
            var expected = pieces.Where((_, index) => index != column).ToArray();

            Assert.Equal(expected, MirageSetIndex.StoredPieces(pieces, bits).ToArray());
        }
    }

    [Fact]
    public void Multiple_bits_withdraw_their_pieces()
    {
        var pieces = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var bits = (ushort)((1 << 1) | (1 << 3)); // OffHand + Body withdrawn

        Assert.Equal(
            new uint[] { 10, 12, 14, 15, 16, 17, 18, 19, 20 },
            MirageSetIndex.StoredPieces(pieces, bits).ToArray());
    }

    [Fact]
    public void All_eleven_bits_yield_nothing()
    {
        var pieces = new uint[] { 10, 0, 12, 0, 14, 0, 16, 0, 18, 0, 20 };

        Assert.Empty(MirageSetIndex.StoredPieces(pieces, 0x7FF));
    }

    // Only 11 slots exist. A stray high bit, from padding or a wider field, must not withdraw a real
    // slot or read past the piece array.
    [Fact]
    public void Bits_above_the_slot_count_are_ignored()
    {
        var pieces = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        var bits = unchecked((ushort)0xF800); // bits 11..15 set, all above the 11 slots

        Assert.Equal(pieces, MirageSetIndex.StoredPieces(pieces, bits).ToArray());
    }

    // Armor outfits leave the weapon slots empty, so this is the common case, not an edge.
    [Fact]
    public void A_stored_bit_over_an_empty_column_is_skipped()
    {
        var pieces = new uint[] { 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        Assert.Equal(new uint[] { 11 }, MirageSetIndex.StoredPieces(pieces, 0).ToArray());
    }

    // A defensive guard: Build always emits 11-wide rows.
    [Fact]
    public void A_short_array_is_never_read_past_its_end()
    {
        var pieces = new uint[] { 10, 11 }; // only two slots present

        Assert.Equal(new uint[] { 10, 11 }, MirageSetIndex.StoredPieces(pieces, 0).ToArray());
        // Bit 5 has no slot to affect.
        Assert.Equal(
            new uint[] { 10 },
            MirageSetIndex.StoredPieces(pieces, (1 << 1) | (1 << 5)).ToArray());
    }

    // Two columns can name the same item, such as a matched ring pair. Each stored slot is a separate
    // copy, so the tally counts two.
    [Fact]
    public void A_piece_shared_by_two_columns_is_yielded_once_per_stored_slot()
    {
        var pieces = new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 500, 500 }; // Bracelets + Ring, same id

        Assert.Equal(new uint[] { 500, 500 }, MirageSetIndex.StoredPieces(pieces, 0).ToArray());
    }
}
