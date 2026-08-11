namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>POST /occult/instance-state</c>: what the server did with the snapshot.
/// </summary>
public sealed record OccultInstanceStateResponse
{
    /// <summary>True on every 200; error bodies use <see cref="ErrorResponse"/> instead.</summary>
    public bool Ok { get; init; }

    /// <summary>
    /// What happened, per <see cref="OccultOutcomes"/>. <c>unresolved</c> is a normal answer,
    /// not an error: the snapshot carried no timestamped pair to fingerprint with, so no
    /// tracker exists yet — just upload again on the next change.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>The tracker this upload landed on, or null when unresolved.</summary>
    public string? TrackerId { get; init; }

    /// <summary>True when this upload created the tracker (first reporter).</summary>
    public bool? Created { get; init; }
}

/// <summary>The <c>outcome</c> values the contract defines.</summary>
public static class OccultOutcomes
{
    /// <summary>The snapshot matched (or created) a tracker and was applied.</summary>
    public const string Applied = "applied";

    /// <summary>No fingerprintable pair yet; no tracker exists. Retry on the next change.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>A leave was processed; the character's presence is cleared.</summary>
    public const string Left = "left";
}
