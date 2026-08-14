namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// One raw per-tick reading of a CE container slot, reduced to Dalamud-free primitives.
/// </summary>
/// <remarks>
/// <para>
/// The reader produces one of these per container slot each poll; the tracker turns them into
/// wire statuses. A <c>readonly record struct</c> is a small immutable value type with built-in
/// equality — the closest TS analog is a frozen plain object, except comparison is by value,
/// not reference.
/// </para>
/// <para>
/// <paramref name="PhaseDeadlineEpoch"/> carries the game's <c>StartTimestamp</c> field, which
/// despite its name is the <b>end deadline of the current phase</b> (Register → when
/// registration closes; Battle → when the battle ends). It is a server-assigned Unix epoch,
/// identical for every client in the instance — the raw material of the tracker fingerprint.
/// Zero while the slot is idle or the value has not synced yet.
/// </para>
/// </remarks>
/// <param name="DynamicEventId">The <c>DynamicEvent</c> Excel sheet row id (e.g. 46, or 48 for the tower).</param>
/// <param name="Phase">The slot's current lifecycle phase.</param>
/// <param name="PhaseDeadlineEpoch">Unix epoch (whole seconds) when the current phase ends; 0 if unset.</param>
/// <param name="DurationSeconds">The battle duration (<c>SecondsDuration</c>; CEs 1200, the tower 1800).</param>
public readonly record struct DynamicEventReading(
    ushort DynamicEventId,
    DynamicEventPhase Phase,
    long PhaseDeadlineEpoch,
    int DurationSeconds);
