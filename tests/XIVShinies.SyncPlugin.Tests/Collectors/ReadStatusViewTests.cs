using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// ReadStatusView builds the settings window's "Reading from:" panel in two groups: the collections the
// sync will upload, and the containers the item counts come from. Like CategorySettingsView, it must
// contain no category-name or source-name branch.
//
// A note's tone picks its form (see SourceNote): a healthy note is a chip whose Label is all its
// visible text; a note needing an in-game action is a full line in Text.
public class ReadStatusViewTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

    private const string UnknownCategory = "facewear";

    private static CategorySettingsRow Row(
        string key,
        bool userEnabled = true,
        bool serverEnabled = true,
        string? skipReason = null,
        bool usesItemManifest = false) => new()
    {
        Key = key,
        DisplayName = $"{key} display",
        Section = "Fakes",
        WhatGetsSent = $"what {key} sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        SkipReason = skipReason,
        UsesItemManifest = usesItemManifest,
    };

    private static ItemSourceStatus Status(string state) => new() { State = state };

    private static IReadOnlyDictionary<string, ItemSourceStatus> NoSources() =>
        new Dictionary<string, ItemSourceStatus>();

    // Suppression tests need a container note: a manifest-driven collection's note drops only when one
    // stands in its place.
    private static IReadOnlyDictionary<string, ItemSourceStatus> OneSource() =>
        new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

    // Container notes appear only while a manifest-driven collection is switched on.
    private static CategorySettingsRow ManifestRow(string key = "items") =>
        Row(key, usesItemManifest: true);

    // Pins that Build has no tone branch that could drop an unreadable source.
    [Fact]
    public void An_unreadable_source_flows_through_as_a_container_chip()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Mannequins] = Status(SourceStates.Unreadable),
        };

        var status = ReadStatusView.Build(new[] { ManifestRow() }, sources, Host);

        var note = Assert.Single(status.Containers);
        Assert.Equal("Mannequins", note.Label);
        Assert.Equal(SourceTone.Unreadable, note.Tone);
        Assert.Null(note.Text);

        // The hover names the website by the address Build was handed.
        Assert.Contains(Host, note.Detail);
    }

    // The collector reports mannequins last, so the one chip nothing can change sits at the end of the
    // row.
    [Fact]
    public void Container_notes_keep_the_order_the_sources_were_reported_in()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
            [SourceKeys.Saddlebag] = Status(SourceStates.Cached),
            [SourceKeys.Mannequins] = Status(SourceStates.Unreadable),
        };

        var status = ReadStatusView.Build(new[] { ManifestRow() }, sources, Host);

        Assert.Equal(
            new[] { "Inventory", "Saddlebag", "Mannequins" },
            status.Containers.Select(note => note.Label));
    }

    // Hover copy may hide only what a reader can live without.
    [Fact]
    public void A_healthy_collections_chip_carries_the_collectors_hover_copy()
    {
        var row = Row(UnknownCategory) with { CollectedDetail = "Optional hover copy." };

        var status = ReadStatusView.Build(new[] { row }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Equal("Optional hover copy.", note.Detail);
    }

    // Hover copy belongs to the healthy chip alone: a partial line is visible and already says what
    // reads the unread half.
    [Fact]
    public void A_partial_line_does_not_carry_the_chip_hover_copy()
    {
        var row = Row(UnknownCategory) with
        {
            PartialNote = "half read — visit the place.",
            CollectedDetail = "Optional hover copy.",
        };

        var status = ReadStatusView.Build(new[] { row }, NoSources(), Host);

        Assert.Null(Assert.Single(status.Collections).Detail);
    }

    // Drawn in the Missing tone, so the unread half stays on screen instead of hiding behind a healthy
    // chip.
    [Fact]
    public void A_partially_read_collection_draws_a_line_naming_the_unread_half()
    {
        var row = Row(UnknownCategory) with { PartialNote = "half read — visit the place." };

        var status = ReadStatusView.Build(new[] { row }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("facewear display: half read — visit the place.", note.Text);
    }

    // A collector cannot know the configured address, so it writes a placeholder; the panel fills it
    // in on both notes a collector authors.
    [Fact]
    public void A_collectors_notes_name_the_configured_website()
    {
        var partial = Row(UnknownCategory) with
        {
            PartialNote = "half read — " + HostPlaceholder.Token + " needs the rest.",
        };
        var whole = Row("mounts") with
        {
            CollectedDetail = "Read in full, so " + HostPlaceholder.Token + " has it all.",
        };

        var status = ReadStatusView.Build(new[] { partial, whole }, NoSources(), Host);

        Assert.Contains(
            status.Collections,
            note => note.Text == "facewear display: half read — " + Host + " needs the rest.");
        Assert.Contains(
            status.Collections, note => note.Detail == "Read in full, so " + Host + " has it all.");
    }

    // With no chip there is nowhere to hover, so the suppression takes the hover copy too.
    [Fact]
    public void A_manifest_rows_chip_hover_copy_is_suppressed_with_its_chip()
    {
        var row = ManifestRow() with { CollectedDetail = "Optional hover copy." };

        var status = ReadStatusView.Build(new[] { row }, OneSource(), Host);

        Assert.Empty(status.Collections);
    }

    // A partial phrase says something no container line says, so it survives the suppression.
    [Fact]
    public void A_manifest_rows_partial_note_survives_the_container_suppression()
    {
        var row = ManifestRow() with { PartialNote = "half read — visit the place." };

        var status = ReadStatusView.Build(new[] { row }, OneSource(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal("items display: half read — visit the place.", note.Text);
    }

    // The status memory merges across passes, so a partial note can outlive the read that wrote it. A
    // fresh skip reason is current and wins the line.
    [Fact]
    public void A_skip_reason_outranks_a_stale_partial_note()
    {
        var row = Row(UnknownCategory, skipReason: "collector_error")
            with { PartialNote = "half read — visit the place." };

        var status = ReadStatusView.Build(new[] { row }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal("facewear display: could not be read.", note.Text);
    }

    // A read collection needs nothing from the user, so it is a bare chip: the green check means
    // "read".
    [Fact]
    public void An_enabled_collection_that_was_read_is_a_chip_labeled_with_its_name()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts") }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal("mounts display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Null(note.Detail);
    }

    // How the "open your Achievements window once" hint reaches the panel without naming achievements:
    // CollectSkipReasons turns the collector's reason into advice. It asks for an action, so it is a
    // full line, never a chip.
    [Fact]
    public void A_collection_skipped_for_a_described_reason_appends_that_reasons_hint()
    {
        var rows = new[] { Row("achievements", skipReason: CollectSkipReasons.AchievementListNotLoaded) };

        var note = Assert.Single(ReadStatusView.Build(rows, NoSources(), Host).Collections);

        Assert.Equal("achievements display", note.Label);
        Assert.Equal(
            "achievements display: "
                + CollectSkipReasons.Describe(CollectSkipReasons.AchievementListNotLoaded, Host),
            note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // A reason the user cannot act on, such as a collector bug or an unloadable game sheet, has no
    // advice. The collection still missed the upload, so the note is a plain statement, never a raw
    // wire string like "collector_error".
    [Fact]
    public void A_collection_skipped_for_an_undescribed_reason_says_only_that_it_could_not_be_read()
    {
        var rows = new[] { Row("mounts", skipReason: CollectSkipReasons.CollectorError) };

        var note = Assert.Single(ReadStatusView.Build(rows, NoSources(), Host).Collections);

        Assert.Equal("mounts display: could not be read.", note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // The server chose not to ask for it, so it was skipped, not missed.
    [Fact]
    public void A_collection_the_server_does_not_ask_for_draws_no_line()
    {
        var rows = new[] { Row(UnknownCategory, skipReason: CollectSkipReasons.ManifestNotOffered) };

        Assert.Empty(ReadStatusView.Build(rows, NoSources(), Host).Collections);
    }

    // The user chose not to sync it, so "not read" would look like a fault.
    [Fact]
    public void A_collection_the_user_switched_off_gets_no_note_at_all()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts", userEnabled: false) }, NoSources(), Host);

        Assert.Empty(status.Collections);
    }

    // The settings card already explains the grayed-out checkbox.
    [Fact]
    public void A_collection_the_server_switched_off_gets_no_note_at_all()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts", serverEnabled: false) }, NoSources(), Host);

        Assert.Empty(status.Collections);
    }

    // Its items were counted in the containers, whose notes already say "read" in more detail. The rule
    // keys on UsesItemManifest, never on a category name.
    [Fact]
    public void A_manifest_driven_collection_that_was_read_gets_no_note_of_its_own()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", usesItemManifest: true) }, OneSource(), Host);

        Assert.Empty(status.Collections);
    }

    // With an empty server manifest the pass opens no container, so dropping this note too would leave
    // a switched-on collection unmentioned.
    [Fact]
    public void A_manifest_driven_collection_keeps_its_note_when_no_container_note_stands_in_for_it()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", usesItemManifest: true) }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal("items display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    // The suppression asks whether a container note exists, not a status: a status SourceNoteText has
    // no copy for is dropped, so the reader never sees it.
    [Fact]
    public void A_manifest_driven_collection_keeps_its_note_when_every_container_status_was_dropped()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            ["facewearCabinet"] = Status(SourceStates.Live),
        };

        var status = ReadStatusView.Build(new[] { Row("items", usesItemManifest: true) }, sources, Host);

        Assert.Empty(status.Containers);
        Assert.Equal("items display", Assert.Single(status.Collections).Label);
    }

    // Why a collection was skipped, such as no config yet or an unreadable inventory, exists nowhere
    // else in the panel, so a skipped one keeps its note even beside a container note.
    [Fact]
    public void A_manifest_driven_collection_that_was_skipped_still_reports_its_hint()
    {
        var rows = new[]
        {
            Row("items", skipReason: CollectSkipReasons.NoRemoteConfig, usesItemManifest: true),
        };

        // A container note is present, which would suppress a healthy collection.
        var note = Assert.Single(ReadStatusView.Build(rows, OneSource(), Host).Collections);

        Assert.Equal(
            "items display: " + CollectSkipReasons.Describe(CollectSkipReasons.NoRemoteConfig, Host),
            note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // No container note stands in for an ordinary collection, so its chip is the only sign the sync
    // can see it.
    [Fact]
    public void A_collection_that_is_not_manifest_driven_keeps_its_chip()
    {
        var status = ReadStatusView.Build(
            new[] { Row("mounts", usesItemManifest: false) }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal("mounts display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    // Storage containers get their copy from SourceNoteText; this view only groups the notes.
    [Fact]
    public void Storage_containers_are_described_by_the_existing_source_note_copy()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

        var status = ReadStatusView.Build(new[] { ManifestRow() }, sources, Host);

        var note = Assert.Single(status.Containers);
        Assert.Equal(
            SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Live), Host),
            note);
    }

    // SourceNoteText returns null for a source and state it has no note for, and the panel drops it
    // rather than show a raw wire string.
    [Fact]
    public void A_storage_container_with_nothing_worth_saying_is_dropped()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            ["facewearCabinet"] = Status(SourceStates.Live),
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

        var note = Assert.Single(ReadStatusView.Build(new[] { ManifestRow() }, sources, Host).Containers);

        Assert.Equal("Inventory", note.Label);
    }

    // The window heads each group with its own label, so no note may land in the other group.
    [Fact]
    public void Collections_and_containers_are_returned_as_separate_groups()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

        var status = ReadStatusView.Build(new[] { Row("mounts"), ManifestRow() }, sources, Host);

        Assert.Equal(new[] { "mounts display" }, status.Collections.Select(note => note.Label));
        Assert.Equal(new[] { "Inventory" }, status.Containers.Select(note => note.Label));
    }

    // Sorted by label, not registration order, so this panel lists collections in the same order as
    // the consent checklist above it.
    [Fact]
    public void Collection_notes_are_ordered_by_label()
    {
        var status = ReadStatusView.Build(new[] { Row("b"), Row("a") }, NoSources(), Host);

        Assert.Equal(
            new[] { "a display", "b display" },
            status.Collections.Select(note => note.Label));
    }

    [Fact]
    public void No_rows_and_no_sources_produce_no_notes_in_either_group()
    {
        var status = ReadStatusView.Build(new List<CategorySettingsRow>(), NoSources(), Host);

        Assert.Empty(status.Collections);
        Assert.Empty(status.Containers);
    }

    // The gate itself: the panel holds no table of known category names.
    [Fact]
    public void A_collection_for_an_unknown_category_still_gets_a_note()
    {
        var status = ReadStatusView.Build(new[] { Row(UnknownCategory) }, NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal("facewear display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    // The gate for the suppression rule: it keys on the collector's flag, never on a category name.
    [Fact]
    public void A_manifest_driven_collection_for_an_unknown_category_is_also_suppressed_when_read()
    {
        var status = ReadStatusView.Build(
            new[] { Row(UnknownCategory, usesItemManifest: true) }, OneSource(), Host);

        Assert.Empty(status.Collections);
    }

    // Containers are opened only for a manifest-driven collection. With none on, nothing reads them,
    // and a note urging the user to open their saddlebag would be advice they cannot act on.
    [Fact]
    public void Container_notes_are_dropped_when_no_manifest_driven_collection_is_on()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts") }, OneSource(), Host);

        Assert.Empty(status.Containers);
        Assert.Equal(new[] { "mounts display" }, status.Collections.Select(note => note.Label));
    }

    // The common case: unticking the collection takes its container notes with it.
    [Fact]
    public void Container_notes_are_dropped_when_the_user_switches_the_manifest_collection_off()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", userEnabled: false, usesItemManifest: true) }, OneSource(), Host);

        Assert.Empty(status.Containers);
    }

    [Fact]
    public void Container_notes_are_dropped_when_the_server_switches_the_manifest_collection_off()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", serverEnabled: false, usesItemManifest: true) }, OneSource(), Host);

        Assert.Empty(status.Containers);
    }

    // The notes are hidden, not forgotten.
    [Fact]
    public void Container_notes_return_once_a_manifest_driven_collection_is_on()
    {
        var status = ReadStatusView.Build(
            new[] { Row("mounts"), ManifestRow() }, OneSource(), Host);

        Assert.Equal(new[] { "Inventory" }, status.Containers.Select(note => note.Label));
    }

    // With every group off, the collector scans nothing and reports a skip. No container is read, so
    // without this note the ticked collection would go unmentioned.
    [Fact]
    public void A_manifest_driven_collection_with_no_groups_enabled_says_what_to_do_about_it()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", usesItemManifest: true, skipReason: CollectSkipReasons.NoItemGroupsEnabled) },
            NoSources(), Host);

        var note = Assert.Single(status.Collections);
        Assert.Equal(
            "items display: not read — none of its groups are switched on. Tick at least one under " +
            "Collections to include it.",
            note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // The order must not depend on case or on the machine's locale: the labels are English strings from
    // this repo. A case-sensitive ordinal sort would put every capitalized label ahead of every
    // lowercase one.
    [Fact]
    public void Collection_notes_are_ordered_regardless_of_case()
    {
        var status = ReadStatusView.Build(new[] { Row("ZEBRA"), Row("apple") }, NoSources(), Host);

        Assert.Equal(
            new[] { "apple display", "ZEBRA display" },
            status.Collections.Select(note => note.Label));
    }

    // List.Sort is unstable, so two notes sharing a label could swap between runs. The note's text
    // breaks the tie, so the row never reshuffles itself.
    [Fact]
    public void Collection_notes_sharing_a_label_are_settled_by_their_text()
    {
        var rows = new[]
        {
            Row("second", skipReason: CollectSkipReasons.NotInOccultInstance) with
            {
                DisplayName = "Same",
            },
            Row("first", skipReason: CollectSkipReasons.AchievementListNotLoaded) with
            {
                DisplayName = "Same",
            },
        };

        var texts = ReadStatusView.Build(rows, NoSources(), Host)
            .Collections.Select(note => note.Text).ToArray();

        Assert.Equal(texts.OrderBy(text => text, StringComparer.Ordinal).ToArray(), texts);
    }
}
