namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// The tracker server's three-word status vocabulary for an occult encounter.
/// </summary>
/// <remarks>
/// The contract (docs/api-contract.md § occult/instance-state) admits exactly these three
/// values. CEs map Register/Warmup → <see cref="Preparing"/>, Battle → <see cref="Active"/>,
/// Inactive → <see cref="Down"/>; a FATE is <see cref="Active"/> while on the table and
/// <see cref="Down"/> once removed. Success vs. failure is deliberately not modeled — the
/// game zeroes the state either way, so one honest "down" is all the data supports.
/// </remarks>
public enum OccultEncounterStatus
{
    /// <summary>A CE's registration or warmup window is open.</summary>
    Preparing,

    /// <summary>The encounter is running (CE battle underway / FATE on the table).</summary>
    Active,

    /// <summary>Not currently up. The respawn countdown derives from when this was stamped.</summary>
    Down,
}
