namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Why an occult upload is happening. Sent lower-cased as the payload's <c>trigger</c> (see
/// docs/api-contract.md, occult/instance-state).
/// </summary>
public enum OccultTrigger
{
    /// <summary>The character entered an occult instance: the first snapshot.</summary>
    Enter,

    /// <summary>An encounter's status changed, after a debounce.</summary>
    Change,

    /// <summary>An idle re-upload that keeps presence and the tracker alive.</summary>
    Heartbeat,

    /// <summary>The character left the instance; clears presence on the server.</summary>
    Leave,
}
