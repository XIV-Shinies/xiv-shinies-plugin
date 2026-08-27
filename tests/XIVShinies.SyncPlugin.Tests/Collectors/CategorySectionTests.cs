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

    // Sections appear in the order their first row appears — so CollectorRegistry still decides
    // which heading comes first, even when a section's rows are not adjacent in the list.
    [Fact]
    public void Sections_follow_the_order_their_first_row_appears()
    {
        var sections = CategorySettingsView.GroupBySection(new[]
        {
            Row("quests", "Collection log"),
            Row("cards", "Triple Triad"),
            Row("mounts", "Collection log"),
        });

        Assert.Equal(new[] { "Collection log", "Triple Triad" }, sections.Select(s => s.Title));
        Assert.Equal(new[] { "cards" }, sections[1].Rows.Select(r => r.Key));
    }

    // Within a section the rows are alphabetical by DISPLAY NAME — what the reader actually sees —
    // rather than by key or by the order the registry happened to list them in. The fixture names
    // each row "{key} display", so sorting by name and sorting by key agree here; the next test
    // separates them.
    [Fact]
    public void Rows_are_sorted_within_their_section()
    {
        var sections = CategorySettingsView.GroupBySection(new[]
        {
            Row("quests", "Collection log"),
            Row("achievements", "Collection log"),
            Row("mounts", "Collection log"),
        });

        Assert.Equal(
            new[] { "achievements", "mounts", "quests" }, sections[0].Rows.Select(r => r.Key));
    }

    // The sort reads the display name, not the key — the two can disagree, and the on-screen order
    // has to follow what is on screen.
    [Fact]
    public void Rows_sort_by_display_name_rather_than_by_key()
    {
        var sections = CategorySettingsView.GroupBySection(new[]
        {
            new CategorySettingsRow
            {
                Key = "aaa", DisplayName = "Zither rolls", Section = "Collection log",
                WhatGetsSent = "what aaa sends", UserEnabled = true, ServerEnabled = true,
                UsesItemManifest = false,
            },
            new CategorySettingsRow
            {
                Key = "zzz", DisplayName = "Achievements", Section = "Collection log",
                WhatGetsSent = "what zzz sends", UserEnabled = true, ServerEnabled = true,
                UsesItemManifest = false,
            },
        });

        Assert.Equal(new[] { "zzz", "aaa" }, sections[0].Rows.Select(r => r.Key));
    }

    // Case must not split the alphabet: an ordinal sort would file every capitalized name before
    // every lowercase one, so "apples" would land after "Zebra".
    [Fact]
    public void Sorting_ignores_case()
    {
        var sections = CategorySettingsView.GroupBySection(new[]
        {
            new CategorySettingsRow
            {
                Key = "zebra", DisplayName = "Zebra", Section = "Collection log",
                WhatGetsSent = "what zebra sends", UserEnabled = true, ServerEnabled = true,
                UsesItemManifest = false,
            },
            new CategorySettingsRow
            {
                Key = "apples", DisplayName = "apples", Section = "Collection log",
                WhatGetsSent = "what apples sends", UserEnabled = true, ServerEnabled = true,
                UsesItemManifest = false,
            },
        });

        Assert.Equal(new[] { "apples", "zebra" }, sections[0].Rows.Select(r => r.Key));
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
