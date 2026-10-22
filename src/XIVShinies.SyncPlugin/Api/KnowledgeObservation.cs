using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// A sighting of the character's true knowledge level, the value the review window shows (see
/// <see cref="Occult.KnowledgeObserver"/> for why that window is the only client-side source).
/// </summary>
/// <remarks>
/// It carries its observation time because knowledge is a live stat, not a collection: death without
/// a raise can lower it, so the server keeps the freshest sighting across plugin and Lodestone
/// sources rather than the highest.
/// </remarks>
public sealed record KnowledgeObservation
{
    /// <summary>The knowledge level the window displayed.</summary>
    public required byte Level { get; init; }

    /// <summary>When the window was opened: the sighting's freshness on the wire.</summary>
    public required DateTimeOffset ObservedAt { get; init; }
}
