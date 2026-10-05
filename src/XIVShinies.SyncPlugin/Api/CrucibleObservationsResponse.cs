namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>POST /api/plugin/v1/crucible/observations</c>: what the server did with
/// the snapshots.
/// </summary>
/// <remarks>
/// Every field but <see cref="Ok"/> is nullable because each outcome carries a different set of
/// them; an absent field reads as null. Error bodies use <see cref="ErrorResponse"/> instead.
/// </remarks>
// `sealed record` is an immutable data type nothing can subclass; each `{ get; init; }` property
// is filled in by the JSON reader and read-only afterwards.
public sealed record CrucibleObservationsResponse
{
    /// <summary>True on every 200.</summary>
    public bool Ok { get; init; }

    /// <summary>What happened, as one of the <see cref="CrucibleOutcomes"/> words.</summary>
    public string? Outcome { get; init; }

    /// <summary>The website run the snapshots belong to, or null when the character has none.</summary>
    public string? RunId { get; init; }

    /// <summary>On <c>applied</c>, how many run events the snapshots produced.</summary>
    public int? Events { get; init; }

    /// <summary>
    /// On <c>applied</c>, how many snapshots produced nothing because the player had already
    /// logged that piece by hand.
    /// </summary>
    public int? Skipped { get; init; }

    /// <summary>On <c>board_mismatch</c>, the board the website run is on.</summary>
    public int? BoardId { get; init; }
}

/// <summary>The <c>outcome</c> words the contract defines.</summary>
// A `static class` only holds shared members and is never instantiated, and each `const string`
// is a value fixed when the code is compiled: together, a module of exported string constants.
public static class CrucibleOutcomes
{
    /// <summary>The character has a run on this board, and the snapshots were applied to it.</summary>
    public const string Applied = "applied";

    /// <summary>
    /// The character has no active run. The server holds the latest snapshots for a while and
    /// applies them once a run on this board starts.
    /// </summary>
    public const string Held = "held";

    /// <summary>The character's run is on a different board; nothing was written to it.</summary>
    public const string BoardMismatch = "board_mismatch";

    /// <summary>The server accepted a leave.</summary>
    public const string Left = "left";
}
