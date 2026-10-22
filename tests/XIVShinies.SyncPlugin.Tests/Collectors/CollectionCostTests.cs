using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// What a collection pass cost its frame. Ordering by duration and comparing against a threshold are
// both easy to get backwards.
public class CollectionCostTests
{
    private static CollectionSnapshot Snapshot(params (string Category, double Milliseconds)[] durations)
    {
        var measured = new Dictionary<string, TimeSpan>();
        foreach (var (category, milliseconds) in durations)
            measured[category] = TimeSpan.FromMilliseconds(milliseconds);

        return new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>(),
            Skipped = new Dictionary<string, string>(),
            Durations = measured,
        };
    }

    [Fact]
    public void A_pass_where_nothing_ran_has_nothing_to_report()
    {
        var cost = CollectionCost.From(Snapshot(), isFirstPassOfSession: false);

        Assert.True(cost.IsEmpty);
        Assert.Equal(TimeSpan.Zero, cost.Total);
        Assert.False(cost.OverBudget);
        Assert.False(cost.IsAlarming);
        Assert.Equal(string.Empty, cost.Breakdown);
        Assert.Equal(string.Empty, cost.SlowestCategory);
    }

    [Fact]
    public void The_total_is_the_sum_of_every_collector()
    {
        var cost = CollectionCost.From(
            Snapshot(("quests", 1.5), ("mounts", 0.5)), isFirstPassOfSession: false);

        Assert.Equal(TimeSpan.FromMilliseconds(2), cost.Total);
        Assert.False(cost.IsEmpty);
    }

    // The first name in the log line is the one worth acting on. Alphabetical order would put
    // "mounts" first here.
    [Fact]
    public void The_breakdown_names_the_slowest_collector_first()
    {
        var cost = CollectionCost.From(
            Snapshot(("mounts", 0.5), ("quests", 4.0), ("minions", 2.0)), isFirstPassOfSession: false);

        Assert.Equal("quests 4.0ms, minions 2.0ms, mounts 0.5ms", cost.Breakdown);
    }

    // FrameBudgetWarnings keys its memory on this. The wrong end of the sort would blame the
    // cheapest collector on every later pass.
    [Fact]
    public void The_slowest_category_is_the_collector_that_took_longest()
    {
        var cost = CollectionCost.From(
            Snapshot(("mounts", 0.5), ("quests", 4.0), ("minions", 2.0)), isFirstPassOfSession: false);

        Assert.Equal("quests", cost.SlowestCategory);
    }

    [Fact]
    public void A_cheap_pass_is_neither_over_budget_nor_alarming()
    {
        var cost = CollectionCost.From(Snapshot(("quests", 1.0)), isFirstPassOfSession: false);

        Assert.False(cost.OverBudget);
        Assert.False(cost.IsAlarming);
    }

    // Exactly at the budget is already too slow to ignore.
    [Fact]
    public void A_pass_exactly_at_the_threshold_is_over_budget()
    {
        var atThreshold = CollectionCost.FrameBudgetWarningThreshold.TotalMilliseconds;

        var cost = CollectionCost.From(Snapshot(("quests", atThreshold)), isFirstPassOfSession: false);

        Assert.True(cost.OverBudget);
    }

    // The budget is on the total, not per collector: many cheap collectors overrun a frame as surely
    // as one expensive one.
    [Fact]
    public void Many_cheap_collectors_can_overrun_the_budget_together()
    {
        var cost = CollectionCost.From(
            Snapshot(("a", 3.0), ("b", 3.0), ("c", 3.0)), isFirstPassOfSession: false);

        Assert.True(cost.OverBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(9), cost.Total);
    }

    // A 60fps frame is 16.7ms. A budget of a whole frame would let a visible stutter through
    // unreported.
    [Fact]
    public void The_budget_threshold_is_half_a_sixty_fps_frame()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(8), CollectionCost.FrameBudgetWarningThreshold);
    }

    // --- Which overruns raise an alarm (see CollectionCost.IsAlarming) ---------------------------

    [Fact]
    public void The_first_pass_of_a_session_is_over_budget_without_being_alarming()
    {
        var cost = CollectionCost.From(Snapshot(("items", 20.0)), isFirstPassOfSession: true);

        Assert.True(cost.OverBudget);
        Assert.False(cost.IsAlarming);
    }

    [Fact]
    public void A_later_pass_over_budget_is_alarming()
    {
        var cost = CollectionCost.From(Snapshot(("items", 20.0)), isFirstPassOfSession: false);

        Assert.True(cost.OverBudget);
        Assert.True(cost.IsAlarming);
    }

    // The first-pass exemption is for warm-up: a cheap first pass has nothing to excuse.
    [Fact]
    public void A_cheap_first_pass_is_neither_over_budget_nor_alarming()
    {
        var cost = CollectionCost.From(Snapshot(("quests", 1.0)), isFirstPassOfSession: true);

        Assert.False(cost.OverBudget);
        Assert.False(cost.IsAlarming);
    }
}
