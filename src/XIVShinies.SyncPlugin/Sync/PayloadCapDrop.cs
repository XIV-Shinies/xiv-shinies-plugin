namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// One category the contract's caps forced short, and by how much.
/// </summary>
/// <remarks>
/// <see cref="PayloadCapWarnings"/> reports each category once, keyed on <see cref="CategoryKey"/>,
/// so the key travels as its own field and not only inside <see cref="Line"/>.
/// </remarks>
public sealed record PayloadCapDrop
{
    /// <summary>The category that was cut, as it appears in the payload's collections object.</summary>
    public required string CategoryKey { get; init; }

    /// <summary>How many entries were dropped to bring the category down to the cap.</summary>
    public required int DroppedEntries { get; init; }

    /// <summary>The cap that applied, chosen by the category's shape rather than its name.</summary>
    public required int Cap { get; init; }

    /// <summary>
    /// The human-readable description of the cut, which the caller frames as a log line.
    /// </summary>
    /// <remarks>
    /// A cut can be exactly one entry — a category one over its ceiling — so the noun agrees with
    /// the count.
    /// </remarks>
    public string Line =>
        $"{CategoryKey}: dropped {DroppedEntries} {(DroppedEntries == 1 ? "entry" : "entries")} " +
        $"over the contract cap of {Cap}";
}
