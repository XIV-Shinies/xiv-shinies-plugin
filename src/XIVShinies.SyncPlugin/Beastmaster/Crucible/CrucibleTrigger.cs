namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Why a Crucible upload is happening. Travels to the server as the request's <c>trigger</c>
/// field, as the lowercase word (<c>enter</c>, <c>change</c>, <c>heartbeat</c>, <c>leave</c>).
/// </summary>
public enum CrucibleTrigger
{
    /// <summary>The character zoned into a board: the first snapshots of the visit.</summary>
    Enter,

    /// <summary>
    /// One or more kinds changed since the last accepted upload, or the plugin started inside a
    /// board and sends its baseline.
    /// </summary>
    Change,

    /// <summary>An idle upload, with no observations, that tells the server the plugin is there.</summary>
    Heartbeat,

    /// <summary>The character zoned out; carries no observations.</summary>
    Leave,
}
