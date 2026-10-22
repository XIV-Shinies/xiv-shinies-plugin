using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Which cap warnings a pass should log, given what earlier passes already said.
public class PayloadCapWarningsTests
{
    private static PayloadCapDrop Drop(string categoryKey, int droppedEntries = 1, int cap = 50_000)
        => new()
        {
            CategoryKey = categoryKey,
            DroppedEntries = droppedEntries,
            Cap = cap,
        };

    [Fact]
    public void A_category_over_the_cap_is_reported_the_first_time()
    {
        var warnings = new PayloadCapWarnings();

        var lines = warnings.LinesFor([Drop("quests")]);

        Assert.Equal("quests: dropped 1 entry over the contract cap of 50000", Assert.Single(lines));
    }

    [Fact]
    public void The_line_agrees_its_noun_with_the_number_dropped()
    {
        var warnings = new PayloadCapWarnings();

        var lines = warnings.LinesFor([Drop("quests", droppedEntries: 2)]);

        Assert.Equal("quests: dropped 2 entries over the contract cap of 50000", Assert.Single(lines));
    }

    // An overage is a standing condition; see PayloadCapWarnings.
    [Fact]
    public void The_same_category_is_not_reported_twice()
    {
        var warnings = new PayloadCapWarnings();

        warnings.LinesFor([Drop("quests")]);
        var second = warnings.LinesFor([Drop("quests")]);

        Assert.Empty(second);
    }

    // Silence is per category: a second category going over is news.
    [Fact]
    public void A_category_that_has_not_been_reported_still_gets_its_line()
    {
        var warnings = new PayloadCapWarnings();

        warnings.LinesFor([Drop("quests")]);
        var second = warnings.LinesFor([Drop("quests"), Drop("items", cap: 10_000)]);

        Assert.Equal(
            "items: dropped 1 entry over the contract cap of 10000", Assert.Single(second));
    }

    // Keyed by category, not by the whole drop, which would re-report a growing overage.
    [Fact]
    public void A_larger_overage_in_a_reported_category_stays_silent()
    {
        var warnings = new PayloadCapWarnings();

        warnings.LinesFor([Drop("quests", droppedEntries: 1)]);
        var second = warnings.LinesFor([Drop("quests", droppedEntries: 5000)]);

        Assert.Empty(second);
    }

    // For the character switch; SyncManager.OnLogout says why the memory is dropped.
    [Fact]
    public void A_reset_lets_a_reported_category_be_reported_again()
    {
        var warnings = new PayloadCapWarnings();

        warnings.LinesFor([Drop("quests")]);
        warnings.Reset();
        var afterReset = warnings.LinesFor([Drop("quests")]);

        Assert.Equal(
            "quests: dropped 1 entry over the contract cap of 50000", Assert.Single(afterReset));
    }

    [Fact]
    public void A_pass_with_nothing_over_a_cap_says_nothing()
    {
        var warnings = new PayloadCapWarnings();

        Assert.Empty(warnings.LinesFor([]));
    }

    // One line per category rather than a summary, so the log names each one. LinesFor keeps the
    // order the cuts were handed over.
    [Fact]
    public void Every_category_over_a_cap_gets_its_own_line()
    {
        var warnings = new PayloadCapWarnings();

        var lines = warnings.LinesFor([Drop("quests"), Drop("items", cap: 10_000)]);

        Assert.Equal(
            [
                "quests: dropped 1 entry over the contract cap of 50000",
                "items: dropped 1 entry over the contract cap of 10000",
            ],
            lines);
    }
}
