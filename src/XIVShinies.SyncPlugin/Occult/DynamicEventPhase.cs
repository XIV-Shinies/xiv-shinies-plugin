namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// The lifecycle phase of a Critical Encounter (or the Forked Tower) as the game reports it.
/// </summary>
/// <remarks>
/// The numeric values mirror the game's own <c>DynamicEventState</c> enum exactly — the reader
/// casts the raw byte it finds in the CE container to this type. The enum is redeclared here
/// (rather than using FFXIVClientStructs' one) so the tracker logic stays Dalamud-free and
/// unit-testable; a mismatch would be caught the moment the reader's cast produced an undefined
/// value. The observed full cycle is Inactive → Register → Warmup → Battle → Inactive.
/// </remarks>
public enum DynamicEventPhase : byte
{
    /// <summary>Not currently running. Every other field of the slot is zeroed.</summary>
    Inactive = 0,

    /// <summary>The registration window is open (players can sign up).</summary>
    Register = 1,

    /// <summary>Registration closed; the encounter is about to begin.</summary>
    Warmup = 2,

    /// <summary>The encounter is underway.</summary>
    Battle = 3,
}
