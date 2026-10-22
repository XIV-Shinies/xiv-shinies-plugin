namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>POST /api/plugin/v1/crucible/observations</c>: what the server did with
/// the snapshots.
/// </summary>
/// <remarks>
/// Every field but <see cref="Ok"/> is nullable because each outcome carries a different set of
/// them; an absent field reads as null. Error bodies use <see cref="ErrorResponse"/> instead.
/// </remarks>
public sealed record CrucibleObservationsResponse
{
    /// <summary>True on every 200.</summary>
    public bool Ok { get; init; }

    /// <summary>What happened, as one of the <see cref="CrucibleOutcomes"/> words.</summary>
    public string? Outcome { get; init; }

    /// <summary>
    /// The character's active website run, or null when it has none. Only on <c>applied</c> were the
    /// snapshots written to it.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// On <c>applied</c>, how many run events the server wrote for the upload, which can include an
    /// earlier piece's outcome.
    /// </summary>
    public int? Events { get; init; }

    /// <summary>
    /// On <c>applied</c>, how many run events the server did not write because the player had
    /// already logged them on the website.
    /// </summary>
    public int? Skipped { get; init; }

    /// <summary>On <c>board_mismatch</c>, the board the website run is on.</summary>
    public int? BoardId { get; init; }
}

/// <summary>The <c>outcome</c> words the contract defines.</summary>
public static class CrucibleOutcomes
{
    /// <summary>The character has a run on this board, and the snapshots were applied to it.</summary>
    public const string Applied = "applied";

    /// <summary>The server kept the snapshots rather than applying them.</summary>
    /// <remarks>
    /// A board's are held while the character has no active run and applied once a run on that board
    /// starts; the entrance's are held either way and never applied to a run.
    /// </remarks>
    public const string Held = "held";

    /// <summary>The character's run is on a different board; nothing was written to it.</summary>
    public const string BoardMismatch = "board_mismatch";

    /// <summary>The server accepted a leave.</summary>
    public const string Left = "left";
}
