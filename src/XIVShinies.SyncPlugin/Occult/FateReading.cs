namespace XIVShinies.SyncPlugin.Occult;

/// <summary>One per-tick reading of a FATE table row, in Dalamud-free primitives.</summary>
/// <remarks>
/// A FATE is active while it is on the table, and its server-assigned start time, the same for every
/// client in the instance, doubles as the fingerprint value. A row can sit on the table for a few
/// seconds with a zero epoch and position before the server syncs it. The tracker treats that row as
/// not there yet, so it never sends a bogus fingerprint pair.
/// </remarks>
/// <param name="FateId">The <c>Fate</c> sheet row id, such as 1972.</param>
/// <param name="StartEpoch">Unix seconds the FATE started; 0 until the server syncs the row.</param>
public readonly record struct FateReading(ushort FateId, long StartEpoch);
