using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The settings window's per-category status memory. CollectionStatusMemory's remarks explain
// why the memory is merged rather than replaced; these tests hold each merge to that rule.
public class CollectionStatusMemoryTests
{
    private static CollectionSnapshot Snapshot(
        IReadOnlyList<string>? collected = null,
        Dictionary<string, string>? skipped = null,
        Dictionary<string, string>? partialNotes = null,
        Dictionary<string, string>? collectedDetails = null)
    {
        var collections = new Dictionary<string, JsonNode>();
        foreach (var key in collected ?? new List<string>())
            collections[key] = JsonNode.Parse("[1]")!;

        return new CollectionSnapshot
        {
            Collections = collections,
            Skipped = skipped ?? new Dictionary<string, string>(),
            PartialNotes = partialNotes ?? new Dictionary<string, string>(),
            CollectedDetails = collectedDetails ?? new Dictionary<string, string>(),
        };
    }

    private static Dictionary<string, string> Memory(params (string Key, string Value)[] entries)
    {
        var memory = new Dictionary<string, string>();
        foreach (var (key, value) in entries)
            memory[key] = value;
        return memory;
    }

    // --- Skip reasons ---------------------------------------------------------------------

    [Fact]
    public void A_skipped_category_gains_its_new_reason()
    {
        var merged = CollectionStatusMemory.MergeSkipReasons(
            Memory(), Snapshot(skipped: Memory(("facewear", "sheet_unavailable"))));

        Assert.Equal("sheet_unavailable", merged["facewear"]);
    }

    [Fact]
    public void A_collected_category_loses_its_old_reason()
    {
        var merged = CollectionStatusMemory.MergeSkipReasons(
            Memory(("facewear", "sheet_unavailable")), Snapshot(collected: new[] { "facewear" }));

        Assert.False(merged.ContainsKey("facewear"));
    }

    [Fact]
    public void A_category_the_pass_never_ran_keeps_its_reason()
    {
        var merged = CollectionStatusMemory.MergeSkipReasons(
            Memory(("facewear", "sheet_unavailable")), Snapshot(collected: new[] { "orchestrion" }));

        Assert.Equal("sheet_unavailable", merged["facewear"]);
    }

    // --- Partial notes --------------------------------------------------------------------

    [Fact]
    public void A_category_collected_with_a_note_gains_it()
    {
        var merged = CollectionStatusMemory.MergePartialNotes(
            Memory(),
            Snapshot(collected: new[] { "facewear" }, partialNotes: Memory(("facewear", "half read."))));

        Assert.Equal("half read.", merged["facewear"]);
    }

    // The transition the panel's honesty rests on: a session that later reads the category in
    // full must stop being described as partial.
    [Fact]
    public void A_category_collected_without_a_note_loses_its_old_one()
    {
        var merged = CollectionStatusMemory.MergePartialNotes(
            Memory(("facewear", "half read.")), Snapshot(collected: new[] { "facewear" }));

        Assert.False(merged.ContainsKey("facewear"));
    }

    [Fact]
    public void A_category_the_pass_never_collected_keeps_its_note()
    {
        var merged = CollectionStatusMemory.MergePartialNotes(
            Memory(("facewear", "half read.")), Snapshot(collected: new[] { "orchestrion" }));

        Assert.Equal("half read.", merged["facewear"]);
    }

    // A skipped category is not a collected one, so its stale note survives — invisibly, because
    // the panel shows the skip reason ahead of it (see ReadStatusViewTests).
    [Fact]
    public void A_skipped_category_keeps_its_stale_note_for_the_skip_reason_to_outrank()
    {
        var merged = CollectionStatusMemory.MergePartialNotes(
            Memory(("facewear", "half read.")),
            Snapshot(skipped: Memory(("facewear", "sheet_unavailable"))));

        Assert.Equal("half read.", merged["facewear"]);
    }

    [Fact]
    public void Merging_never_mutates_the_previous_memory()
    {
        var previous = Memory(("facewear", "half read."));

        CollectionStatusMemory.MergePartialNotes(previous, Snapshot(collected: new[] { "facewear" }));

        Assert.Equal("half read.", previous["facewear"]);
    }

    // --- Healthy-chip hover copy (same rules as the partial notes) --------------------------

    [Fact]
    public void A_category_collected_with_a_chip_detail_gains_it()
    {
        var merged = CollectionStatusMemory.MergeCollectedDetails(
            Memory(),
            Snapshot(
                collected: new[] { "facewear" },
                collectedDetails: Memory(("facewear", "Optional hover copy."))));

        Assert.Equal("Optional hover copy.", merged["facewear"]);
    }

    [Fact]
    public void A_category_collected_without_a_chip_detail_loses_its_old_one()
    {
        var merged = CollectionStatusMemory.MergeCollectedDetails(
            Memory(("facewear", "Optional hover copy.")), Snapshot(collected: new[] { "facewear" }));

        Assert.False(merged.ContainsKey("facewear"));
    }
}
