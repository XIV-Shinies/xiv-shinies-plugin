using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// A sighting of the character's TRUE knowledge level — the value the review window shows
/// (see <see cref="Occult.KnowledgeObserver"/> for why that window is the only client-side
/// source).
/// </summary>
/// <remarks>
/// Carried with its observation time because knowledge is a live stat, not a collection:
/// death without a raise can de-level it, so the server keeps the FRESHEST observation
/// across plugin and Lodestone sources rather than a maximum. A stale sighting with an
/// honest timestamp is useful; a stale sighting passed off as current would not be.
/// </remarks>
public sealed record KnowledgeObservation
{
    /// <summary>The knowledge level the window displayed.</summary>
    public required byte Level { get; init; }

    /// <summary>When the window was opened (the sighting's freshness on the wire).</summary>
    public required DateTimeOffset ObservedAt { get; init; }
}
