using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// What identifies "the same clip" differs per manifest: the item manifest's content is what
// manifestVersion hashes, so a new version is a new clip; the quest-sequence manifest sits
// deliberately outside that hash (docs/api-contract.md), so it can only honestly promise one
// line per session.
public class ManifestTruncationWarningsTests
{
    private static IReadOnlySet<string> Truncated(params string[] keys) => new HashSet<string>(keys);

    [Fact]
    public void Nothing_truncated_says_nothing()
    {
        var warnings = new ManifestTruncationWarnings();

        Assert.Empty(warnings.LinesFor(Truncated(), "v1"));
    }

    [Fact]
    public void A_clipped_manifest_warns_once_for_a_given_version()
    {
        var warnings = new ManifestTruncationWarnings();

        Assert.Single(warnings.LinesFor(Truncated(CategoryKeys.Items), "v1"));
        Assert.Empty(warnings.LinesFor(Truncated(CategoryKeys.Items), "v1"));
        Assert.Empty(warnings.LinesFor(Truncated(CategoryKeys.Items), "v1"));
    }

    [Fact]
    public void A_new_manifest_version_warns_again()
    {
        var warnings = new ManifestTruncationWarnings();
        warnings.LinesFor(Truncated(CategoryKeys.Items), "v1");

        Assert.Single(warnings.LinesFor(Truncated(CategoryKeys.Items), "v2"));
    }

    // A config carrying no version cannot distinguish one clip from the next, but it must still
    // say something the first time rather than staying silent about a real clip.
    [Fact]
    public void A_null_manifest_version_still_warns_once()
    {
        var warnings = new ManifestTruncationWarnings();

        Assert.Single(warnings.LinesFor(Truncated(CategoryKeys.Items), null));
        Assert.Empty(warnings.LinesFor(Truncated(CategoryKeys.Items), null));
    }

    // The quest-sequence manifest is outside the version hash, so a version bump is not evidence
    // its content changed — re-warning on one would be noise about a manifest nobody touched.
    [Fact]
    public void A_manifest_outside_the_version_hash_warns_only_once_per_session()
    {
        var warnings = new ManifestTruncationWarnings();

        Assert.Single(warnings.LinesFor(Truncated(CategoryKeys.QuestSequences), "v1"));
        Assert.Empty(warnings.LinesFor(Truncated(CategoryKeys.QuestSequences), "v2"));
        Assert.Empty(warnings.LinesFor(Truncated(CategoryKeys.QuestSequences), "v3"));
    }

    // Each category keeps its own latch: reporting one must not mark another as said.
    [Fact]
    public void Each_category_is_latched_on_its_own()
    {
        var warnings = new ManifestTruncationWarnings();
        warnings.LinesFor(Truncated(CategoryKeys.Items), "v1");

        var lines = warnings.LinesFor(Truncated(CategoryKeys.Items, CategoryKeys.QuestSequences), "v1");

        var line = Assert.Single(lines);
        Assert.Contains("quest-sequence", line);
    }

    [Fact]
    public void Two_clips_at_once_are_reported_in_catalog_order()
    {
        var warnings = new ManifestTruncationWarnings();

        var lines = warnings.LinesFor(
            Truncated(CategoryKeys.QuestSequences, CategoryKeys.Items), "v1");

        Assert.Equal(2, lines.Count);
        Assert.Contains("item manifest", lines[0]);
        Assert.Contains("quest-sequence manifest", lines[1]);
    }

    // The snapshot's key set is an init-only property any producer can populate, so a key the
    // catalog has no wording for must be passed over rather than logged as a bare key — and it
    // must not latch, because nothing was said.
    [Fact]
    public void A_key_with_no_manifest_is_passed_over()
    {
        var warnings = new ManifestTruncationWarnings();

        Assert.Empty(warnings.LinesFor(Truncated("notACategory"), "v1"));
        Assert.Empty(warnings.LinesFor(Truncated(CategoryKeys.Quests), "v1"));
    }
}
