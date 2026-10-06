using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Kill-switch precedence, pinned directly rather than only through the runner. Four switches must
// all be on; any one of them off wins. Two belong to the user, two to the server.
public class CollectorGateTests
{
    private const string Category = "quests";

    private static PluginSettings FullyOptedIn()
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };
        settings.SetCategoryEnabled(Category, true);
        return settings;
    }

    private static ConfigResponse Remote(bool enabled = true, bool categoryEnabled = true) => new()
    {
        Categories = new Dictionary<string, bool> { [Category] = categoryEnabled },
        Enabled = enabled,
        Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
    };

    // The collection every test below asks about: an ordinary one, which a key the server never
    // names leaves enabled.
    private static FakeCollector Ordinary() => new(Category);

    // The same collection, declaring that it is only worth collecting once the server names it.
    private static FakeCollector NeedingServerSupport() =>
        new(Category) { RequiresServerSupport = true };

    [Fact]
    public void All_four_switches_on_permits_collection()
    {
        Assert.True(CollectorGate.IsEnabled(Ordinary(), FullyOptedIn(), Remote()));
    }

    [Fact]
    public void The_user_master_switch_off_wins()
    {
        var settings = FullyOptedIn();
        settings.MasterEnabled = false;

        Assert.False(CollectorGate.IsEnabled(Ordinary(), settings, Remote()));
    }

    // Nothing may be collected before the user has been shown what gets sent and opted in.
    [Fact]
    public void Incomplete_onboarding_wins()
    {
        var settings = FullyOptedIn();
        settings.OnboardingComplete = false;

        Assert.False(CollectorGate.IsEnabled(Ordinary(), settings, Remote()));
    }

    [Fact]
    public void A_category_the_user_did_not_opt_into_wins()
    {
        var settings = FullyOptedIn();
        settings.SetCategoryEnabled(Category, false);

        Assert.False(CollectorGate.IsEnabled(Ordinary(), settings, Remote()));
    }

    [Fact]
    public void The_server_global_kill_switch_wins()
    {
        Assert.False(CollectorGate.IsEnabled(Ordinary(), FullyOptedIn(), Remote(enabled: false)));
    }

    [Fact]
    public void The_server_per_category_kill_switch_wins()
    {
        Assert.False(CollectorGate.IsEnabled(
            Ordinary(), FullyOptedIn(), Remote(categoryEnabled: false)));
    }

    // Without a fetched config we cannot know the server's switches. Proceeding is safe — the
    // server strips disabled categories and answers 503 on its global switch — and refusing would
    // strand the plugin whenever /config is unreachable.
    [Fact]
    public void A_missing_remote_config_falls_back_to_the_users_own_switches()
    {
        Assert.True(CollectorGate.IsEnabled(Ordinary(), FullyOptedIn(), remoteConfig: null));
    }

    // ...but the user's switches still govern when the config is missing.
    [Fact]
    public void A_missing_remote_config_never_overrides_the_user()
    {
        var settings = FullyOptedIn();
        settings.MasterEnabled = false;

        Assert.False(CollectorGate.IsEnabled(Ordinary(), settings, remoteConfig: null));
    }

    // A category the server has never heard of is permitted: the server strips unknown payload keys,
    // so a collection the server does not know costs only a few discarded bytes.
    [Fact]
    public void A_category_the_server_never_mentions_is_permitted()
    {
        var settings = new PluginSettings { MasterEnabled = true, OnboardingComplete = true };
        settings.SetCategoryEnabled("facewear", true);

        Assert.True(CollectorGate.IsEnabled(new FakeCollector("facewear"), settings, Remote()));
    }

    // --- A collection that needs the server to name it ------------------------------------------
    // The stricter half of the server's gate. A collection declaring RequiresServerSupport is only
    // worth uploading to a server that knows it, so only an explicit "on" in the category map
    // counts.

    [Fact]
    public void A_server_supported_category_the_server_never_names_is_refused()
    {
        var config = Remote() with { Categories = new Dictionary<string, bool>() };
        Assert.False(CollectorGate.ServerPermits(NeedingServerSupport(), config));
    }

    [Fact]
    public void A_server_supported_category_named_true_is_permitted()
    {
        Assert.True(CollectorGate.ServerPermits(NeedingServerSupport(), Remote()));
    }

    [Fact]
    public void A_server_supported_category_named_false_is_refused()
    {
        Assert.False(CollectorGate.ServerPermits(
            NeedingServerSupport(), Remote(categoryEnabled: false)));
    }

    // Before /config arrives an ordinary category proceeds (the server enforces its switches on
    // arrival), but one that needs the server to name it waits: nothing says the server knows it.
    [Fact]
    public void Without_a_config_only_ordinary_categories_proceed()
    {
        Assert.True(CollectorGate.ServerPermits(Ordinary(), null));
        Assert.False(CollectorGate.ServerPermits(NeedingServerSupport(), null));
    }

    [Fact]
    public void The_global_switch_outranks_a_named_category()
    {
        Assert.False(CollectorGate.ServerPermits(NeedingServerSupport(), Remote(enabled: false)));
    }

    // --- The whole gate, with the declaration in play -------------------------------------------
    // IsEnabled reads the collector's own declaration, so the stricter rule reaches a collection
    // without anyone naming it.

    // A collection the user opted into is still refused when it needs the server to name it and
    // the server has not: the user's consent is one half of the gate, never both.
    [Fact]
    public void A_collector_needing_server_support_is_refused_when_the_config_omits_it()
    {
        var config = Remote() with { Categories = new Dictionary<string, bool>() };

        Assert.False(CollectorGate.IsEnabled(NeedingServerSupport(), FullyOptedIn(), config));
    }

    // The same config, an ordinary collector: the ordinary rule that an unnamed key reads as enabled
    // holds for every collection that does not ask for the stricter one.
    [Fact]
    public void An_ordinary_collector_is_permitted_when_the_config_omits_it()
    {
        var config = Remote() with { Categories = new Dictionary<string, bool>() };

        Assert.True(CollectorGate.IsEnabled(Ordinary(), FullyOptedIn(), config));
    }

    // The user's half still governs: a server naming the collection cannot opt the user in.
    [Fact]
    public void A_named_collector_the_user_did_not_opt_into_is_refused()
    {
        var settings = FullyOptedIn();
        settings.SetCategoryEnabled(Category, false);

        Assert.False(CollectorGate.IsEnabled(NeedingServerSupport(), settings, Remote()));
    }

    // A Dalamud-free stand-in for a real collector. The gate reads only its key and its
    // RequiresServerSupport self-description; the rest exists because the interface requires it.
    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey) => CategoryKey = categoryKey;

        public string CategoryKey { get; }

        public string DisplayName => CategoryKey;

        public string Section => "Fakes";

        public string WhatGetsSent => $"Facts about {CategoryKey}.";

        public string? Details => null;

        public bool UsesItemManifest => false;

        public bool RequiresServerSupport { get; init; }

        public bool IsSingleRecord { get; init; }

        public bool ReadsStorage { get; init; }

        public bool RequiresOwnOptIn { get; init; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] { 1 });
    }
}
