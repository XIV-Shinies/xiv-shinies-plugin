using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The settings half of the extensibility gate. The payload half is covered by CollectorRunnerTests.
// Together they enforce the project rule that adding a collection is one new ICollector class: if the
// settings window had a name-to-label table, or special-cased any category, "facewear" below would
// not survive the trip.
public class CategorySettingsViewTests
{
    // A category this plugin has never heard of, deliberately.
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

        // Set per-test, so a fake can carry a named section; every other test leaves the shared
        // default.
        public string Section { get; init; } = "Fakes";

        public string WhatGetsSent { get; }

        // Set per-test, so the view's carry-through can be checked both ways: a collector that
        // offers elaboration and one that does not.
        public string? Details { get; init; }

        public bool UsesItemManifest { get; }

        public CollectResult Collect(CollectContext context) => CollectResult.Ids(new uint[] {1});
    }

    private static ICollector Fake(string key) =>
        new FakeCollector(key, $"{key} display", $"what {key} sends");

    // A collector that announces itself as manifest-driven, the same way ItemCollector does. Used to
    // prove group rows attach via self-description rather than a check on the category's name.
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
        Dictionary<string, string>? categoryNotes = null) => new()
    {
        Categories = categories ?? new Dictionary<string, bool>(),
        Enabled = true,
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

    // THE GATE: a collector for an unknown category renders with its own copy, untouched.
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

        // The badge is part of the gate: a collection the plugin has never heard of is one this
        // install has never shown, so it announces itself like any other.
        Assert.True(row.IsNew);
    }

    // The category-level twin of Group_rows_carry_the_users_enabled_and_seen_state_per_group. The
    // polarity is what matters — inverting it would badge every familiar collection forever, and
    // IsNew is defaulted rather than required, so nothing else would catch it.
    [Fact]
    public void Category_rows_carry_the_installs_seen_state_per_category()
    {
        var settings = OptedIn();
        settings.MarkCategoriesSeen(new[] {"quests"});
        // "facewear" is left untouched: never marked seen.

        var rows = CategorySettingsView.Build(
            new[] {Fake("quests"), Fake(UnknownCategory)}, settings, RemoteConfig());

        Assert.False(rows[0].IsNew);
        Assert.True(rows[1].IsNew);
    }

    // The section heading is self-description like the display name, and the grouping the consent
    // surfaces draw (see CategorySectionTests) is only as good as this carry-through.
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

    // The extensibility gate, end to end: an unknown collector declaring an unheard-of section
    // flows through Build AND GroupBySection into its own heading in one trip. The two links are
    // pinned individually elsewhere; this is the assertion that nothing between them normalizes
    // or defaults the section away.
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

    // The partial phrase rides the same no-name-branch route as the skip reason: keyed lookup,
    // null when the category's last read was whole.
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

    // The healthy-chip hover copy rides the same keyed route as the partial note.
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

    // The hover elaboration is optional self-description, carried through like the rest of the
    // copy. Null must survive as null: the window draws no hover affordance for a category whose
    // one-liner already says everything, so inventing an empty string here would put a question
    // mark on every row with nothing behind it.
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

    // A fresh install has opted into nothing.
    [Fact]
    public void A_category_the_user_never_opted_into_is_off()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn(), RemoteConfig());

        Assert.False(rows[0].UserEnabled);
        Assert.False(rows[0].IsEffectivelyOn);
    }

    // The server's per-category kill switch. The user's own preference is preserved beneath it, so
    // flipping the switch back on restores what they chose rather than silently opting them out.
    [Fact]
    public void A_category_the_server_disabled_keeps_the_users_preference_but_is_not_effectively_on()
    {
        var config = RemoteConfig(new Dictionary<string, bool> {["a"] = false});

        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), config);

        Assert.True(rows[0].UserEnabled);
        Assert.False(rows[0].ServerEnabled);
        Assert.False(rows[0].IsEffectivelyOn);
    }

    // A config we could not fetch forbids nothing. Showing every category as server-disabled would be
    // a lie, and would match neither the collectors nor the upload gate.
    [Fact]
    public void Without_a_config_every_category_reads_as_server_enabled()
    {
        var rows = CategorySettingsView.Build(new[] {Fake("a")}, OptedIn("a"), remoteConfig: null);

        Assert.True(rows[0].ServerEnabled);
        Assert.True(rows[0].IsEffectivelyOn);
    }

    // A category the server has never heard of reads as enabled, so a plugin can ship a collector
    // before the server grows the matching switch.
    [Fact]
    public void A_category_the_server_does_not_know_reads_as_server_enabled()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.True(rows[0].ServerEnabled);
    }

    // How the "open your Achievements window once" hint reaches the UI without anyone naming
    // achievements. The collector reported a reason; the row carries it verbatim.
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

    // A manifest-driven collector paired with a config that carries groups gets one row per group,
    // in the server's own order, carrying that group's own key and label.
    [Fact]
    public void A_manifest_driven_collector_gets_one_group_row_per_manifest_group_in_server_order()
    {
        var config = RemoteConfig(itemManifestGroups: new[]
        {
            Group("glamour-weapons", "Glamour weapons"),
            Group("relic-tools", "Relic tools"),
        });

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        // The collector's self-reported manifest flag rides through to the row. It is the seam the views
        // key their manifest-driven behavior on: ReadStatusView suppresses such a collection's
        // read-status line while a container line stands in for it.
        Assert.True(rows[0].UsesItemManifest);

        var groups = Assert.Single(rows).Groups;
        Assert.NotNull(groups);
        Assert.Equal(2, groups!.Count);
        Assert.Equal("glamour-weapons", groups[0].Key);
        Assert.Equal("Glamour weapons", groups[0].Label);
        Assert.Equal("relic-tools", groups[1].Key);
        Assert.Equal("Relic tools", groups[1].Label);
    }

    // Enabled mirrors the user's per-group opt-in (an allowlist, so an unknown group reads as off).
    // IsNew mirrors whether the settings UI has shown that key before (an unknown group reads as new).
    [Fact]
    public void Group_rows_carry_the_users_enabled_and_seen_state_per_group()
    {
        var settings = OptedIn();
        settings.SetItemGroupEnabled("glamour-weapons", true);
        settings.MarkItemGroupsSeen(new[] {"glamour-weapons"});
        // "relic-tools" is left untouched: never enabled, never marked seen.

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

    // A collection the server has switched off cannot be used, so it does not announce itself yet.
    // This is what a beta gate looks like from the plugin's side: the server sends the category
    // disabled for everyone outside the test group, and they see a quiet greyed row rather than a
    // badge pointing at something they cannot turn on.
    [Fact]
    public void A_collection_the_server_switched_off_does_not_announce_itself()
    {
        var settings = OptedIn();
        var config = RemoteConfig(categories: new Dictionary<string, bool> {[UnknownCategory] = false});

        var rows = CategorySettingsView.Build(new[] {Fake(UnknownCategory)}, settings, config);
        var row = Assert.Single(rows);

        // Never shown by this install, so it stays unseen — but not announced while it is off.
        Assert.True(row.IsNew);
        Assert.False(row.ServerEnabled);
        Assert.False(row.IsEffectivelyNew);

        // And drawing it does not spend the announcement.
        Assert.False(row.WasDrawnAsUsable);
        Assert.False(row.ShowingItRetiresTheBadge);
    }

    // The permitted case: the server has answered and allows the collection, so the row announces
    // itself, and showing it is what spends the announcement.
    [Fact]
    public void A_collection_announces_itself_once_the_server_switches_it_on()
    {
        var settings = OptedIn();

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, settings, RemoteConfig());

        var row = Assert.Single(rows);
        Assert.True(row.IsEffectivelyNew);

        // The mainline: the server has answered, it permits the collection, so showing the row is
        // the introduction and spends it.
        Assert.True(row.ServerStateKnown);
        Assert.True(row.WasDrawnAsUsable);
        Assert.True(row.ShowingItRetiresTheBadge);
    }

    // The invariant DrawGroupCheckboxes' correctness rests on: group rows exist only once a config
    // has been fetched, so a group can never be drawn in the "server has not answered" state.
    [Fact]
    public void A_manifest_driven_collector_has_no_group_rows_before_the_server_answers()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn(), remoteConfig: null);

        Assert.Null(Assert.Single(rows).Groups);
    }

    // The folded header follows the same rule, so a server-disabled collection cannot raise a chip
    // on a header the user then opens to find nothing new.
    [Fact]
    public void The_header_chip_ignores_a_collection_the_server_switched_off()
    {
        var settings = OptedIn();
        var config = RemoteConfig(categories: new Dictionary<string, bool> {[UnknownCategory] = false});
        var none = new HashSet<string>();

        var rows = CategorySettingsView.Build(new[] {Fake(UnknownCategory)}, settings, config);

        Assert.False(CategorySettingsView.AnythingIsNew(rows, none, none));
    }

    // The same, one level down. A switched-off collection's GROUPS are equally unusable, so they
    // must not raise the header chip either — without this the chip would still light for a
    // manifest-driven collection the server has switched off.
    [Fact]
    public void The_header_chip_ignores_the_groups_of_a_collection_the_server_switched_off()
    {
        var settings = OptedIn();
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {["items"] = false},
            itemManifestGroups: new[] {Group("relic-tools", "Relic tools")});
        var none = new HashSet<string>();

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, settings, config);

        // The group really is unseen — the chip is suppressed by the parent's state, not because
        // there was nothing to announce.
        Assert.True(rows[0].Groups![0].IsNew);
        Assert.False(CategorySettingsView.AnythingIsNew(rows, none, none));
    }

    // Before the first /config answers, the server's permission is an assumption. The badge still
    // shows — someone whose poll is failing should still learn a collection exists — but drawing it
    // must not retire it, or a collection gated for them would be announced to nobody.
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

    // Pins both conjuncts of the predicate: a seen row is not new even when the server permits it,
    // so IsEffectivelyNew cannot degenerate into ServerEnabled alone.
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

    // The folded "Collections" header's chip. With the header shut, none of the per-row badges are
    // visible, so this is the only thing telling a user something arrived — it has to answer for
    // both levels, and for a badge that went up earlier in the session.
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

    // A badge that went up earlier this session keeps the header chip lit even though drawing the
    // row already persisted its seen flag — otherwise the chip would blink out one frame after the
    // badge beneath it appeared.
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

    // No groups in the config means nothing for the UI to draw, even for a collector that announces
    // itself as manifest-driven — Groups is null, not an empty list, so the window can tell "no groups
    // yet" apart from "server sent a group list with nothing in it".
    [Fact]
    public void A_manifest_driven_collector_has_no_group_rows_when_the_config_carries_no_groups()
    {
        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn(), RemoteConfig());

        Assert.Null(rows[0].Groups);
    }

    // The other side of the null-vs-empty distinction: a group list that is PRESENT but empty
    // yields an empty (non-null) row list. Callers checking null get "the server sent groups",
    // even though there is nothing in them to draw.
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

    // The flag gates the feature, not the presence of groups in the config. A collector that never
    // announced itself as manifest-driven gets no group rows even when the config carries some.
    [Fact]
    public void A_collector_that_is_not_manifest_driven_never_gets_group_rows()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("glamour-weapons", "Glamour weapons")});

        var rows = CategorySettingsView.Build(new[] {Fake("items")}, OptedIn(), config);

        // Not manifest-driven: the flag is false, so no group rows attach — the server's groups belong to
        // whichever collection announced itself, and this one did not.
        Assert.False(rows[0].UsesItemManifest);
        Assert.Null(rows[0].Groups);
    }

    // A blank group key is malformed server data that no downstream path can handle: consent reads
    // treat it as off, seen-marking skips it (a forever-"New" badge that would re-save the config
    // every frame), and the consent write throws. The view drops it at the boundary; its healthy
    // siblings still flow.
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

    // Pins that an unreasonably long key takes the whole group out rather than being shortened,
    // and that its healthy siblings still flow. BuildGroupRows explains why dropping is the only
    // safe answer for a consent identity.
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

    // The opposite disposal from the key above: the group survives and only its label is cut.
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

    // A label the server left blank would draw a checkbox with nothing beside it, which the user
    // cannot act on. The key stands in — not pretty, but it names the consent being offered.
    [Fact]
    public void A_group_with_a_blank_label_falls_back_to_its_key()
    {
        var config = RemoteConfig(itemManifestGroups: new[] {Group("relic-tools", "   ")});

        var rows = CategorySettingsView.Build(new[] {FakeManifestDriven("items")}, OptedIn(), config);

        Assert.Equal("relic-tools", Assert.Single(rows[0].Groups!).Label);
    }

    // THE GATE, extended: a manifest-driven collector for a category this plugin has never heard of
    // still gets group rows built from the config, proving group attachment is gated on the
    // self-reported flag rather than on any category name.
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

        // The user's own consent is intact — only the effective answer changed.
        Assert.True(group.Enabled);
        Assert.False(group.ParentServerEnabled);
        Assert.False(group.IsEffectivelyOn);
    }

    // With the collection permitted, a group's effective state is simply its own consent.
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

    // A group the user has not consented to is off whatever the server says — the parent's answer
    // can only ever take a group away, never grant one.
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
    // Two unlike reasons a category can be off, and the row draws a different sentence for each.
    // Generic like everything else here: the fake announces a category nobody wrote code for, so a
    // future gated collection gets this behaviour without an edit.

    // A category still being tested: the server explains it, and the plugin prints that verbatim.
    // "It is off" alone would invite the reader to conclude something is broken.
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
            Assert.Single(rows).ServerOffText);
    }

    // The kill switch is the louder signal and carries no note: the collection is off for
    // everyone, usually because something is wrong. The generic line keeps its job there, where a
    // specific explanation would be a guess made on the server's behalf.
    [Fact]
    public void A_switched_off_category_without_a_note_falls_back_to_the_generic_line()
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = false});

        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config);

        // Pinned by identity with the constant, so a reword moves both surfaces that draw it.
        Assert.Equal(
            CategorySettingsRow.ServerOffFallback, Assert.Single(rows).ServerOffText);
    }

    // The note is carried on the row in its own right, not only folded into the sentence.
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

    // A note against a live category is legal and has nowhere to go — there is no greyed row to
    // explain. Dropped by the same rule rather than special-cased, so the server may send one
    // without the panel growing a branch for it.
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

        Assert.Null(Assert.Single(rows).ServerOffText);
    }

    // Nothing to say about a category that is simply on.
    [Fact]
    public void An_enabled_category_says_nothing()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), RemoteConfig());

        Assert.Null(Assert.Single(rows).ServerOffText);
    }

    // A config that has not been fetched forbids nothing, so there is no off-state to explain.
    // Without this the panel would tell a user their collections were switched off by XIV Shinies
    // whenever the plugin could not reach /config, which would be a lie.
    [Fact]
    public void A_category_says_nothing_before_the_config_arrives()
    {
        var rows = CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), remoteConfig: null);

        Assert.Null(Assert.Single(rows).ServerOffText);
    }

    // --- Which mark a row wears -----------------------------------------------------------------
    // BadgeFor decides between "Off", "New" and nothing. The precedence is a promise about what the
    // user sees, so it is pinned here rather than left inside the draw method where no test reaches
    // it. Rows come from the real Build so the inputs are the ones the window actually passes.

    private static CategorySettingsRow UnseenRow(bool serverEnabled)
    {
        var config = RemoteConfig(
            categories: new Dictionary<string, bool> {[UnknownCategory] = serverEnabled});

        // OptedIn marks nothing as seen, so the row arrives new — the state a badge is owed for.
        return Assert.Single(
            CategorySettingsView.Build(
                new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), config));
    }

    // The collision the precedence exists for: a collection can be both freshly added AND switched
    // off, and the badge must not invite the user toward something they cannot use.
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

    // The first-run wizard shows every collection by definition, so it announces none of them.
    [Fact]
    public void A_surface_that_does_not_badge_wears_no_New_mark()
    {
        Assert.Equal(
            CategoryBadgeKind.None,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: true), showNewChips: false, badgedThisSession: true));
    }

    // The switched-off mark is not a badging decision, so the surface that suppresses "New" still
    // shows it — a wizard user is owed the reason a row cannot be ticked.
    [Fact]
    public void A_surface_that_does_not_badge_still_shows_the_Off_mark()
    {
        Assert.Equal(
            CategoryBadgeKind.Off,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: false), showNewChips: false, badgedThisSession: false));
    }

    // A row the user has already been shown, with nothing in the session set either: the two
    // sources of newness are both quiet, so there is nothing to announce.
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

    // The half of the New rule the row itself carries: the badge does not depend on a caller's
    // session record — a row the install has never shown badges on its own flag alone.
    [Fact]
    public void A_new_row_wears_New_before_the_window_records_anything()
    {
        Assert.Equal(
            CategoryBadgeKind.New,
            CategorySettingsView.BadgeFor(
                UnseenRow(serverEnabled: true), showNewChips: true, badgedThisSession: false));
    }

    // --- When a drawing retires the announcement ------------------------------------------------
    // ShowingRetiresTheBadge decides which drawings are recorded as seen. The rule differs per
    // surface, so it takes the surface as a parameter — and it is pure so both arms are testable.

    private static CategorySettingsRow UnseenRowWithNoConfig() =>
        Assert.Single(CategorySettingsView.Build(
            new[] {Fake(UnknownCategory)}, OptedIn(UnknownCategory), remoteConfig: null));

    // A badging surface waits for the server's answer: the record is what the badge is spent
    // from, and spending it on an assumption could cost the announcement outright.
    [Fact]
    public void A_badging_surface_does_not_retire_on_an_unanswered_config()
    {
        Assert.False(
            CategorySettingsView.ShowingRetiresTheBadge(UnseenRowWithNoConfig(), showNewChips: true));
    }

    // The wizard shows every collection as its purpose, so a failed config poll must not stop it
    // recording what it plainly showed.
    [Fact]
    public void A_non_badging_surface_retires_even_on_an_unanswered_config()
    {
        Assert.True(
            CategorySettingsView.ShowingRetiresTheBadge(UnseenRowWithNoConfig(), showNewChips: false));
    }

    // Greyed and unusable is not an introduction, on either surface.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_server_disabled_row_never_retires_its_announcement(bool showNewChips)
    {
        Assert.False(
            CategorySettingsView.ShowingRetiresTheBadge(UnseenRow(serverEnabled: false), showNewChips));
    }

    // A row with no announcement left has nothing to retire, so reporting it would only make the
    // caller save the config on every frame.
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

    // The list is rebuilt every frame the settings window is open, so its size cannot be the
    // server's to choose. No honest manifest is anywhere near the ceiling.
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

    // A key standing in for a missing label is folded like the label would have been — the raw
    // key keeps its identity for consent, but what is DRAWN never carries invisible formatting.
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

    // A key with no visible spelling at all is as malformed as a blank one, and the group is
    // dropped the same way — a checkbox with an invisible label is not a consent control.
    [Fact]
    public void A_group_whose_key_and_label_both_fold_to_nothing_is_dropped()
    {
        var config = RemoteConfig(
            itemManifestGroups: new[] {Group("​‮", " "), Group("real", "Real")});

        var rows = CategorySettingsView.Build(
            new[] {FakeManifestDriven("items")}, OptedIn("items"), config);

        Assert.Equal("real", Assert.Single(Assert.Single(rows).Groups!).Key);
    }
}
