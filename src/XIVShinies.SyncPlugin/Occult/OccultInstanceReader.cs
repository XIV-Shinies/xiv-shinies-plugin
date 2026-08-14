using System.Collections.Generic;
// Dalamud's safe wrapper over the game's FATE table.
using Dalamud.Plugin.Services;
// The game's occult public-content director and its CE container, via FFXIVClientStructs.
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Reads the occult instance's live state out of game memory, reducing it to the Dalamud-free
/// reading records the tracker consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Framework thread only</b>, like every game-memory read in this plugin. This is the thin
/// unsafe layer: it makes no decisions (the tracker owns all interpretation) so that the logic
/// stays unit-testable and this class stays small enough to verify by in-game QA.
/// </para>
/// <para>
/// <c>unsafe</c> marks code that works with raw pointers — the game's objects are C++ structs
/// at fixed memory addresses, and FFXIVClientStructs maps them as C# structs reached through
/// pointers. There is no JavaScript analog; this is the systems-programming layer Dalamud
/// plugins sit on.
/// </para>
/// <para>
/// Nothing here reads any other player's data: the CE container and FATE table describe the
/// instance's world state (participant counts are aggregate integers the reader does not even
/// carry forward).
/// </para>
/// </remarks>
internal static class OccultInstanceReader
{
    /// <summary>
    /// Attempts one full read of the occult director's state. False when the director is absent
    /// or its state has not finished syncing from the server — the moments during zone
    /// transitions when the memory is not yet meaningful.
    /// </summary>
    /// <param name="fateTable">Dalamud's live FATE table for the current zone.</param>
    /// <param name="events">One reading per populated CE container slot.</param>
    /// <param name="fates">One reading per FATE table row (pre-init rows included; the tracker filters).</param>
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

        // `&` takes the address of the embedded container struct — a pointer into the director,
        // not a copy — and `->` dereferences a pointer to reach a member (the pointer cousin of
        // `.`). `ref var` binds each loop variable to the slot IN PLACE rather than copying the
        // ~0x1D0-byte struct per iteration; the slots are only read, never written.
        var container = &director->DynamicEventContainer;
        foreach (ref var slot in container->Events)
        {
            // An unpopulated slot (the container always has 16) has no event id; there is no
            // encounter to report.
            if (slot.DynamicEventId == 0)
                continue;

            events.Add(new DynamicEventReading(
                slot.DynamicEventId,
                // Two casts because enums only convert through their underlying integer: the
                // game's enum down to its raw byte, then that byte up into our mirrored enum.
                // Values outside the known range are handled by the tracker (they read as down).
                (DynamicEventPhase)(byte)slot.State,
                // Despite the game field's name, this is the CURRENT PHASE'S END deadline.
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
