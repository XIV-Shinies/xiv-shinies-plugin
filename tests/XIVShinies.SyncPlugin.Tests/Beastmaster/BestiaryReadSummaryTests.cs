using Xunit;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Beastmaster;

// The comparison the bestiary observer relies on to skip logging a redraw that read nothing new.
public class BestiaryReadSummaryTests
{
    private static LedgerReport CompleteReport() => new(50, 50, 50, 50, IsComplete: true);

    private static BestiaryReadSummary Read(LedgerReport ledger, int listed = 25, int held = 25) =>
        new(listed, held, PageCapturedTotal: 50, PageBeastTotal: 50, ledger, SheetSize: 50);

    // A redraw that reads the same figures produces a summary equal to the last one. The two ledger
    // reports are built separately, as the observer builds a fresh one on every read — so this
    // would fail if the nested report compared by reference, and every read would look new.
    [Fact]
    public void A_redraw_reading_the_same_figures_is_equal_to_the_last_read()
    {
        Assert.Equal(Read(CompleteReport()), Read(CompleteReport()));
    }

    [Fact]
    public void A_change_in_the_ledger_makes_a_read_new()
    {
        var incomplete = CompleteReport() with { Held = 49, IsComplete = false };

        Assert.NotEqual(Read(CompleteReport()), Read(incomplete));
    }

    [Fact]
    public void A_change_in_the_page_makes_a_read_new()
    {
        Assert.NotEqual(Read(CompleteReport()), Read(CompleteReport(), listed: 5, held: 5));
    }

    // The observer keeps the last logged read as a nullable value, and before anything is logged a
    // read must count as new.
    [Fact]
    public void A_read_differs_from_no_read_at_all()
    {
        BestiaryReadSummary? none = null;

        Assert.True(Read(CompleteReport()) != none);
    }
}
