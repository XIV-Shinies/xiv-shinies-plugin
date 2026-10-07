using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The consent surfaces' claims about the user's choices, each true only in some states: the
// wizard's opening sentence, and whether a privacy card may say "you choose". Each `[Fact]` is one
// test, like `it(...)` in Jest; a `[Theory]` runs once per `[InlineData]` row, like `it.each`.
public class ConsentCopyTests
{
    // The website address the copy names. `const` fixes the value when the code compiles.
    // A made-up address, so a test can tell one that was passed in from one written into the copy.
    private const string Host = "shinies.example";

    private const string Choose = "Choose what to upload. ";

    private const string AllStartOff =
        "The collections below, and sharing your Crucible runs, all start switched off — " +
        "nothing about your progress is sent unless you turn it on here. ";

    private const string SomethingOn = "Nothing about your progress is sent unless you switch it on here. ";

    private const string TrackerSentence =
        "Sharing live Occult instance state starts on; untick it below if you would rather not. ";

    private const string Closing = "You can change any of this later.";

    // --- The wizard's opening sentence --------------------------------------------------------------

    // Every combination of the three inputs, with a choice to make. "Start switched off" holds only
    // while nothing is chosen on; the tracker's "starts on" only while the server offers it and it is
    // chosen on.
    // `[InlineData]` needs values fixed when the code compiles, which `const` strings joined with `+`
    // are.
    [Theory]
    [InlineData(false, true, true, Choose + AllStartOff + TrackerSentence + Closing)]
    [InlineData(false, true, false, Choose + AllStartOff + Closing)]
    [InlineData(false, false, true, Choose + AllStartOff + Closing)]
    [InlineData(false, false, false, Choose + AllStartOff + Closing)]
    [InlineData(true, true, true, Choose + SomethingOn + TrackerSentence + Closing)]
    [InlineData(true, true, false, Choose + SomethingOn + Closing)]
    [InlineData(true, false, true, Choose + SomethingOn + Closing)]
    [InlineData(true, false, false, Choose + SomethingOn + Closing)]
    public void The_opening_sentence_matches_the_boxes_beneath_it(
        bool anythingSwitchedOn, bool trackerOffered, bool trackerOn, string expected)
    {
        // `trackerOffered: trackerOffered` names the parameter each value fills, so a row of booleans
        // says which is which, like the keys of an options object.
        Assert.Equal(
            expected,
            ConsentCopy.WizardIntro(
                anythingSwitchedOn: anythingSwitchedOn,
                trackerOffered: trackerOffered,
                trackerOn: trackerOn,
                userHasAChoice: true,
                host: Host));
    }

    // With nothing the server permits, as during a pause, the sentence says so instead of inviting a
    // choice the grayed boxes below would refuse.
    [Fact]
    public void With_nothing_to_choose_the_sentence_says_so()
    {
        Assert.Equal(
            "Nothing about your progress is sent: " + Host + " has everything below switched off " +
            "for now. Once it allows them, you can choose what to upload in the settings.",
            ConsentCopy.WizardIntro(
                anythingSwitchedOn: false,
                trackerOffered: false,
                trackerOn: true,
                userHasAChoice: false,
                host: Host));
    }

    // --- What counts as switched on -----------------------------------------------------------------

    /// <summary>A collection row with the given switches.</summary>
    // `bool userEnabled = false` is an optional parameter with a default, like a TypeScript default
    // parameter. `=> new() { ... }` builds a CategorySettingsRow and sets these properties on it, like
    // an object literal.
    private static CategorySettingsRow Row(bool serverEnabled, bool userEnabled = false) => new()
    {
        Key = "quests",
        DisplayName = "Quests",
        Section = "Fakes",
        WhatGetsSent = "what quests sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        UsesItemManifest = false,
    };

    /// <summary>A config with the sharing features' switches as given.</summary>
    // `[]` is an empty list.
    private static ConfigResponse Config(bool crucible = false, bool tracker = false, bool enabled = true) =>
        new()
        {
            // `Dictionary<string, bool>` maps text to a bool, like `Map<string, boolean>`.
            Categories = new Dictionary<string, bool>(),
            Enabled = enabled,
            Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
            ItemManifest = [],
            ManifestVersion = "x",
            CrucibleRuns = new CrucibleRunsConfig { Enabled = crucible },
            OccultTracker = new OccultTrackerConfig { Enabled = tracker },
        };

    // A collection the user chose counts, even one the server has since grayed out: the choice is still
    // theirs and takes effect once the server permits it. `new[] { ... }` builds an array whose type is
    // taken from its items.
    [Fact]
    public void A_chosen_collection_counts_whatever_the_server_says()
    {
        Assert.True(ConsentCopy.AnythingChosenOn(
            new[] { Row(serverEnabled: true, userEnabled: true) }, shareCrucibleRuns: false));
        Assert.True(ConsentCopy.AnythingChosenOn(
            new[] { Row(serverEnabled: false, userEnabled: true) }, shareCrucibleRuns: false));
    }

    // The Crucible choice counts on its own; with nothing chosen at all, nothing counts.
    [Fact]
    public void The_crucible_choice_counts_and_no_choice_does_not()
    {
        var nothingChosen = new[] { Row(serverEnabled: true) };

        Assert.True(ConsentCopy.AnythingChosenOn(nothingChosen, shareCrucibleRuns: true));
        Assert.False(ConsentCopy.AnythingChosenOn(nothingChosen, shareCrucibleRuns: false));
    }

    // --- Whether a privacy card may say "you choose" ------------------------------------------------

    // One collection the server permits is a choice.
    [Fact]
    public void A_permitted_collection_leaves_a_choice()
    {
        Assert.True(ConsentCopy.UserHasAChoice(new[] { Row(serverEnabled: true) }, Config()));
    }

    // With every collection off, a sharing feature the server still offers is a choice too.
    [Fact]
    public void A_permitted_sharing_feature_leaves_a_choice()
    {
        var rows = new[] { Row(serverEnabled: false) };

        Assert.True(ConsentCopy.UserHasAChoice(rows, Config(crucible: true)));
        Assert.True(ConsentCopy.UserHasAChoice(rows, Config(tracker: true)));
    }

    // Nothing the server permits leaves the user nothing to choose: every switch off, a pause with the
    // sharing switches left on, or a server that advertises neither sharing feature at all.
    // `with { ... }` copies the record with the listed properties changed.
    [Fact]
    public void Nothing_permitted_leaves_no_choice()
    {
        var rows = new[] { Row(serverEnabled: false) };

        Assert.False(ConsentCopy.UserHasAChoice(rows, Config()));
        Assert.False(ConsentCopy.UserHasAChoice(rows, Config(crucible: true, tracker: true, enabled: false)));
        Assert.False(ConsentCopy.UserHasAChoice(
            rows, Config() with { CrucibleRuns = null, OccultTracker = null }));
    }

    // Before the server answers it has refused nothing, so the sharing features remain choices even
    // with no collection permitted.
    [Fact]
    public void Before_the_server_answers_the_choice_stands()
    {
        Assert.True(ConsentCopy.UserHasAChoice(new[] { Row(serverEnabled: false) }, remoteConfig: null));
    }
}
