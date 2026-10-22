using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One assembled "Reading from:" panel: the collection lines and the container lines, kept apart so
/// the window can head each group with its own label.
/// </summary>
/// <remarks>
/// A collection line answers "can the sync see this collection at all?"; a container line answers
/// "where did the item counts come from?". Two lists keep them apart without relying on order.
/// </remarks>
public sealed record ReadStatus
{
    /// <summary>One line per collection worth reporting on, ordered by label.</summary>
    public required IReadOnlyList<SourceNote> Collections { get; init; }

    /// <summary>
    /// One line per storage container the item pass looked at (inventory, saddlebag, armoire and so
    /// on), in the order the pass reported them.
    /// </summary>
    public required IReadOnlyList<SourceNote> Containers { get; init; }
}

/// <summary>
/// Assembles every line of the settings window's "Reading from:" panel: what the sync read this
/// session, and what it missed.
/// </summary>
/// <remarks>
/// Reads no game state and names no category or source: a collection's line comes from its row and a
/// container's from <see cref="SourceNoteText.Describe"/>. A new collection or container needs no
/// change here.
/// </remarks>
public static class ReadStatusView
{
    /// <summary>
    /// Every line the panel should draw, split into its collection lines and its container lines.
    /// </summary>
    /// <param name="rows">
    /// The settings window's category rows, the list the consent card draws from (see
    /// <see cref="CategorySettingsView.Build"/>). Only <see cref="CategorySettingsRow.IsEffectivelyOn"/>
    /// rows produce a line: beside a collection switched off, "not read" would read as a fault rather
    /// than a decision.
    /// </param>
    /// <param name="sourceNotes">
    /// Per-container scan status from the latest item pass, keyed by <see cref="SourceKeys"/>. Empty
    /// before any pass has looked at the item sources.
    /// </param>
    /// <param name="host">
    /// The configured website's address. Lines that name the website use it, and it fills a
    /// collector's <see cref="HostPlaceholder.Token"/>.
    /// </param>
    public static ReadStatus Build(
        IReadOnlyList<CategorySettingsRow> rows,
        IReadOnlyDictionary<string, ItemSourceStatus> sourceNotes,
        string host)
    {
        var collections = new List<SourceNote>(rows.Count);
        var containers = new List<SourceNote>(sourceNotes.Count);

        // Containers are read only for a manifest-driven collection. With every such collection off,
        // their lines would urge the user to open a saddlebag nothing is scanning.
        var anyManifestCollectionOn = false;
        foreach (var row in rows)
        {
            if (row.UsesItemManifest && row.IsEffectivelyOn)
            {
                anyManifestCollectionOn = true;
                break;
            }
        }

        // Containers first: DescribeCollection's manifest-driven rule needs to know if any line resulted.
        if (anyManifestCollectionOn)
        {
            foreach (var (sourceKey, status) in sourceNotes)
            {
                // Null means nothing to report, so the container is skipped rather than shown as a raw
                // wire string such as "unscanned".
                if (SourceNoteText.Describe(sourceKey, status, host) is not { } note)
                    continue;

                containers.Add(note);
            }
        }

        foreach (var row in rows)
        {
            if (!row.IsEffectivelyOn)
                continue;

            if (DescribeCollection(row, containers.Count > 0, host) is { } note)
                collections.Add(note);
        }

        // Sorted by label, ordinal and case-blind, to match the consent checklist in every locale.
        // Containers keep the pass's order, which lists the ones a user can act on ahead of the one
        // nothing can change; that order comes from a dictionary and is not promised.
        collections.Sort(ByLabel);

        return new ReadStatus { Collections = collections, Containers = containers };
    }

    /// <summary>
    /// Orders two chips by label. Two collections may share a display name, so the note's text breaks
    /// the tie.
    /// </summary>
    private static int ByLabel(SourceNote left, SourceNote right)
    {
        var byLabel = string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        return byLabel != 0
            ? byLabel
            : string.Compare(left.Text, right.Text, StringComparison.Ordinal);
    }

    /// <summary>One collection's note, or null when it adds nothing to the panel.</summary>
    /// <remarks>
    /// A full read is a chip; a partial read or a miss is a "<c>{DisplayName}: {phrase}</c>" line (see
    /// <see cref="CollectSkipReasons.Describe"/>). Null for a deliberate skip (see
    /// <see cref="CollectSkipReasons.IsDeliberate"/>), and for a healthy manifest-driven collection
    /// while the panel has a container line, because its facts are the container counts; with no
    /// container line, dropping it would leave the panel silent about a collection the user switched
    /// on. A missed manifest-driven collection always keeps its line, because its reason appears
    /// nowhere else.
    /// </remarks>
    /// <param name="row">The category row this line is about.</param>
    /// <param name="hasContainerLines">
    /// Whether the panel's container group has at least one line. Asked of the lines, not the raw
    /// statuses: a status <see cref="SourceNoteText.Describe"/> has no copy for draws nothing, so it
    /// cannot stand in for this line.
    /// </param>
    /// <param name="host">The configured website's address (see <see cref="Build"/>).</param>
    private static SourceNote? DescribeCollection(
        CategorySettingsRow row, bool hasContainerLines, string host)
    {
        // No skip reason: the pass read this collection, in full or in part.
        if (row.SkipReason is not { } reason)
        {
            // Read in part. Missing is the honest tone for the unread half, which must stay visible
            // rather than hide in a hover. Never dropped for a manifest-driven row: no container line
            // says this.
            if (row.PartialNote is { } partialNote)
            {
                return new SourceNote
                {
                    Label = row.DisplayName,
                    Text = $"{row.DisplayName}: {HostPlaceholder.Fill(partialNote, host)}",
                    Tone = SourceTone.Missing,
                };
            }

            // Healthy: a chip with the collection's name and any hover copy the collector attached.
            // A hover may carry information, never a required action (see SourceNote).
            return row.UsesItemManifest && hasContainerLines
                ? null
                : new SourceNote
                {
                    Label = row.DisplayName,
                    Tone = SourceTone.Live,
                    Detail = row.CollectedDetail is { } detail ? HostPlaceholder.Fill(detail, host) : null,
                };
        }

        if (CollectSkipReasons.IsDeliberate(reason))
            return null;

        if (CollectSkipReasons.Describe(reason, host) is { } hint)
        {
            return new SourceNote
            {
                Label = row.DisplayName,
                Text = $"{row.DisplayName}: {hint}",
                Tone = SourceTone.Missing,
            };
        }

        // No advice for this reason, such as a collector bug or an unloadable game sheet. The
        // collection was still missed, so it still gets a line.
        return new SourceNote
        {
            Label = row.DisplayName,
            Text = $"{row.DisplayName}: could not be read.",
            Tone = SourceTone.Missing,
        };
    }
}
