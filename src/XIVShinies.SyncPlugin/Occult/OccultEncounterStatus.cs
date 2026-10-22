namespace XIVShinies.SyncPlugin.Occult;

/// <summary>The tracker server's three statuses for an occult encounter.</summary>
/// <remarks>
/// The contract (docs/api-contract.md, occult/instance-state) allows only these three. A CE maps
/// Register and Warmup to <see cref="Preparing"/>, Battle to <see cref="Active"/>, and Inactive to
/// <see cref="Down"/>; a FATE is <see cref="Active"/> while on the table and <see cref="Down"/> once
/// removed. Success and failure are not told apart because the game zeroes the state either way.
/// </remarks>
public enum OccultEncounterStatus
{
    /// <summary>A CE's registration or warmup window is open.</summary>
    Preparing,

    /// <summary>The encounter is running: a CE battle underway, or a FATE on the table.</summary>
    Active,

    /// <summary>Not up. The respawn countdown runs from when this was stamped.</summary>
    Down,
}
