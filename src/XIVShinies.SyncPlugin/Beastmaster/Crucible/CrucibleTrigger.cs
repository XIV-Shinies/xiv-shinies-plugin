namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Why a Crucible upload is happening. Sent as the request's <c>trigger</c> field in lowercase:
/// <c>enter</c>, <c>change</c>, <c>heartbeat</c> or <c>leave</c>.
/// </summary>
public enum CrucibleTrigger
{
    /// <summary>The character zoned into a board; carries the visit's first snapshots, if any.</summary>
    Enter,

    /// <summary>
    /// Snapshots whose content changed since the last reading, or the baseline sent when the plugin
    /// starts inside a board.
    /// </summary>
    Change,

    /// <summary>An idle upload with no observations, telling the server the plugin is there.</summary>
    Heartbeat,

    /// <summary>The character zoned out; carries no observations.</summary>
    Leave,
}
