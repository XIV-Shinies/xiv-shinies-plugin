namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Why an occult upload is happening. Travels to the server as the payload's
/// <c>trigger</c> field (lower-cased), per docs/api-contract.md § occult/instance-state.
/// </summary>
public enum OccultTrigger
{
    /// <summary>The character just entered an occult instance; first snapshot.</summary>
    Enter,

    /// <summary>Some encounter's status changed (debounced).</summary>
    Change,

    /// <summary>Idle re-upload keeping presence and tracker liveness fresh.</summary>
    Heartbeat,

    /// <summary>The character left the instance; clears presence server-side.</summary>
    Leave,
}
