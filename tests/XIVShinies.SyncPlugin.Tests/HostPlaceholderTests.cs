using System;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Tests.Collectors;

namespace XIVShinies.SyncPlugin.Tests;

// The placeholder a collection's own copy uses for the website's address, and the rule that fills it
// in.
public class HostPlaceholderTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

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

    // A brace left after Fill is a misspelled placeholder, such as "{Host}", that would reach the
    // player. No disclosure calls the website "server". The registry scan covers later collections.
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

    // Names and section headings are drawn as written, never filled.
    [Fact]
    public void No_collection_name_or_section_carries_a_placeholder()
    {
        foreach (var info in CategoryInfoReflection.All())
        {
            Assert.DoesNotContain("{", info.DisplayName);
            Assert.DoesNotContain("{", info.Section);
        }
    }

    // The tamed-beasts hover lives on its collector, not its registry entry, so the scan misses it.
    [Fact]
    public void The_whole_bestiary_note_fills_to_the_website()
    {
        Assert.Equal(
            "Your whole bestiary has been read, so shinies.example knows which beasts you are missing.",
            HostPlaceholder.Fill(TamedBeastCollector.WholeBestiaryRead, Host));
    }
}
