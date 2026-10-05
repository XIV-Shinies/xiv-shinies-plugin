namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Why a Crucible upload is happening. Travels to the server as the request's <c>trigger</c>
/// field, as the lowercase word (<c>enter</c>, <c>change</c>, <c>heartbeat</c>, <c>leave</c>).
/// </summary>
public enum CrucibleTrigger
{
    /// <summary>
    /// The character zoned into a board, with the visit's first snapshots if any were read.
    /// </summary>
    Enter,

    /// <summary>
    /// Snapshots of kinds whose content changed since the plugin last read them, or the baseline
    /// the plugin sends when it starts inside a board.
    /// </summary>
    Change,

    /// <summary>An idle upload, with no observations, that tells the server the plugin is there.</summary>
    Heartbeat,

    /// <summary>The character zoned out; carries no observations.</summary>
    Leave,
}
