using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Picks the collectors an upload runs. The keys here are ones the plugin does not ship, so a
// category-name branch in the selection fails these tests.
public class CollectorSelectionTests
{
    private const string Facewear = "facewear";
    private const string Orchestrion = "orchestrion";

    // ICollector is Dalamud-free, so constructing this loads no game assembly.
    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey) => CategoryKey = categoryKey;

        public string CategoryKey { get; }

        public string DisplayName => CategoryKey;

        public string Section => "Fakes";

        public string WhatGetsSent => $"Facts about {CategoryKey}.";

        public string? Details => null;

        public bool UsesItemManifest => false;

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    private static IReadOnlyList<ICollector> Collectors() =>
        new ICollector[] {new FakeCollector(Facewear), new FakeCollector(Orchestrion)};

    private static string[] KeysOf(IEnumerable<ICollector> collectors) =>
        collectors.Select(collector => collector.CategoryKey).ToArray();

    // Null means a full sweep: the login, interval and manual path.
    [Fact]
    public void A_null_category_set_selects_every_collector()
    {
        var selected = CollectorSelection.For(Collectors(), categories: null);

        Assert.Equal(new[] {Facewear, Orchestrion}, KeysOf(selected));
    }

    // The unlock path. Selecting one collector too many would date a collection the player did not
    // just acquire, and the server never revises an acquisition date.
    [Fact]
    public void A_category_set_selects_only_the_matching_collectors()
    {
        var selected = CollectorSelection.For(Collectors(), new HashSet<string> {Orchestrion});

        Assert.Equal(new[] {Orchestrion}, KeysOf(selected));
    }

    // The set can name a collection this build does not ship.
    [Fact]
    public void An_unrecognized_category_matches_nothing_rather_than_throwing()
    {
        var selected = CollectorSelection.For(Collectors(), new HashSet<string> {"triple_triad"});

        Assert.Empty(selected);
    }

    // Empty is not null: null means every collector.
    [Fact]
    public void An_empty_category_set_selects_nothing()
    {
        Assert.Empty(CollectorSelection.For(Collectors(), new HashSet<string>()));
    }

    [Fact]
    public void Selecting_from_no_collectors_yields_nothing()
    {
        Assert.Empty(CollectorSelection.For(new List<ICollector>(), categories: null));
    }
}
