using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The one-time group migration carries a user's consent onto the server's legacy groups. It asks the
// collectors whose consent that is, so the sync orchestrator names no category.
public class ManifestConsentTests
{
    private const string UnknownCategory = "facewear";

    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(string categoryKey, bool usesItemManifest)
        {
            CategoryKey = categoryKey;
            UsesItemManifest = usesItemManifest;
        }

        public string CategoryKey { get; }

        public string DisplayName => $"{CategoryKey} display";

        public string Section => "Fakes";

        public string WhatGetsSent => $"what {CategoryKey} sends";

        public string? Details => null;

        public bool UsesItemManifest { get; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] { 1 });
    }

    // The one no-click consent path depends on leaving out a category whose groups the user answers
    // separately; ManifestConsent.FixedScopeCategoryKeys explains why.
    [Fact]
    public void Only_categories_a_tick_settles_on_their_own_have_a_fixed_scope()
    {
        var keys = ManifestConsent.FixedScopeCategoryKeys(new[]
        {
            new FakeCollector("quests", usesItemManifest: false),
            new FakeCollector("items", usesItemManifest: true),
            new FakeCollector("orchestrionRolls", usesItemManifest: false),
        });

        Assert.Equal(new[] { "quests", "orchestrionRolls" }, keys);
    }

    [Fact]
    public void A_list_of_only_manifest_driven_categories_has_no_fixed_scope_keys()
    {
        Assert.Empty(ManifestConsent.FixedScopeCategoryKeys(new[]
        {
            new FakeCollector("items", usesItemManifest: true),
        }));
    }

    [Fact]
    public void A_manifest_driven_category_the_user_opted_into_is_found()
    {
        var settings = new PluginSettings();
        settings.SetCategoryEnabled("items", true);

        var collectors = new ICollector[]
        {
            new FakeCollector("quests", usesItemManifest: false),
            new FakeCollector("items", usesItemManifest: true),
        };

        Assert.True(ManifestConsent.AnyManifestCategoryEnabled(collectors, settings));
    }

    // A collection without a manifest has no groups to carry consent onto.
    [Fact]
    public void A_category_that_is_not_manifest_driven_does_not_count()
    {
        var settings = new PluginSettings();
        settings.SetCategoryEnabled("quests", true);

        var collectors = new ICollector[]
        {
            new FakeCollector("quests", usesItemManifest: false),
            new FakeCollector("items", usesItemManifest: true),
        };

        Assert.False(ManifestConsent.AnyManifestCategoryEnabled(collectors, settings));
    }

    [Fact]
    public void A_manifest_driven_category_the_user_left_off_gives_nothing_to_carry_over()
    {
        var collectors = new ICollector[] { new FakeCollector("items", usesItemManifest: true) };

        Assert.False(ManifestConsent.AnyManifestCategoryEnabled(collectors, new PluginSettings()));
    }

    // The gate: a category-name check in the orchestrator would fail this test.
    [Fact]
    public void An_unknown_manifest_driven_category_answers_for_its_own_consent()
    {
        var settings = new PluginSettings();
        settings.SetCategoryEnabled(UnknownCategory, true);

        var collectors = new ICollector[] { new FakeCollector(UnknownCategory, usesItemManifest: true) };

        Assert.True(ManifestConsent.AnyManifestCategoryEnabled(collectors, settings));
    }

    [Fact]
    public void No_collectors_at_all_means_no_consent_to_carry_over()
    {
        Assert.False(ManifestConsent.AnyManifestCategoryEnabled(
            Array.Empty<ICollector>(), new PluginSettings()));
    }

    // --- The category/group agreement rules -------------------------------------------------
    // A category and its groups send nothing without each other, so their checkboxes are written
    // together. These rules decide what the user consented to, so they live here, not in the draw code.

    // The parent defaults to server-permitted; a server-off parent has its own tests.
    private static ItemGroupRow GroupRow(string key, bool enabled = false, bool parentServerEnabled = true) =>
        new()
        {
            Key = key,
            Label = $"{key} label",
            Enabled = enabled,
            ParentServerEnabled = parentServerEnabled,
            IsNew = false,
        };

    private static CategorySettingsRow Row(
        string key,
        IReadOnlyList<ItemGroupRow>? groups = null,
        bool userEnabled = false,
        bool serverEnabled = true,
        bool serverGloballyOff = false) => new()
    {
        Key = key,
        DisplayName = $"{key} display",
        Section = "Fakes",
        WhatGetsSent = $"what {key} sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        ServerGloballyOff = serverGloballyOff,
        UsesItemManifest = groups is not null,
        Groups = groups,
    };

    // The groups are what the category sends, so a ticked category with every group off would upload
    // nothing.
    [Fact]
    public void Ticking_a_category_ticks_every_group_the_user_can_see_under_it()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });

        ManifestConsent.SetRowConsent(row, enabled: true, settings);

        Assert.True(settings.IsCategoryEnabled("items"));
        Assert.True(settings.IsItemGroupEnabled("proofs"));
        Assert.True(settings.IsItemGroupEnabled("materials"));
    }

    [Fact]
    public void Unticking_a_category_unticks_its_groups_with_it()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });
        ManifestConsent.SetRowConsent(row, enabled: true, settings);

        ManifestConsent.SetRowConsent(row, enabled: false, settings);

        Assert.False(settings.IsCategoryEnabled("items"));
        Assert.Empty(settings.EnabledItemGroupKeys);
    }

    // A group not on the row, such as one the server added after the list was built, is neither granted
    // nor revoked.
    [Fact]
    public void Only_the_groups_on_the_row_are_ever_written()
    {
        var settings = new PluginSettings();
        settings.SetItemGroupEnabled("some-other-collections-group", true);

        var row = Row("items", new[] { GroupRow("proofs") });

        ManifestConsent.SetRowConsent(row, enabled: true, settings);
        Assert.True(settings.IsItemGroupEnabled("proofs"));

        ManifestConsent.SetRowConsent(row, enabled: false, settings);
        Assert.False(settings.IsItemGroupEnabled("proofs"));

        Assert.True(settings.IsItemGroupEnabled("some-other-collections-group"));
    }

    [Fact]
    public void A_category_with_no_groups_writes_only_itself()
    {
        var settings = new PluginSettings();

        ManifestConsent.SetRowConsent(Row("quests"), enabled: true, settings);

        Assert.True(settings.IsCategoryEnabled("quests"));
        Assert.Empty(settings.EnabledItemGroupKeys);
    }

    // A group is scanned only in its category's pass, so a group without its category is consent that
    // could never be honored.
    [Fact]
    public void Ticking_a_group_ticks_its_category()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });

        ManifestConsent.SetGroupConsent(row, "proofs", enabled: true, settings);

        Assert.True(settings.IsCategoryEnabled("items"));
        Assert.True(settings.IsItemGroupEnabled("proofs"));
        Assert.False(settings.IsItemGroupEnabled("materials"));
    }

    // Keeps "category on, every group off", which looks enabled and uploads nothing, out of reach by
    // hand.
    [Fact]
    public void Unticking_the_last_group_unticks_its_category()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs", enabled: true) }, userEnabled: true);
        settings.SetCategoryEnabled("items", true);
        settings.SetItemGroupEnabled("proofs", true);

        ManifestConsent.SetGroupConsent(row, "proofs", enabled: false, settings);

        Assert.False(settings.IsCategoryEnabled("items"));
    }

    // It still has a group to scan, and narrowing group by group is what these checkboxes are for.
    [Fact]
    public void Unticking_one_of_several_groups_leaves_the_category_on()
    {
        var settings = new PluginSettings();
        var row = Row("items", new[] { GroupRow("proofs"), GroupRow("materials") });
        ManifestConsent.SetRowConsent(row, enabled: true, settings);

        ManifestConsent.SetGroupConsent(row, "materials", enabled: false, settings);

        Assert.True(settings.IsCategoryEnabled("items"));
        Assert.True(settings.IsItemGroupEnabled("proofs"));
        Assert.False(settings.IsItemGroupEnabled("materials"));
    }

    // --- The "all collections" box ----------------------------------------------------------

    [Fact]
    public void All_collections_reads_as_ticked_only_when_every_row_and_group_is_on()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: true),
            Row("items", new[] { GroupRow("proofs", enabled: true) }, userEnabled: true),
        };

        Assert.True(ManifestConsent.AllConsentGiven(rows));
    }

    // One category off leaves the box unticked, even while another is on.
    [Fact]
    public void All_collections_reads_as_unticked_while_a_category_is_off()
    {
        var rows = new[] { Row("quests", userEnabled: false), Row("mounts", userEnabled: true) };

        Assert.False(ManifestConsent.AllConsentGiven(rows));
    }

    // The box promises every collection in full, so one group left off under a ticked category keeps
    // the box unticked.
    [Fact]
    public void All_collections_reads_as_unticked_while_a_group_is_off()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: true),
            Row("items", new[] { GroupRow("proofs", enabled: true), GroupRow("materials") },
                userEnabled: true),
        };

        Assert.False(ManifestConsent.AllConsentGiven(rows));
    }

    // A box seeded true with nothing under it would read as ticked while nothing is on, the one reading
    // a consent control must never give.
    [Fact]
    public void All_collections_never_reads_as_ticked_when_there_is_nothing_to_tick()
    {
        Assert.False(ManifestConsent.AllConsentGiven(Array.Empty<CategorySettingsRow>()));

        var serverDisabledOnly = new[] { Row("quests", userEnabled: false, serverEnabled: false) };
        Assert.False(ManifestConsent.AllConsentGiven(serverDisabledOnly));
    }

    // A server-off row is not the user's to answer and draws grayed, so it neither holds the box
    // unticked nor gets written by it.
    [Fact]
    public void A_server_disabled_row_does_not_hold_the_all_collections_box_unticked()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: true),
            Row("items", new[] { GroupRow("proofs") }, userEnabled: false, serverEnabled: false),
        };

        Assert.True(ManifestConsent.AllConsentGiven(rows));
    }

    // --- "The server asked, and the user has not agreed to answer" --------------------------
    // Decides whether a manifest-driven collection is scanned at all. Get it wrong and the plugin tells
    // the user it read a collection it never looked at.

    private static CollectContext ContextWith(
        IReadOnlyList<ItemManifestGroup>? groups, params string[] enabledGroupKeys) =>
        new()
        {
            RemoteConfig = new ConfigResponse
            {
                Categories = new Dictionary<string, bool>(),
                Enabled = true,
                Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
                ItemManifest = Array.Empty<uint>(),
                ItemManifestGroups = groups,
                ManifestVersion = "abc123",
            },
            EnabledItemGroupKeys = new HashSet<string>(enabledGroupKeys),
        };

    private static ItemManifestGroup Group(string key) =>
        new() { Key = key, Label = $"{key} label", Ids = new uint[] { 1, 2 } };

    [Fact]
    public void Groups_offered_and_none_enabled_means_there_is_nothing_to_scan()
    {
        var context = ContextWith(new[] { Group("proofs"), Group("materials") });

        Assert.True(ManifestConsent.GroupsOfferedButNoneEnabled(context));
    }

    [Fact]
    public void One_enabled_group_is_enough_to_scan()
    {
        var context = ContextWith(new[] { Group("proofs"), Group("materials") }, "materials");

        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(context));
    }

    // With no groups offered, the category checkbox alone gates the manifest, so no group consent can be
    // missing.
    [Fact]
    public void A_server_that_offers_no_groups_asks_nothing_of_the_user()
    {
        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(ContextWith(groups: null)));
        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(
            ContextWith(Array.Empty<ItemManifestGroup>())));
    }

    // No config is a different miss with its own line: CollectSkipReasons.NoRemoteConfig.
    [Fact]
    public void No_config_at_all_is_not_a_consent_problem()
    {
        Assert.False(ManifestConsent.GroupsOfferedButNoneEnabled(new CollectContext()));
    }

    // --- Whether the select-all has anything to act on ---------------------------------------

    [Fact]
    public void No_server_enabled_row_means_the_select_all_has_nothing_to_act_on()
    {
        var rows = new[] {Row("quests", serverEnabled: false), Row("mounts", serverEnabled: false)};

        Assert.False(ManifestConsent.AnyServerEnabled(rows));
    }

    [Fact]
    public void One_server_enabled_row_is_enough_for_the_select_all_to_act_on()
    {
        var rows = new[] {Row("quests", serverEnabled: false), Row("mounts", serverEnabled: true)};

        Assert.True(ManifestConsent.AnyServerEnabled(rows));
    }

    [Fact]
    public void An_empty_list_means_the_select_all_has_nothing_to_act_on()
    {
        Assert.False(ManifestConsent.AnyServerEnabled(Array.Empty<CategorySettingsRow>()));
    }

    // --- Whether the server has paused everything ---------------------------------------------

    [Fact]
    public void A_globally_off_row_reports_the_server_as_paused()
    {
        var rows = new[]
        {
            Row("quests", serverEnabled: false, serverGloballyOff: true),
            Row("mounts", serverEnabled: false, serverGloballyOff: true),
        };

        Assert.True(ManifestConsent.ServerHasPausedEverything(rows));
    }

    // The sync card's status line depends on this distinction.
    [Fact]
    public void Collections_switched_off_individually_are_not_a_pause()
    {
        var rows = new[] {Row("quests", serverEnabled: false), Row("mounts", serverEnabled: false)};

        Assert.False(ManifestConsent.ServerHasPausedEverything(rows));
    }

    [Fact]
    public void An_empty_list_is_not_a_pause()
    {
        Assert.False(ManifestConsent.ServerHasPausedEverything(Array.Empty<CategorySettingsRow>()));
    }

    // --- Whether the server permits no collection, without a pause --------------------------------

    // In this state the wizard's consent step explains why every box is grayed, as the settings
    // screen's status line does.
    [Fact]
    public void Collections_all_switched_off_individually_permit_none()
    {
        var rows = new[] {Row("quests", serverEnabled: false), Row("mounts", serverEnabled: false)};

        Assert.True(ManifestConsent.ServerPermitsNoCollection(rows));
    }

    // A pause has its own sentence, and an empty list is nobody's refusal.
    [Fact]
    public void A_pause_a_permitted_collection_or_an_empty_list_is_not_that_state()
    {
        var paused = new[] {Row("quests", serverEnabled: false, serverGloballyOff: true)};
        var onePermitted = new[] {Row("quests", serverEnabled: false), Row("mounts")};

        Assert.False(ManifestConsent.ServerPermitsNoCollection(paused));
        Assert.False(ManifestConsent.ServerPermitsNoCollection(onePermitted));
        Assert.False(ManifestConsent.ServerPermitsNoCollection(Array.Empty<CategorySettingsRow>()));
    }

    // --- Whether anything at all will be uploaded ----------------------------------------------

    // The cadence promise needs both the user and the server to permit a collection, and the server
    // knows nothing of the user's half.
    [Fact]
    public void A_collection_the_user_switched_off_does_not_count_as_running()
    {
        var rows = new[] {Row("quests", userEnabled: false, serverEnabled: true)};

        Assert.False(ManifestConsent.AnyEffectivelyOn(rows));
    }

    [Fact]
    public void A_collection_the_server_switched_off_does_not_count_as_running()
    {
        var rows = new[] {Row("quests", userEnabled: true, serverEnabled: false)};

        Assert.False(ManifestConsent.AnyEffectivelyOn(rows));
    }

    [Fact]
    public void One_collection_both_sides_permit_is_enough_to_be_running()
    {
        var rows = new[]
        {
            Row("quests", userEnabled: false, serverEnabled: true),
            Row("mounts", userEnabled: true, serverEnabled: true),
        };

        Assert.True(ManifestConsent.AnyEffectivelyOn(rows));
    }

}
