using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The consent surfaces draw the category list one section at a time, grouped by the titles the
// collectors declare. These tests pin the grouping rules: no fixed section list, and order
// decided entirely by the rows.
public class CategorySectionTests
{
    private static CategorySettingsRow Row(string key, string section) => new()
    {
        Key = key,
        DisplayName = $"{key} display",
        Section = section,
        WhatGetsSent = $"what {key} sends",
        UserEnabled = true,
        ServerEnabled = true,
        UsesItemManifest = false,
    };

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

    [Fact]
    public void No_rows_means_no_sections()
    {
        Assert.Empty(CategorySettingsView.GroupBySection(Array.Empty<CategorySettingsRow>()));
    }
}
