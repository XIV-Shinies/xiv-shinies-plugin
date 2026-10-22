using Xunit;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// When the character enters and leaves a board, from the territory it stands in on each tick.
public class CrucibleVisitsTests
{
    private const uint FirstBoard = CrucibleTerritories.FirstBoard;
    private const uint SecondBoard = 1340;
    private const uint Entrance = 148;

    [Fact]
    public void Standing_outside_a_board_is_no_visit()
    {
        var visits = new CrucibleVisits();

        Assert.Equal(CrucibleVisitChange.None, visits.Update(Entrance));
        Assert.Equal(CrucibleVisitChange.None, visits.Update(Entrance));
        Assert.Null(visits.Board);
    }

    // Seen arriving from somewhere else, the character zoned in.
    [Fact]
    public void Zoning_into_a_board_is_a_fresh_entry()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);

        Assert.Equal(CrucibleVisitChange.Entered, visits.Update(FirstBoard));
        Assert.Equal(FirstBoard, visits.Board);
    }

    // The character was there before the plugin started, or before the sharing was switched on.
    [Fact]
    public void A_board_on_the_first_tick_is_already_inside()
    {
        var visits = new CrucibleVisits();

        Assert.Equal(CrucibleVisitChange.AlreadyInside, visits.Update(FirstBoard));
        Assert.Equal(FirstBoard, visits.Board);
    }

    [Fact]
    public void Staying_in_the_board_changes_nothing()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);
        visits.Update(FirstBoard);

        Assert.Equal(CrucibleVisitChange.None, visits.Update(FirstBoard));
    }

    [Fact]
    public void Zoning_out_of_a_board_is_a_leave()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);
        visits.Update(FirstBoard);

        Assert.Equal(CrucibleVisitChange.Left, visits.Update(Entrance));
        Assert.Null(visits.Board);
    }

    // The scheduler closes the first visit on its own when it hears of the entry.
    [Fact]
    public void Moving_straight_to_another_board_is_a_fresh_entry()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);
        visits.Update(FirstBoard);

        Assert.Equal(CrucibleVisitChange.Entered, visits.Update(SecondBoard));
        Assert.Equal(SecondBoard, visits.Board);
    }

    [Fact]
    public void Coming_back_to_the_same_board_is_a_fresh_entry()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);
        visits.Update(FirstBoard);
        visits.Update(Entrance);

        Assert.Equal(CrucibleVisitChange.Entered, visits.Update(FirstBoard));
    }

    [Fact]
    public void Staying_in_a_board_the_plugin_started_in_changes_nothing()
    {
        var visits = new CrucibleVisits();
        visits.Update(FirstBoard);

        Assert.Equal(CrucibleVisitChange.None, visits.Update(FirstBoard));
    }

    // The tick outside is the one seen first.
    [Fact]
    public void After_a_reset_outside_a_board_zoning_in_is_a_fresh_entry()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);
        visits.Reset();
        visits.Update(Entrance);

        Assert.Equal(CrucibleVisitChange.Entered, visits.Update(FirstBoard));
    }

    // After a reset, as when the sharing is switched off, the next tick is seen as the first again.
    [Fact]
    public void A_reset_forgets_the_visit_and_the_last_territory()
    {
        var visits = new CrucibleVisits();
        visits.Update(Entrance);
        visits.Update(FirstBoard);

        visits.Reset();

        Assert.Null(visits.Board);
        Assert.Equal(CrucibleVisitChange.AlreadyInside, visits.Update(FirstBoard));
    }
}
