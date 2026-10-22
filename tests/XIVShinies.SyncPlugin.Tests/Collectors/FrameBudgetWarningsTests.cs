using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// An overrun usually has a lasting cause, such as a slow machine or a large manifest, so the warning
// describes a standing condition and must not repeat every sweep.
public class FrameBudgetWarningsTests
{
    private static CollectionCost Cost(
        (string Category, double Milliseconds)[] durations, bool isFirstPassOfSession)
    {
        var measured = new Dictionary<string, TimeSpan>();
        foreach (var (category, milliseconds) in durations)
            measured[category] = TimeSpan.FromMilliseconds(milliseconds);

        return CollectionCost.From(
            new CollectionSnapshot
            {
                Collections = new Dictionary<string, JsonNode>(),
                Skipped = new Dictionary<string, string>(),
                Durations = measured,
            },
            isFirstPassOfSession);
    }

    [Fact]
    public void An_alarming_pass_is_reported_the_first_time()
    {
        var warnings = new FrameBudgetWarnings();

        Assert.True(warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false)));
    }

    [Fact]
    public void The_same_culprit_is_not_reported_twice()
    {
        var warnings = new FrameBudgetWarnings();

        warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false));

        Assert.False(warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false)));
    }

    // Only the culprit identifies a warning, not the cost: a machine that gets slower has nothing
    // new to say.
    [Fact]
    public void The_same_culprit_at_a_different_cost_is_not_reported_again()
    {
        var warnings = new FrameBudgetWarnings();

        warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false));

        Assert.False(warnings.ShouldReport(Cost([("items", 60.0)], isFirstPassOfSession: false)));
    }

    // Silencing a new culprit would hide the regression the warning exists to catch.
    [Fact]
    public void A_new_culprit_earns_its_own_warning()
    {
        var warnings = new FrameBudgetWarnings();

        warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false));

        Assert.True(warnings.ShouldReport(Cost([("quests", 20.0)], isFirstPassOfSession: false)));
    }

    // A warm-up pass must not spend the culprit's one report, or a genuinely slow later pass would
    // go unreported.
    [Fact]
    public void A_first_pass_is_not_reported_and_does_not_spend_the_culprits_warning()
    {
        var warnings = new FrameBudgetWarnings();

        Assert.False(warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: true)));
        Assert.True(warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false)));
    }

    [Fact]
    public void A_pass_inside_the_budget_is_not_reported()
    {
        var warnings = new FrameBudgetWarnings();

        Assert.False(warnings.ShouldReport(Cost([("items", 1.0)], isFirstPassOfSession: false)));
    }

    // A cheap pass must not spend the culprit's report either.
    [Fact]
    public void A_cheap_pass_leaves_its_culprit_reportable()
    {
        var warnings = new FrameBudgetWarnings();

        warnings.ShouldReport(Cost([("items", 1.0)], isFirstPassOfSession: false));

        Assert.True(warnings.ShouldReport(Cost([("items", 20.0)], isFirstPassOfSession: false)));
    }

    [Fact]
    public void A_pass_where_nothing_ran_is_not_reported()
    {
        var warnings = new FrameBudgetWarnings();

        Assert.False(warnings.ShouldReport(Cost([], isFirstPassOfSession: false)));
    }

    // The key is the slowest collector, not any name in the pass. Only a pass of several
    // collectors can tell the two ends of the sort apart.
    [Fact]
    public void A_pass_whose_culprit_changed_is_reported_again()
    {
        var warnings = new FrameBudgetWarnings();

        warnings.ShouldReport(Cost([("items", 20.0), ("quests", 1.0)], isFirstPassOfSession: false));

        Assert.True(
            warnings.ShouldReport(Cost([("items", 1.0), ("quests", 20.0)], isFirstPassOfSession: false)));
    }
}
