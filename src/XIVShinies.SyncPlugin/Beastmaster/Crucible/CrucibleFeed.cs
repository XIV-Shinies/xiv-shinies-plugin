using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>Which reading a window's close sent, if any.</summary>
public enum CrucibleCloseOutcome
{
    /// <summary>
    /// No remembered reading waited for this close, so nothing went up: none was admitted since the
    /// window opened, or an earlier close event took it.
    /// </summary>
    NoOpening,

    /// <summary>The window as read at the close went up, its content changed from the last reading.</summary>
    ReadAtClose,

    /// <summary>The window as read at the close went up, with the last reading's content.</summary>
    ReadAtCloseUnchanged,

    /// <summary>The window could not be read at the close, so the last reading went up.</summary>
    LastReading,

    /// <summary>
    /// The window's territory refused the read at the close, so the last reading went up.
    /// </summary>
    LastReadingAtCloseRefused,
}

/// <summary>
/// Hands what the game shows to the scheduler: the character's visits to the boards, each snapshot
/// filed under the territory its window was open in, and one closing snapshot per window opening.
/// </summary>
/// <remarks>
/// A snapshot belongs to where its window was open, not where the character stands when it arrives. So
/// a window closing just after a zone-out is filed under the board it showed and goes ahead of the
/// leave, and the roster pick closing just after zoning in is filed under the entrance. Only the
/// game's main thread calls it.
/// </remarks>
public sealed class CrucibleFeed
{
    private readonly CrucibleUploadScheduler scheduler;
    private readonly CrucibleVisits visits = new();

    /// <summary>
    /// The last admitted reading of each open window that sends a closing snapshot, with the territory
    /// it was read in, by window name.
    /// </summary>
    /// <remarks>
    /// Taken out at the close, so a window raising several close events sends one closing snapshot per
    /// opening.
    /// </remarks>
    private readonly Dictionary<string, (CrucibleSnapshot Snapshot, uint Territory)> lastReadings = [];

    /// <summary>Feeds the given scheduler.</summary>
    public CrucibleFeed(CrucibleUploadScheduler scheduler)
    {
        this.scheduler = scheduler;
    }

    /// <summary>The board being visited, or null when the character is not in one.</summary>
    public uint? Board => visits.Board;

    /// <summary>
    /// True from entering a board until a bag snapshot goes in: the run HUD is not redrawn until
    /// something in it changes, so the caller keeps trying to read it while this is set.
    /// </summary>
    public bool BagPending { get; private set; }

    /// <summary>
    /// Brings the visit up to date with the territory the character stands in, and tells the scheduler
    /// of an entry or a leave.
    /// </summary>
    /// <returns>What changed, so the caller can read a new visit's baseline.</returns>
    public CrucibleVisitChange Follow(uint territoryTypeId, DateTimeOffset now)
    {
        var change = visits.Update(territoryTypeId);

        switch (change)
        {
            case CrucibleVisitChange.Entered:
            case CrucibleVisitChange.AlreadyInside:
                scheduler.NotifyEntered(
                    territoryTypeId, now, freshEntry: change == CrucibleVisitChange.Entered);
                BagPending = true;
                break;

            case CrucibleVisitChange.Left:
                scheduler.NotifyLeft(now);
                BagPending = false;
                break;
        }

        return change;
    }

    /// <summary>
    /// Takes in a window reading: one that may go up from where it was read is queued as an open
    /// snapshot, and remembered for the window's closing snapshot if it sends one.
    /// </summary>
    /// <remarks>
    /// A reading its territory refuses (see <see cref="CrucibleTerritories.Admits"/>) is dropped whole,
    /// and the remembered reading stays the last one that could go up. So a window redrawn just after
    /// crossing between the entrance and a board still closes on the side it was open.
    /// </remarks>
    /// <param name="snapshot">Makes a snapshot of the reading.</param>
    /// <param name="territoryTypeId">The territory the window was read in.</param>
    /// <param name="now">The moment it was read.</param>
    public void Read(string windowName, CrucibleSnapshot snapshot, uint territoryTypeId, DateTimeOffset now)
    {
        var observation = snapshot(now, false);
        if (!CrucibleTerritories.Admits(observation, territoryTypeId))
            return;

        if (CrucibleWindows.Closes(windowName))
            lastReadings[windowName] = (snapshot, territoryTypeId);

        Queue(observation, territoryTypeId, now);
    }

    /// <summary>
    /// A window closed: its closing snapshot goes in, marked closed, under the territory of its last
    /// admitted reading.
    /// </summary>
    /// <remarks>
    /// The snapshot is the window as read at the close, its final state, since values can change
    /// without a redraw: a purchase, a pick, loot taken. The last admitted reading stands in when the
    /// close could not be read or its territory refuses that read. A window with no admitted reading
    /// since it opened, or that sends no closing snapshot, adds nothing.
    /// </remarks>
    /// <param name="now">The moment it closed.</param>
    /// <param name="atClose">
    /// Makes a snapshot of the window as read at the close; null when it could not be read then.
    /// </param>
    /// <returns>Which reading went up, for the caller's log.</returns>
    public CrucibleCloseOutcome Close(
        string windowName, DateTimeOffset now, CrucibleSnapshot? atClose = null)
    {
        if (!lastReadings.Remove(windowName, out var last))
            return CrucibleCloseOutcome.NoOpening;

        var lastReading = last.Snapshot(now, true);
        if (atClose is null)
        {
            Offer(lastReading, last.Territory, now);
            return CrucibleCloseOutcome.LastReading;
        }

        var readAtClose = atClose(now, true);
        if (!CrucibleTerritories.Admits(readAtClose, last.Territory))
        {
            Offer(lastReading, last.Territory, now);
            return CrucibleCloseOutcome.LastReadingAtCloseRefused;
        }

        Offer(readAtClose, last.Territory, now);

        // Compared without their moments, the way the scheduler tells a change from a repeat.
        var unchanged = CrucibleUploadScheduler.ContentOf(readAtClose)
            == CrucibleUploadScheduler.ContentOf(lastReading);
        return unchanged ? CrucibleCloseOutcome.ReadAtCloseUnchanged : CrucibleCloseOutcome.ReadAtClose;
    }

    /// <summary>
    /// Queues a snapshot read in the given territory, if it may go up from there (see
    /// <see cref="CrucibleTerritories.Admits"/>).
    /// </summary>
    public void Offer(CrucibleObservation observation, uint territoryTypeId, DateTimeOffset now)
    {
        if (CrucibleTerritories.Admits(observation, territoryTypeId))
            Queue(observation, territoryTypeId, now);
    }

    /// <summary>Hands an admitted snapshot to the scheduler.</summary>
    private void Queue(CrucibleObservation observation, uint territoryTypeId, DateTimeOffset now)
    {
        scheduler.Queue(observation, territoryTypeId, now);

        if (observation is CrucibleBagObservation)
            BagPending = false;
    }

    /// <summary>Forgets the visit, every remembered reading, the pending bag and the queue.</summary>
    public void Reset()
    {
        visits.Reset();
        lastReadings.Clear();
        BagPending = false;
        scheduler.Reset();
    }
}
