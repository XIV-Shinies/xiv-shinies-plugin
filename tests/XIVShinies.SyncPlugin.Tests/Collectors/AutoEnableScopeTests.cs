using System;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The standing "turn on new collections automatically" answer is the one path that switches a
// collection on with no action from the user on the consent list, so what it may reach is pinned
// here. Asked of the collectors rather than of a list of names, which keeps the load path free of
// category names.
public class AutoEnableScopeTests
{
    // The Dalamud-free stand-in for a real collector: it answers the two self-description questions
    // the scope reads without loading a game assembly.
    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(
            string categoryKey, bool usesItemManifest = false, bool requiresOwnOptIn = false)
        {
            CategoryKey = categoryKey;
            UsesItemManifest = usesItemManifest;
            RequiresOwnOptIn = requiresOwnOptIn;
        }

        public string CategoryKey { get; }

        public string DisplayName => $"{CategoryKey} display";

        public string Section => "Fakes";

        public string WhatGetsSent => $"what {CategoryKey} sends";

        public string? Details => null;

        public bool UsesItemManifest { get; }

        public bool RequiresOwnOptIn { get; }

        // The scope never reads these; the interface requires them.
        public bool RequiresServerSupport => false;

        public bool IsSingleRecord => false;

        public bool ReadsStorage => false;

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] { 1 });
    }

    [Fact]
    public void A_collection_that_requires_its_own_opt_in_is_left_out()
    {
        var keys = AutoEnableScope.EligibleCategoryKeys(new[]
        {
            new FakeCollector("quests"),
            new FakeCollector("appearance", requiresOwnOptIn: true),
            new FakeCollector("mounts"),
        });

        Assert.Equal(new[] { "quests", "mounts" }, keys);
    }

    // The manifest exclusion still applies on top: a collection whose groups the user answers
    // separately is never switched on for them (see ManifestConsent.FixedScopeCategoryKeys).
    [Fact]
    public void A_manifest_driven_collection_is_left_out()
    {
        var keys = AutoEnableScope.EligibleCategoryKeys(new[]
        {
            new FakeCollector("quests"),
            new FakeCollector("items", usesItemManifest: true),
        });

        Assert.Equal(new[] { "quests" }, keys);
    }

    [Fact]
    public void A_list_with_nothing_eligible_yields_no_keys()
    {
        Assert.Empty(AutoEnableScope.EligibleCategoryKeys(new[]
        {
            new FakeCollector("items", usesItemManifest: true),
            new FakeCollector("appearance", requiresOwnOptIn: true),
        }));
    }

    [Fact]
    public void An_empty_registry_yields_no_keys()
    {
        Assert.Empty(AutoEnableScope.EligibleCategoryKeys(Array.Empty<ICollector>()));
    }
}
