using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The consent surfaces group categories under the section titles collectors declare. There is no
// fixed section list; the rows decide the order.
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

    // CollectorRegistry's order decides which heading comes first, and a section's rows stay under one
    // heading even when they are not adjacent.
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

    // The fixture names each row "{key} display", so name order and key order agree here; the next
    // test separates them.
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

    // The order must follow what the reader sees.
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

    // A case-sensitive ordinal sort would file "apples" after "Zebra".
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

    // The extensibility gate for sections: a new collection brings its heading with it, which a
    // section list held by the UI would break.
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
