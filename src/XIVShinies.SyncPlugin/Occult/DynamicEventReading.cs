namespace XIVShinies.SyncPlugin.Occult;

/// <summary>One per-tick reading of a CE container slot, in Dalamud-free primitives.</summary>
/// <remarks>
/// <paramref name="PhaseDeadlineEpoch"/> carries the game's <c>StartTimestamp</c>, which despite its
/// name is when the current phase ends: when registration closes, or when the battle ends. It is
/// server-assigned and the same for every client in the instance, so it is the raw material of the
/// tracker fingerprint.
/// </remarks>
/// <param name="DynamicEventId">The <c>DynamicEvent</c> row id, such as 46, or 48 for the tower.</param>
/// <param name="Phase">The slot's lifecycle phase.</param>
/// <param name="PhaseDeadlineEpoch">
/// Unix seconds when the current phase ends; 0 while the slot is idle or not yet synced.
/// </param>
/// <param name="DurationSeconds">
/// The battle length (<c>SecondsDuration</c>): 1200 for a CE, 1800 for the tower.
/// </param>
public readonly record struct DynamicEventReading(
    ushort DynamicEventId,
    DynamicEventPhase Phase,
    long PhaseDeadlineEpoch,
    int DurationSeconds);
