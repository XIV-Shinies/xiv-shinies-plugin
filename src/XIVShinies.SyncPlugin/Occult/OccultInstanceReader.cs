using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Reads the occult instance's live state from game memory into the Dalamud-free readings the
/// tracker consumes. It makes no decisions; the tracker interprets them.
/// </summary>
/// <remarks>
/// Framework thread only. It reads the instance's world state and never another player's data: the
/// CE container's participant counts are aggregates the reader does not carry forward.
/// </remarks>
internal static class OccultInstanceReader
{
    /// <summary>
    /// One full read of the occult director's state. False when the director is absent or its state
    /// has not finished syncing, as during a zone transition.
    /// </summary>
    /// <param name="fateTable">Dalamud's live FATE table for the current zone.</param>
    /// <param name="events">One reading per populated CE container slot.</param>
    /// <param name="fates">
    /// One reading per FATE table row, including rows not yet synced; the tracker filters them.
    /// </param>
    public static unsafe bool TryRead(
        IFateTable fateTable,
        out List<DynamicEventReading> events,
        out List<FateReading> fates)
    {
        events = [];
        fates = [];

        var director = PublicContentOccultCrescent.GetInstance();
        if (director == null || !director->StateLoaded)
            return false;

        // A slot is about 0x1D0 bytes, so the container and each slot are read in place, never copied.
        var container = &director->DynamicEventContainer;
        foreach (ref var slot in container->Events)
        {
            // The container always has 16 slots; an unused one has no event id.
            if (slot.DynamicEventId == 0)
                continue;

            events.Add(new DynamicEventReading(
                slot.DynamicEventId,
                // The raw byte goes into the mirror enum; the tracker reads unknown values as down.
                (DynamicEventPhase)(byte)slot.State,
                // Despite its name, this is when the current phase ends.
                slot.StartTimestamp,
                (int)slot.SecondsDuration));
        }

        foreach (var fate in fateTable)
        {
            if (fate == null)
                continue;

            fates.Add(new FateReading(fate.FateId, fate.StartTimeEpoch));
        }

        return true;
    }
}
