namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// The lifecycle phase of a Critical Encounter or the Forked Tower, as the game reports it. The
/// observed cycle is Inactive, Register, Warmup, Battle, then Inactive again.
/// </summary>
/// <remarks>
/// The values mirror the game's <c>DynamicEventState</c>, declared here so the tracker uses no game
/// types; the reader casts the raw byte to this enum. Nothing checks the mirror at run time: an
/// unknown byte reads as down (see <c>OccultEncounterTracker.MapCe</c>), so a patch that changes
/// <c>DynamicEventState</c> needs this enum re-checked.
/// </remarks>
public enum DynamicEventPhase : byte
{
    /// <summary>Not running. Every other field of the slot is zeroed.</summary>
    Inactive = 0,

    /// <summary>The registration window is open.</summary>
    Register = 1,

    /// <summary>Registration closed; the encounter is about to begin.</summary>
    Warmup = 2,

    /// <summary>The encounter is underway.</summary>
    Battle = 3,
}
