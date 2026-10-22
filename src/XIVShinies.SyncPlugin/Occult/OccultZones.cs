namespace XIVShinies.SyncPlugin.Occult;

/// <summary>The Occult Crescent territories the tracker watches.</summary>
/// <remarks>
/// These ids are hardcoded, an exception to "the server's catalog owns the ids", because the client
/// must know where it stands before it decides to upload anything. A new Occult zone means adding its
/// TerritoryType row id here in a release, which ships alongside the server's curation of that zone's
/// tracker categories anyway.
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
