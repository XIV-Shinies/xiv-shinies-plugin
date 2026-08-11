namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// One raw per-tick reading of a FATE table row, reduced to Dalamud-free primitives.
/// </summary>
/// <remarks>
/// A FATE's presence on the table is what makes it "active"; its <c>StartTimeEpoch</c> is a
/// true server-assigned start time, identical for every client in the instance, and doubles
/// as the fingerprint value. A row can sit on the table for a few
/// seconds before the server syncs it — zero epoch, zero position — and the tracker treats
/// that as "not there yet" rather than inventing a bogus fingerprint pair.
/// </remarks>
/// <param name="FateId">The <c>Fate</c> Excel sheet row id (e.g. 1972).</param>
/// <param name="StartEpoch">Unix epoch (whole seconds) the FATE started; 0 until the server syncs the row.</param>
public readonly record struct FateReading(ushort FateId, long StartEpoch);
