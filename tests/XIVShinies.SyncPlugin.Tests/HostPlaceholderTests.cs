using System;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Tests.Collectors;

namespace XIVShinies.SyncPlugin.Tests;

// The placeholder a collection's own copy uses for the website's address, and the rule that fills it
// in. Each `[Fact]` is one test, like `it(...)` in Jest.
public class HostPlaceholderTests
{
    // A made-up address, so a test can tell one that was passed in from one written into the copy.
    // `const` fixes the value when the code compiles.
    private const string Host = "shinies.example";

    // Every placeholder in the text is replaced, not just the first.
    [Fact]
    public void Every_placeholder_becomes_the_address()
    {
        var text = HostPlaceholder.Token + " asks, and " + HostPlaceholder.Token + " keeps.";

        Assert.Equal("shinies.example asks, and shinies.example keeps.", HostPlaceholder.Fill(text, Host));
    }

    [Fact]
    public void Text_without_a_placeholder_is_unchanged()
    {
        Assert.Equal("Nothing to fill.", HostPlaceholder.Fill("Nothing to fill.", Host));
    }

    // A collection's disclosure is drawn after Fill, so any brace left over afterwards is a placeholder
    // misspelled (say "{Host}") that would reach the player as written; and none calls the website
    // "server". Every collection is checked, including one added later, through the registry itself.
    // `foreach` walks every item in turn, like `for (const info of ...)` in TypeScript;
    // `new[] { a, b }` is an array of the two, like `[a, b]`; `?? ""` uses an empty string when there
    // are no details; `StringComparison.OrdinalIgnoreCase` matches whatever the letter case.
    [Fact]
    public void Every_collection_disclosure_fills_to_plain_text_without_server()
    {
        foreach (var info in CategoryInfoReflection.All())
        {
            foreach (var text in new[] { info.WhatGetsSent, info.Details ?? "" })
            {
                var filled = HostPlaceholder.Fill(text, Host);

                Assert.DoesNotContain("{", filled);
                Assert.DoesNotContain("}", filled);
                Assert.DoesNotContain("server", filled, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // A collection's name and section heading are drawn as written, never filled, so neither may
    // carry a placeholder at all.
    [Fact]
    public void No_collection_name_or_section_carries_a_placeholder()
    {
        foreach (var info in CategoryInfoReflection.All())
        {
            Assert.DoesNotContain("{", info.DisplayName);
            Assert.DoesNotContain("{", info.Section);
        }
    }

    // The tamed-beasts hover is written by its collector, not its registry entry, so it is checked
    // here by name.
    [Fact]
    public void The_whole_bestiary_note_fills_to_the_website()
    {
        Assert.Equal(
            "Your whole bestiary has been read, so shinies.example knows which beasts you are missing.",
            HostPlaceholder.Fill(TamedBeastCollector.WholeBestiaryRead, Host));
    }
}
