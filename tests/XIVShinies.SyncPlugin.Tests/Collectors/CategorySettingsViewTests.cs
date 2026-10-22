using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The settings half of the extensibility gate; CollectorRunnerTests holds the payload half. Adding a
// collection must be one new ICollector class, so a name-to-label table or a category special case in
// the settings window would break the "facewear" tests below.
public class CategorySettingsViewTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

    private const string UnknownCategory = "facewear";

    private sealed class FakeCollector : ICollector
    {
        public FakeCollector(
            string categoryKey, string displayName, string whatGetsSent, bool usesItemManifest = false)
        {
            CategoryKey = categoryKey;
            DisplayName = displayName;
            WhatGetsSent = whatGetsSent;
            UsesItemManifest = usesItemManifest;
        }

        public string CategoryKey { get; }

        public string DisplayName { get; }

        public string Section { get; init; } = "Fakes";

        public string WhatGetsSent { get; }

        public string? Details { get; init; }

        public bool UsesItemManifest { get; }

        public bool UploadsOnUnlock { get; init; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    private static ICollector Fake(string key) =>
        new FakeCollector(key, $"{key} display", $"what {key} sends");

    // Proves group rows attach by the collector's self-description, not by its category name.
    private static ICollector FakeManifestDriven(string key) =>
        new FakeCollector(key, $"{key} display", $"what {key} sends", usesItemManifest: true);

    private static PluginSettings OptedIn(params string[] categoryKeys)
    {
        var settings = new PluginSettings {MasterEnabled = true, OnboardingComplete = true};
        foreach (var key in categoryKeys)
            settings.SetCategoryEnabled(key, true);

        return settings;
    }

    private static ConfigResponse RemoteConfig(
        Dictionary<string, bool>? categories = null,
        IReadOnlyList<ItemManifestGroup>? itemManifestGroups = null,
        Dictionary<string, string>? categoryNotes = null,
        bool enabled = true) => new()
    {
        Categories = categories ?? new Dictionary<string, bool>(),
        Enabled = enabled,
        Intervals = new ConfigIntervals {FullSyncMinutes = 30, UnlockDebounceSeconds = 5},
        ItemManifest = Array.Empty<uint>(),
        ManifestVersion = "abc",
        ItemManifestGroups = itemManifestGroups,
        CategoryNotes = categoryNotes,
    };

    private static ItemManifestGroup Group(string key, string label) => new()
    {
        Key = key,
        Label = label,
        Ids = Array.Empty<uint>(),
    };

    // The gate itself: an unknown collector's own copy reaches its row untouched.
    [Fact]
    public void A_collector_for_an_unknown_category_appears_in_the_settings_list()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        var row = Assert.Single(rows);
        Assert.Equal(UnknownCategory, row.Key);
        Assert.Equal("facewear display", row.DisplayName);
        Assert.Equal("what facewear sends", row.WhatGetsSent);
        Assert.True(row.UserEnabled);

        // An unheard-of collection is one this install has never shown, so it wears the badge too.
        Assert.True(row.IsNew);
    }

    // Guards the polarity: inverted, every familiar collection would wear the badge forever, and
    // nothing else would catch it because IsNew has a default.
    [Fact]
    public void Category_rows_carry_the_installs_seen_state_per_category()
    {
        var settings = OptedIn();
        settings.MarkCategoriesSeen(new[] {"quests"});

        var rows = CategorySettingsView.Build(
            new[] {Fake("quests"), Fake(UnknownCategory)}, settings, RemoteConfig());

        Assert.False(rows[0].IsNew);
        Assert.True(rows[1].IsNew);
    }

    // The consent surfaces group rows by this section (see CategorySectionTests).
    [Fact]
    public void A_collectors_section_is_carried_onto_its_row()
    {
        var collector = new FakeCollector(UnknownCategory, "facewear display", "what facewear sends")
        {
            Section = "Glamour",
        };

        var row = Assert.Single(
            CategorySettingsView.Build(
                new[] {collector}, OptedIn(UnknownCategory), RemoteConfig()));

        Assert.Equal("Glamour", row.Section);
    }

    // The sync card reads this to decide what to promise without naming a collection.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_collectors_unlock_upload_flag_reaches_its_row(bool uploadsOnUnlock)
    {
        var collector = new FakeCollector(UnknownCategory, "Facewear", "what facewear sends")
        {
            UploadsOnUnlock = uploadsOnUnlock,
        };

        var row = Assert.Single(
            CategorySettingsView.Build(new[] {collector}, OptedIn(UnknownCategory), RemoteConfig()));

        Assert.Equal(uploadsOnUnlock, row.UploadsOnUnlock);
    }

    /// <summary>Leaves <c>UploadsOnUnlock</c> to the interface's default.</summary>
    private sealed class SilentCollector : ICollector
    {
        public string CategoryKey => UnknownCategory;

        public string DisplayName => "Facewear";

        public string Section => "Fakes";

        public string WhatGetsSent => "what facewear sends";

        public string? Details => null;

        public bool UsesItemManifest => false;

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    [Fact]
    public void A_collector_that_does_not_declare_the_flag_reaches_its_row_as_false()
    {
        var row = Assert.Single(
            CategorySettingsView.Build(
                new ICollector[] {new SilentCollector()}, OptedIn(UnknownCategory), RemoteConfig()));

        Assert.False(row.UploadsOnUnlock);
    }

    // The gate end to end across Build and GroupBySection, each pinned alone elsewhere: nothing
    // between them may normalize or default the section away.
    [Fact]
    public void An_unknown_collectors_section_becomes_its_own_heading_end_to_end()
    {
        var collector = new FakeCollector(UnknownCategory, "facewear display", "what facewear sends")
        {
            Section = "Glamour",
        };

        var sections = CategorySettingsView.GroupBySection(
            CategorySettingsView.Build(
                new[] {collector}, OptedIn(UnknownCategory), RemoteConfig()));

        var section = Assert.Single(sections);
        Assert.Equal("Glamour", section.Title);
        Assert.Equal(UnknownCategory, Assert.Single(section.Rows).Key);
    }

    // Looked up by category key like the skip reason, so the view names no category.
    [Fact]
    public void A_categorys_partial_note_is_carried_onto_its_row()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)},
            OptedIn(UnknownCategory),
            RemoteConfig(),
            lastSkipped: null,
            lastPartialNotes: new Dictionary<string, string> {[UnknownCategory] = "half read."});

        Assert.Equal("half read.", Assert.Single(rows).PartialNote);
    }

    [Fact]
    public void A_category_with_no_partial_note_leaves_the_row_without_one()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Null(Assert.Single(rows).PartialNote);
    }

    // The healthy chip's hover copy, looked up like the partial note.
    [Fact]
    public void A_categorys_chip_detail_is_carried_onto_its_row()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)},
            OptedIn(UnknownCategory),
            RemoteConfig(),
            lastCollectedDetails: new Dictionary<string, string>
            {
                [UnknownCategory] = "Optional hover copy.",
            });

        Assert.Equal("Optional hover copy.", Assert.Single(rows).CollectedDetail);
    }

    // Null must stay null: the window draws a hover marker only for a row with details, so an empty
    // string would put a question mark with nothing behind it on every row.
    [Fact]
    public void A_collectors_details_are_carried_onto_its_row()
    {
        var collector = new FakeCollector(UnknownCategory, "facewear display", "what facewear sends")
        {
            Details = "where facewear was looked for",
        };

        var row = Assert.Single(
            CategorySettingsView.Build(
                new[] {collector}, OptedIn(UnknownCategory), RemoteConfig()));

        Assert.Equal("where facewear was looked for", row.Details);
    }

    [Fact]
    public void A_collector_offering_no_details_leaves_the_row_without_any()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Null(Assert.Single(rows).Details);
    }

    [Fact]
    public void Rows_follow_registration_order()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake("b"), Fake("a")}, OptedIn(), RemoteConfig());

        Assert.Equal(new[] {"b", "a"}, rows.Select(row => row.Key));
    }

    [Fact]
    public void A_category_the_user_never_opted_into_is_off()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn(), RemoteConfig());

        Assert.False(rows[0].UserEnabled);
        Assert.False(rows[0].IsEffectivelyOn);
    }

    // The server's per-category kill switch keeps the user's choice beneath it, so switching the
    // category back on restores that choice instead of opting them out.
    [Fact]
    public void A_category_the_server_disabled_keeps_the_users_preference_but_is_not_effectively_on()
    {
        var config = RemoteConfig(new Dictionary<string, bool> {["a"] = false});

        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), config);

        Assert.True(rows[0].UserEnabled);
        Assert.False(rows[0].ServerEnabled);
        Assert.False(rows[0].IsEffectivelyOn);
    }

    // A config that could not be fetched forbids nothing, matching the collectors and the upload gate.
    [Fact]
    public void Without_a_config_every_category_reads_as_server_enabled()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), remoteConfig: null);

        Assert.True(rows[0].ServerEnabled);
        Assert.True(rows[0].IsEffectivelyOn);
    }

    // Lets the plugin ship a collector before the server has its switch.
    [Fact]
    public void A_category_the_server_does_not_know_reads_as_server_enabled()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(rows[0].ServerEnabled);
    }

    // How the "open your Achievements window once" hint reaches the UI without naming achievements:
    // the row carries the collector's reason verbatim.
    [Fact]
    public void A_skip_reason_from_the_last_pass_rides_along_with_its_category()
    {
        var skipped = new Dictionary<string, string> {[UnknownCategory] = "list_not_loaded"};

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory), Fake("a")}, OptedIn(), RemoteConfig(), skipped);

        Assert.Equal("list_not_loaded", rows[0].SkipReason);
        Assert.Null(rows[1].SkipReason);
    }

    [Fact]
    public void Before_the_first_collection_pass_no_row_carries_a_skip_reason()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), RemoteConfig());

        Assert.Null(rows[0].SkipReason);
    }

    [Fact]
    public void No_collectors_produces_no_rows()
    {
        Assert.Empty(CategorySettingsView.Build(
            Array.Empty<ICollector>(), OptedIn(), RemoteConfig()));
    }

    [Fact]
    public void A_manifest_driven_collector_gets_one_group_row_per_manifest_group_in_server_order()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("glamour-weapons", "Glamour weapons"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        // ReadStatusView keys on this flag: it hides a manifest-driven collection's read-status line
        // while a container line stands in for it.
        Assert.True(rows[0].UsesItemManifest);

        var groups = Assert.Single(rows).Groups;
        Assert.NotNull(groups);
        Assert.Equal(2, groups!.Count);
        Assert.Equal("glamour-weapons", groups[0].Key);
        Assert.Equal("Glamour weapons", groups[0].Label);
        Assert.Equal("relic-tools", groups[1].Key);
        Assert.Equal("Relic tools", groups[1].Label);
    }

    // An unknown group reads as new, and as off because the opt-in is an allowlist.
    [Fact]
    public void Group_rows_carry_the_users_enabled_and_seen_state_per_group()
    {
        var settings = OptedIn();
        settings.SetItemGroupEnabled("glamour-weapons", true);
        settings.MarkItemGroupsSeen(new[] {"glamour-weapons"});

        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("glamour-weapons", "Glamour weapons"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, settings, config);
        var groups = rows[0].Groups!;

        Assert.True(groups[0].Enabled);
        Assert.False(groups[0].IsNew);

        Assert.False(groups[1].Enabled);
        Assert.True(groups[1].IsNew);
    }

    // A switched-off collection cannot be used, so it does not announce itself yet. This is how a beta
    // gate looks: users outside the test group see a quiet grayed row, not a badge for something they
    // cannot turn on.
    [Fact]
    public void A_collection_the_server_switched_off_does_not_announce_itself()
    {
        var settings = OptedIn();
        var config = RemoteConfig(categories: new Dictionary<string, bool> {[UnknownCategory] = false});

        var rows = CategorySettingsView.Build(new[] {Fake(UnknownCategory)}, settings, config);
        var row = Assert.Single(rows);

        // Still unseen, but not announced while off.
        Assert.True(row.IsNew);
        Assert.False(row.ServerEnabled);
        Assert.False(row.IsEffectivelyNew);

        // Drawing it does not spend the announcement.
        Assert.False(row.WasDrawnAsUsable);
        Assert.False(row.ShowingItRetiresTheBadge);
    }

    // Once the server has answered and permits the collection, showing the row spends the
    // announcement.
    [Fact]
    public void A_collection_announces_itself_once_the_server_switches_it_on()
    {
        var settings = OptedIn();

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, RemoteConfig());

        var row = Assert.Single(rows);
        Assert.True(row.IsEffectivelyNew);

        Assert.True(row.ServerStateKnown);
        Assert.True(row.WasDrawnAsUsable);
        Assert.True(row.ShowingItRetiresTheBadge);
    }

    // DrawGroupCheckboxes relies on this: a group is never drawn before the server has answered.
    [Fact]
    public void A_manifest_driven_collector_has_no_group_rows_before_the_server_answers()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn(), remoteConfig: null);

        Assert.Null(Assert.Single(rows).Groups);
    }

    // The folded header's chip skips a collection the server switched off, as the row's badge does, so
    // the user never opens it to find nothing new.
    [Fact]
    public void The_header_chip_ignores_a_collection_the_server_switched_off()
    {
        var settings = OptedIn();
        var config = RemoteConfig(categories: new Dictionary<string, bool> {[UnknownCategory] = false});
        var none = new HashSet<string>();

        var rows = CategorySettingsView.Build(new[] {Fake(UnknownCategory)}, settings, config);

        Assert.False(CategorySettingsView.AnythingIsNew(rows, none, none));
    }

    // One level down: a switched-off collection's groups are unusable too, so they must not light the
    // header chip either.
    [Fact]
    public void The_header_chip_ignores_the_groups_of_a_collection_the_server_switched_off()
    {
        var settings = OptedIn();
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {["items"] = false},
            itemManifestGroups: new[] {Group("relic-tools", "Relic tools")});
        var none = new HashSet<string>();

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, settings, config);

        // The group is unseen, so the parent's state is what keeps the chip dark.
        Assert.True(rows[0].Groups![0].IsNew);
        Assert.False(CategorySettingsView.AnythingIsNew(rows, none, none));
    }

    // Before the first /config answers, the server's permission is assumed. The badge shows, so a user
    // whose poll fails still learns the collection exists, but drawing it must not retire it, or a
    // collection gated for them would be announced to nobody.
    [Fact]
    public void A_row_drawn_before_the_server_answers_is_shown_but_not_retired()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(), remoteConfig: null);

        var row = Assert.Single(rows);
        Assert.True(row.ServerEnabled);          // assumed, so the row draws normally
        Assert.False(row.ServerStateKnown);      // but it is only an assumption
        Assert.True(row.IsEffectivelyNew);       // so the badge shows
        Assert.False(row.ShowingItRetiresTheBadge); // and showing it costs nothing
    }

    // Pins the seen half of IsEffectivelyNew, so it cannot shrink to ServerEnabled alone.
    [Fact]
    public void A_collection_already_shown_is_not_new_even_when_the_server_permits_it()
    {
        var settings = OptedIn();
        settings.MarkCategoriesSeen(new[] {UnknownCategory});

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, RemoteConfig());

        var row = Assert.Single(rows);
        Assert.True(row.ServerEnabled);
        Assert.False(row.IsEffectivelyNew);
    }

    // With the "Collections" header folded, its chip is the only sign that something arrived, so it
    // answers for collections and groups alike.
    [Fact]
    public void The_header_chip_answers_for_a_new_collection_a_new_group_and_neither()
    {
        var settings = OptedIn();
        var config = RemoteConfig(itemManifestGroups: new[] {Group("relic-tools", "Relic tools")});
        var none = new HashSet<string>();

        // A collection this install has never shown.
        var freshCategory = CategorySettingsView.Build(new[] {Fake("quests")}, settings, RemoteConfig());
        Assert.True(CategorySettingsView.AnythingIsNew(freshCategory, none, none));

        // Nothing new: the collection has been shown, and it carries no groups.
        settings.MarkCategoriesSeen(new[] {"quests"});
        var seenCategory = CategorySettingsView.Build(new[] {Fake("quests")}, settings, RemoteConfig());
        Assert.False(CategorySettingsView.AnythingIsNew(seenCategory, none, none));

        // A group inside an already-shown collection still raises it.
        settings.MarkCategoriesSeen(new[] {"items"});
        var freshGroup = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, settings, config);
        Assert.True(CategorySettingsView.AnythingIsNew(freshGroup, none, none));
    }

    // Drawing a row saves its seen flag at once, so without the session's badge set the chip would go
    // dark one frame after the badge beneath it appeared.
    [Fact]
    public void The_header_chip_stays_lit_for_a_badge_already_shown_this_session()
    {
        var settings = OptedIn();
        settings.MarkCategoriesSeen(new[] {"quests"});

        var rows = CategorySettingsView.Build(new[] {Fake("quests")}, settings, RemoteConfig());
        var badgedCategories = new HashSet<string> {"quests"};
        var none = new HashSet<string>();

        Assert.False(rows[0].IsNew);
        Assert.True(CategorySettingsView.AnythingIsNew(rows, badgedCategories, none));
    }

    // Null, not empty, so the window can tell "no groups yet" from "the server sent an empty list".
    [Fact]
    public void A_manifest_driven_collector_has_no_group_rows_when_the_config_carries_no_groups()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn(), RemoteConfig());

        Assert.Null(rows[0].Groups);
    }

    // The other side of null versus empty: a non-null list tells callers the server sent groups, even
    // with none to draw.
    [Fact]
    public void An_empty_group_list_in_the_config_yields_empty_group_rows_not_null()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")},
            OptedIn(),
            RemoteConfig(itemManifestGroups: Array.Empty<ItemManifestGroup>()));

        Assert.NotNull(rows[0].Groups);
        Assert.Empty(rows[0].Groups!);
    }

    // The collector's flag decides group rows, not the config: the server's groups belong to whichever
    // collection announced itself manifest-driven.
    [Fact]
    public void A_collector_that_is_not_manifest_driven_never_gets_group_rows()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("glamour-weapons", "Glamour weapons")});

        var rows = CategorySettingsView.Build(new[] {Fake("items")}, OptedIn(), config);

        Assert.False(rows[0].UsesItemManifest);
        Assert.Null(rows[0].Groups);
    }

    // No later path can handle a blank group key: consent reads treat it as off, seen-marking skips it,
    // leaving a "New" badge that re-saves the config every frame, and the consent write throws. So the
    // view drops it at the boundary.
    [Fact]
    public void A_group_with_a_blank_key_is_dropped_and_its_siblings_survive()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("", "Broken group"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        var group = Assert.Single(rows[0].Groups!);
        Assert.Equal("relic-tools", group.Key);
    }

    // A key is a consent identity, so it is dropped, never shortened; BuildGroupRows explains why.
    [Fact]
    public void A_group_with_an_unreasonably_long_key_is_dropped_and_its_siblings_survive()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group(new string('k', 5_000), "Overlong key"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        var group = Assert.Single(rows[0].Groups!);
        Assert.Equal("relic-tools", group.Key);
    }

    [Fact]
    public void A_group_with_an_unreasonably_long_label_keeps_the_group_and_shortens_the_label()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("relic-tools", new string('l', ServerText.MaxAdoptedLength + 400)),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        var group = Assert.Single(rows[0].Groups!);
        Assert.Equal("relic-tools", group.Key);
        Assert.Equal(new string('l', ServerText.MaxAdoptedLength) + "...", group.Label);
    }

    // A blank label would draw a checkbox with nothing beside it; the key at least names the consent
    // being offered.
    [Fact]
    public void A_group_with_a_blank_label_falls_back_to_its_key()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("relic-tools", "   ")});

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        Assert.Equal("relic-tools", Assert.Single(rows[0].Groups!).Label);
    }

    // The gate for group rows: they attach by the collector's flag, never by a category name.
    [Fact]
    public void A_manifest_driven_collector_for_an_unknown_category_still_gets_group_rows()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("mystery-group", "Mystery group")});

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven(UnknownCategory)}, OptedIn(UnknownCategory), config);

        var groups = Assert.Single(rows).Groups;
        Assert.NotNull(groups);
        var group = Assert.Single(groups!);
        Assert.Equal("mystery-group", group.Key);
    }

    // --- What a switched-off collection does to its groups --------------------------------

    [Fact]
    public void A_group_under_a_server_disabled_collection_is_not_effectively_on()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {["items"] = false},
            itemManifestGroups: new[] {Group("relic-tools", "Relic tools")});

        var settings = OptedIn();
        settings.SetItemGroupEnabled("relic-tools", true);

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, settings, config);

        var group = Assert.Single(rows[0].Groups!);

        // The user's consent survives; only the effective answer changes.
        Assert.True(group.Enabled);
        Assert.False(group.ParentServerEnabled);
        Assert.False(group.IsEffectivelyOn);
    }

    [Fact]
    public void A_group_under_a_permitted_collection_is_effectively_on_when_the_user_enabled_it()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {["items"] = true},
            itemManifestGroups: new[] {Group("relic-tools", "Relic tools")});

        var settings = OptedIn();
        settings.SetItemGroupEnabled("relic-tools", true);

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, settings, config);

        Assert.True(Assert.Single(rows[0].Groups!).IsEffectivelyOn);
    }

    // The parent's answer can only take a group away, never grant one.
    [Fact]
    public void A_group_the_user_declined_is_not_effectively_on_under_a_permitted_collection()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {["items"] = true},
            itemManifestGroups: new[] {Group("relic-tools", "Relic tools")});

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn(), config);

        Assert.False(Assert.Single(rows[0].Groups!).IsEffectivelyOn);
    }

    // --- What a switched-off category says for itself -------------------------------------------
    // A category is off for one of two reasons, and the row draws a different sentence for each. The
    // fake is a category nobody wrote code for, so a future gated collection gets this without an edit.

    // A category in testing prints the server's note verbatim, since "it is off" alone reads as broken.
    [Fact]
    public void A_server_note_is_what_a_switched_off_category_says()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false},
            categoryNotes: new Dictionary<string, string>
            {
                [UnknownCategory] = "In testing — it will switch on for everyone once it is ready.",
            });

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config);

        Assert.Equal(
            "In testing — it will switch on for everyone once it is ready.",
            Assert.Single(rows).ServerOffText(Host));
    }

    // The kill switch carries no note: the collection is off for everyone, usually because something
    // is wrong, and a specific line would be a guess made on the server's behalf.
    [Fact]
    public void A_switched_off_category_without_a_note_falls_back_to_the_generic_line()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false});

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config);

        // Pinned against ServerOffCopy.Feature itself, so a reword moves both surfaces that draw it.
        Assert.Equal(
            ServerOffCopy.Feature(Host), Assert.Single(rows).ServerOffText(Host));
    }

    [Fact]
    public void The_server_note_is_carried_on_the_row()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false},
            categoryNotes: new Dictionary<string, string> {[UnknownCategory] = "In testing."});

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config);

        Assert.Equal("In testing.", Assert.Single(rows).ServerNote);
    }

    // A note on a live category is legal but has no grayed row to explain. ServerOffText returns null
    // for any enabled category, so the note is dropped without a special case.
    [Fact]
    public void An_enabled_category_says_nothing_even_when_a_note_came_with_it()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = true},
            categoryNotes: new Dictionary<string, string>
            {
                [UnknownCategory] = "A note the panel has no place to draw.",
            });

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config);

        Assert.Null(Assert.Single(rows).ServerOffText(Host));
    }

    [Fact]
    public void An_enabled_category_says_nothing()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Null(Assert.Single(rows).ServerOffText(Host));
    }

    // An unfetched config forbids nothing. Otherwise every failed /config fetch would tell the user
    // XIV Shinies switched their collections off.
    [Fact]
    public void A_category_says_nothing_before_the_config_arrives()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), remoteConfig: null);

        Assert.Null(Assert.Single(rows).ServerOffText(Host));
    }

    // --- Which mark a row wears -----------------------------------------------------------------
    // BadgeFor picks "Off", "New" or nothing. Rows come from the real Build, so the inputs match what
    // the window passes.

    private static CategorySettingsRow UnseenRow(bool serverEnabled)
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = serverEnabled});

        // OptedIn marks nothing seen, so the row arrives new.
        return Assert.Single(
            CategorySettingsView.Build(
                new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config));
    }

    // A collection can be both new and switched off, and the badge must not invite the user toward
    // something they cannot use.
    [Fact]
    public void A_switched_off_row_wears_Off_even_when_it_is_also_new()
    {
        var row = UnseenRow(serverEnabled: false);

        Assert.True(row.IsNew);
        Assert.Equal(
            CategoryBadgeKind.Off,
            CategorySettingsView.BadgeFor(row, showNewChips: true, badgedThisSession: true));
    }

    [Fact]
    public void A_new_row_the_server_allows_wears_New()
    {
        Assert.Equal(
            CategoryBadgeKind.New,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: true), showNewChips: true, badgedThisSession: true));
    }

    // The first-run wizard shows every collection, so it announces none.
    [Fact]
    public void A_surface_that_does_not_badge_wears_no_New_mark()
    {
        Assert.Equal(
            CategoryBadgeKind.None,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: true), showNewChips: false, badgedThisSession: true));
    }

    // "Off" is not a badging decision: a wizard user still needs the reason a row cannot be ticked.
    [Fact]
    public void A_surface_that_does_not_badge_still_shows_the_Off_mark()
    {
        Assert.Equal(
            CategoryBadgeKind.Off,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: false), showNewChips: false, badgedThisSession: false));
    }

    [Fact]
    public void A_row_with_nothing_to_announce_wears_nothing()
    {
        var settings = OptedIn(UnknownCategory);
        settings.MarkCategoriesSeen(new[] {UnknownCategory});

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, RemoteConfig()));

        Assert.Equal(
            CategoryBadgeKind.None,
            CategorySettingsView.BadgeFor(row, showNewChips: true, badgedThisSession: false));
    }

    // A never-shown row badges on its own flag, without the caller's session record.
    [Fact]
    public void A_new_row_wears_New_before_the_window_records_anything()
    {
        Assert.Equal(
            CategoryBadgeKind.New,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: true), showNewChips: true, badgedThisSession: false));
    }

    // --- When a drawing retires the announcement ------------------------------------------------
    // ShowingRetiresTheBadge decides which drawings count as seen; the rule differs per surface.

    private static CategorySettingsRow UnseenRowWithNoConfig() =>
        Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), remoteConfig: null));

    // A badging surface waits for the server's answer: spending the badge on an assumption could lose
    // the announcement outright.
    [Fact]
    public void A_badging_surface_does_not_retire_on_an_unanswered_config()
    {
        Assert.False(
            CategorySettingsView.ShowingRetiresTheBadge(UnseenRowWithNoConfig(), showNewChips: true));
    }

    // The wizard's purpose is to show every collection, so a failed config poll must not stop it
    // recording what it showed.
    [Fact]
    public void A_non_badging_surface_retires_even_on_an_unanswered_config()
    {
        Assert.True(
            CategorySettingsView.ShowingRetiresTheBadge(UnseenRowWithNoConfig(), showNewChips: false));
    }

    // Grayed and unusable is not an introduction, on either surface.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_server_disabled_row_never_retires_its_announcement(bool showNewChips)
    {
        Assert.False(
            CategorySettingsView.ShowingRetiresTheBadge(UnseenRow(serverEnabled: false), showNewChips));
    }

    // Reporting a seen row would only make the caller save the config every frame.
    [Fact]
    public void A_row_already_seen_never_retires()
    {
        var settings = OptedIn(UnknownCategory);
        settings.MarkCategoriesSeen(new[] {UnknownCategory});

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, RemoteConfig()));

        Assert.False(CategorySettingsView.ShowingRetiresTheBadge(row, showNewChips: true));
    }

    // --- What the group list refuses to build ---------------------------------------------------

    // The list is rebuilt every frame the settings window is open, so the server cannot choose its
    // size. No honest manifest comes near the cap.
    [Fact]
    public void A_group_list_is_capped_however_many_groups_the_server_sends()
    {
        var groups = new List<ItemManifestGroup>();
        for (var i = 0; i < 150; i++)
            groups.Add(Group($"group-{i}", $"Group {i}"));

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn("items"), RemoteConfig(itemManifestGroups: groups));

        Assert.Equal(100, Assert.Single(rows).Groups!.Count);
    }

    // The raw key stays the consent identity, but drawn text never carries invisible formatting.
    [Fact]
    public void A_groups_key_standing_in_for_its_label_is_folded_before_it_is_drawn()
    {
        var config = RemoteConfig(
            itemManifestGroups: new[] {Group("a‮b", " ")});

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn("items"), config);

        var row = Assert.Single(Assert.Single(rows).Groups!);
        Assert.Equal("a‮b", row.Key);
        Assert.Equal("ab", row.Label);
    }

    // A key with no visible characters is as malformed as a blank one: a checkbox with an invisible
    // label is not a consent control.
    [Fact]
    public void A_group_whose_key_and_label_both_fold_to_nothing_is_dropped()
    {
        var config = RemoteConfig(
            itemManifestGroups: new[] {Group("​‮", " "), Group("real", "Real")});

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn("items"), config);

        Assert.Equal("real", Assert.Single(Assert.Single(rows).Groups!).Key);
    }

    // The server's two switches are independent: a pause arrives with the per-category map still
    // saying yes, so a row read from the map alone would promise a collection that cannot run.
    [Fact]
    public void A_paused_server_switches_every_collection_off()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = true},
            enabled: false);

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config));

        Assert.False(row.ServerEnabled);
        Assert.False(row.IsEffectivelyOn);
        Assert.True(row.ServerGloballyOff);
    }

    // The pause outranks a per-category note; CategorySettingsRow.ServerOffText holds why.
    [Fact]
    public void A_paused_server_explains_itself_rather_than_blaming_the_collection()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false},
            categoryNotes: new Dictionary<string, string> {[UnknownCategory] = "In testing."},
            enabled: false);

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config));

        Assert.Equal(ServerOffCopy.Paused(Host), row.ServerOffText(Host));
    }

    // A collection must not spend its one-time introduction during a pause: the user would see the
    // badge, find the row unusable, and never hear of it again.
    [Fact]
    public void A_paused_server_withholds_the_new_badge()
    {
        var settings = OptedIn(UnknownCategory);
        var config = RemoteConfig(enabled: false);

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, config));

        Assert.True(row.IsNew);
        Assert.False(row.IsEffectivelyNew);
        Assert.False(row.WasDrawnAsUsable);
        Assert.False(row.ShowingItRetiresTheBadge);
    }

    // Every checkbox clearing at once would look like the plugin discarded the user's choices, so the
    // row reports what they chose and nothing is written back.
    [Fact]
    public void A_paused_server_leaves_the_stored_preference_intact()
    {
        var settings = OptedIn(UnknownCategory);
        var config = RemoteConfig(enabled: false);

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, config));

        Assert.True(row.UserEnabled);
        Assert.True(settings.IsCategoryEnabled(UnknownCategory));
    }

    // Guards against the pause swallowing the per-category case.
    [Fact]
    public void A_collection_switched_off_on_its_own_still_says_so()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false},
            categoryNotes: new Dictionary<string, string> {[UnknownCategory] = "In testing."});

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config));

        Assert.False(row.ServerGloballyOff);
        Assert.Equal("In testing.", row.ServerOffText(Host));
    }

    // Group checkboxes follow their parent rather than deriving their own state, so a pause reaches
    // them too.
    [Fact]
    public void A_paused_server_switches_the_group_checkboxes_off_too()
    {
        var config = RemoteConfig(
            itemManifestGroups: new[] {Group("relic-proofs", "Relic proofs")},
            enabled: false);

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn("items"), config));

        var group = Assert.Single(row.Groups!);
        Assert.False(group.ParentServerEnabled);
        Assert.False(group.IsEffectivelyOn);
    }

    // With both switches off, the pause speaks: it explains every other collection on screen too.
    [Fact]
    public void A_paused_server_outranks_a_switched_off_collection_with_no_note()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false},
            enabled: false);

        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config));

        Assert.Equal(ServerOffCopy.Paused(Host), row.ServerOffText(Host));
    }

    // An unfetched config forbids nothing, so a plugin that cannot reach /config must not draw every
    // collection as paused.
    [Fact]
    public void An_unfetched_config_does_not_read_as_a_pause()
    {
        var row = Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), remoteConfig: null));

        Assert.True(row.ServerEnabled);
        Assert.False(row.ServerGloballyOff);
        Assert.Null(row.ServerOffText(Host));
    }
}
