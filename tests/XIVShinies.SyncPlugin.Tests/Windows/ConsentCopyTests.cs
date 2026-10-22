using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// Consent copy that is true only in some states: the wizard's opening sentence, and whether a privacy
// card may say "you choose".
public class ConsentCopyTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

    private const string Choose = "Choose what to upload. ";

    private const string AllStartOff =
        "The collections below, and sharing your Crucible runs, all start switched off — " +
        "nothing about your progress is sent unless you turn it on here. ";

    private const string SomethingOn = "Nothing about your progress is sent unless you switch it on here. ";

    private const string TrackerSentence =
        "Sharing live Occult instance state starts on; untick it below if you would rather not. ";

    private const string Closing = "You can change any of this later.";

    // --- The wizard's opening sentence ---

    // "Start switched off" holds only while nothing is chosen on; the tracker's "starts on" only while
    // the server offers it and it is chosen on.
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
        Assert.Equal(
            expected,
            ConsentCopy.WizardIntro(
                anythingSwitchedOn: anythingSwitchedOn,
                trackerOffered: trackerOffered,
                trackerOn: trackerOn,
                userHasAChoice: true,
                host: Host));
    }

    // With nothing permitted, as during a pause, an invitation to choose would point at grayed-out
    // boxes. WizardIntro's remarks say why the sentence names no one.
    [Fact]
    public void With_nothing_to_choose_the_sentence_says_so()
    {
        Assert.Equal(
            "Nothing about your progress is sent: everything below is switched off on " + Host +
            " for now. Once it is back on, you can choose what to upload in the settings.",
            ConsentCopy.WizardIntro(
                anythingSwitchedOn: false,
                trackerOffered: false,
                trackerOn: true,
                userHasAChoice: false,
                host: Host));
    }

    // --- What counts as switched on ---

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

    private static ConfigResponse Config(bool crucible = false, bool tracker = false, bool enabled = true) =>
        new()
        {
            Categories = new Dictionary<string, bool>(),
            Enabled = enabled,
            Intervals = new ConfigIntervals { FullSyncMinutes = 30, UnlockDebounceSeconds = 5 },
            ItemManifest = [],
            ManifestVersion = "x",
            CrucibleRuns = new CrucibleRunsConfig { Enabled = crucible },
            OccultTracker = new OccultTrackerConfig { Enabled = tracker },
        };

    // A choice the server has since grayed out still counts: it takes effect once the server permits it.
    [Fact]
    public void A_chosen_collection_counts_whatever_the_server_says()
    {
        Assert.True(ConsentCopy.AnythingChosenOn(
            new[] { Row(serverEnabled: true, userEnabled: true) }, shareCrucibleRuns: false));
        Assert.True(ConsentCopy.AnythingChosenOn(
            new[] { Row(serverEnabled: false, userEnabled: true) }, shareCrucibleRuns: false));
    }

    [Fact]
    public void The_crucible_choice_counts_and_no_choice_does_not()
    {
        var nothingChosen = new[] { Row(serverEnabled: true) };

        Assert.True(ConsentCopy.AnythingChosenOn(nothingChosen, shareCrucibleRuns: true));
        Assert.False(ConsentCopy.AnythingChosenOn(nothingChosen, shareCrucibleRuns: false));
    }

    // --- Whether a privacy card may say "you choose" ---

    [Fact]
    public void A_permitted_collection_leaves_a_choice()
    {
        Assert.True(ConsentCopy.UserHasAChoice(new[] { Row(serverEnabled: true) }, Config()));
    }

    [Fact]
    public void A_permitted_sharing_feature_leaves_a_choice()
    {
        var rows = new[] { Row(serverEnabled: false) };

        Assert.True(ConsentCopy.UserHasAChoice(rows, Config(crucible: true)));
        Assert.True(ConsentCopy.UserHasAChoice(rows, Config(tracker: true)));
    }

    // Covers every switch off, a pause with the sharing switches left on, and a server that advertises
    // neither sharing feature.
    [Fact]
    public void Nothing_permitted_leaves_no_choice()
    {
        var rows = new[] { Row(serverEnabled: false) };

        Assert.False(ConsentCopy.UserHasAChoice(rows, Config()));
        Assert.False(ConsentCopy.UserHasAChoice(rows, Config(crucible: true, tracker: true, enabled: false)));
        Assert.False(ConsentCopy.UserHasAChoice(
            rows, Config() with { CrucibleRuns = null, OccultTracker = null }));
    }

    // Before the server answers it has refused nothing.
    [Fact]
    public void Before_the_server_answers_the_choice_stands()
    {
        Assert.True(ConsentCopy.UserHasAChoice(new[] { Row(serverEnabled: false) }, remoteConfig: null));
    }
}
