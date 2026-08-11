namespace XIVShinies.SyncPlugin.Occult;

/// <summary>The Occult Crescent territories the tracker watches.</summary>
/// <remarks>
/// The one place this plugin hardcodes game ids — a deliberate exception to the
/// "the server's catalog owns the ids" rule, because this is not catalog knowledge: the
/// client has to know where it is standing before it can decide to upload anything at all.
/// A future Occult zone means adding its TerritoryType row id here in a plugin release,
/// which is acceptable: the tracker categories for a new zone need server-side curation
/// shipped in lockstep anyway.
/// </remarks>
public static class OccultZones
{
    /// <summary>South Horn's TerritoryType row id.</summary>
    public const uint SouthHorn = 1252;

    /// <summary>North Horn's TerritoryType row id.</summary>
    public const uint NorthHorn = 1346;

    /// <summary>True when the given territory is an Occult Crescent instance zone.</summary>
    public static bool IsOccultTerritory(uint territoryTypeId) =>
        territoryTypeId is SouthHorn or NorthHorn;
}
