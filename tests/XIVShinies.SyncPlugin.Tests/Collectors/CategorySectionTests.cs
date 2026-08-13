using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The consent surfaces draw the category list one section at a time, grouped by the titles the
// collectors declare. These tests pin the grouping rules: no fixed section list, order decided
// entirely by the rows, and a header count that means "will actually upload".
public class CategorySectionTests
{
    private static CategorySettingsRow Row(
        string key,
        string section,
        bool userEnabled = true,
        bool serverEnabled = true,
        IReadOnlyList<ItemGroupRow>? groups = null) => new()
    {
        Key = key,
        DisplayName = $"{key} display",
        Section = section,
        WhatGetsSent = $"what {key} sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        UsesItemManifest = groups is not null,
        Groups = groups,
    };

    private static ItemGroupRow Group(string key, bool enabled) =>
        new() { Key = key, Label = $"{key} label", Enabled = enabled, IsNew = false };

    // Sections appear in the order their first row appears, and rows keep their order within a
    // section — so CollectorRegistry's registration order still decides everything on screen,
    // even when a section's rows are not adjacent in the list.
    [Fact]
    public void Sections_follow_first_appearance_and_rows_keep_their_order()
    {
        var sections = CategorySettingsView.GroupBySection(new[]
        {
            Row("quests", "Collection log"),
            Row("cards", "Triple Triad"),
            Row("mounts", "Collection log"),
        });

        Assert.Equal(new[] { "Collection log", "Triple Triad" }, sections.Select(s => s.Title));
        Assert.Equal(new[] { "quests", "mounts" }, sections[0].Rows.Select(r => r.Key));
        Assert.Equal(new[] { "cards" }, sections[1].Rows.Select(r => r.Key));
    }

    // The extensibility gate, one level up from the rows: a collection declaring a section this
    // plugin has never drawn brings its heading with it. If the UI held a section list, this
    // title would not survive the trip.
    [Fact]
    public void A_row_declaring_an_unheard_of_section_gets_its_own_heading()
    {
        var sections = CategorySettingsView.GroupBySection(new[]
        {
            Row("quests", "Collection log"),
            Row("facewear", "Glamour"),
        });

        Assert.Equal(2, sections.Count);
        Assert.Equal("Glamour", sections[1].Title);
        Assert.Equal("facewear", Assert.Single(sections[1].Rows).Key);
    }

    // The count the section header carries. It answers "how much of this section is
    // uploading?", so a category the user ticked but the server switched off does not count —
    // it will not upload, whatever the box says.
    [Fact]
    public void EnabledCount_counts_only_rows_that_will_actually_upload()
    {
        var section = Assert.Single(CategorySettingsView.GroupBySection(new[]
        {
            Row("quests", "Collection log", userEnabled: true),
            Row("mounts", "Collection log", userEnabled: false),
            Row("minions", "Collection log", userEnabled: true, serverEnabled: false),
        }));

        Assert.Equal(1, section.EnabledCount);
    }

    // A ticked manifest-driven category whose groups are all off looks at nothing during a pass,
    // so the header count must not claim it is on — the header would say more than will upload.
    [Fact]
    public void EnabledCount_does_not_count_a_manifest_row_whose_groups_are_all_off()
    {
        var section = Assert.Single(CategorySettingsView.GroupBySection(new[]
        {
            Row("items", "Items & relics", groups: new[] { Group("proofs", enabled: false) }),
        }));

        Assert.Equal(0, section.EnabledCount);
    }

    [Fact]
    public void EnabledCount_counts_a_manifest_row_with_at_least_one_group_on()
    {
        var section = Assert.Single(CategorySettingsView.GroupBySection(new[]
        {
            Row(
                "items",
                "Items & relics",
                groups: new[] { Group("proofs", enabled: true), Group("materials", enabled: false) }),
        }));

        Assert.Equal(1, section.EnabledCount);
    }

    [Fact]
    public void No_rows_means_no_sections()
    {
        Assert.Empty(CategorySettingsView.GroupBySection(Array.Empty<CategorySettingsRow>()));
    }
}
