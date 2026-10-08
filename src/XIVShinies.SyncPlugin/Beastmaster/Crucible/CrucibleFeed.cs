using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>Which reading a window's close sent, if any.</summary>
// An `enum` is a fixed set of named values, like a TypeScript union of string literals.
public enum CrucibleCloseOutcome
{
    /// <summary>
    /// No remembered reading was waiting for this close (none was admitted since the window opened, or
    /// an earlier close event already took it), so nothing went up.
    /// </summary>
    NoOpening,

    /// <summary>
    /// The window as read at the close went up, its content differing from the last reading.
    /// </summary>
    ReadAtClose,

    /// <summary>
    /// The window as read at the close went up, with the same content as the last reading.
    /// </summary>
    ReadAtCloseUnchanged,

    /// <summary>The window could not be read at the close, so the last reading went up.</summary>
    LastReading,

    /// <summary>
    /// The read at the close was refused where the window was open, so the last reading went up.
    /// </summary>
    LastReadingAtCloseRefused,
}

/// <summary>
/// Takes what the game shows and hands it to the scheduler: the character's visits to the boards,
/// each snapshot filed under the territory its window was open in, and the closing snapshot each window
/// sends once per opening.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot belongs to the place its window was open in, not to where the character stands when the
/// snapshot is handed over. A window that closes just after the character zones out still belongs to
/// the board it showed, so its closing snapshot is filed under that board, and the scheduler sends it
/// ahead of the leave; the entrance's roster pick, closing just after zoning in, is filed under the
/// entrance in the same way.
/// </para>
/// <para>
/// A closing snapshot is the window as read at the close, falling back to its last admitted reading
/// (see <see cref="Read"/>) when the close could not be read or its territory refuses that read,
/// stamped with the moment it closed (see <see cref="Close"/>).
/// </para>
/// <para>
/// Only the game's main thread calls it.
/// </para>
/// </remarks>
// A class (not a record) because it holds changing state; `sealed` means nothing can subclass it.
public sealed class CrucibleFeed
{
    private readonly CrucibleUploadScheduler scheduler;
    private readonly CrucibleVisits visits = new();

    /// <summary>
    /// The last admitted reading of each open window that sends a closing snapshot, with the territory
    /// it was read in, by window name.
    /// </summary>
    /// <remarks>
    /// Taken out when the window closes, so a window that raises more than one close event sends one
    /// closing snapshot per opening.
    /// </remarks>
    // `(CrucibleSnapshot Snapshot, uint Territory)` is a tuple with named parts: two values traveling
    // together. `[]` is an empty collection of the declared type.
    private readonly Dictionary<string, (CrucibleSnapshot Snapshot, uint Territory)> lastReadings = [];

    /// <summary>Feeds the given scheduler.</summary>
    /// <param name="scheduler">The scheduler the snapshots and visits go to.</param>
    public CrucibleFeed(CrucibleUploadScheduler scheduler)
    {
        this.scheduler = scheduler;
    }

    /// <summary>The board being visited, or null when the character is not in one.</summary>
    // `uint?` is a territory id that may be null, like `number | null`. `=>` makes the expression the
    // property's whole getter.
    public uint? Board => visits.Board;

    /// <summary>
    /// True from entering a board until a bag snapshot goes in: the run HUD is not redrawn until
    /// something in it changes, so the caller keeps trying to read it while this is set.
    /// </summary>
    // `{ get; private set; }`: anyone may read it, only this class may change it.
    public bool BagPending { get; private set; }

    /// <summary>
    /// Brings the visit up to date with the territory the character stands in, and tells the scheduler
    /// of an entry or a leave.
    /// </summary>
    /// <param name="territoryTypeId">The territory the character stands in.</param>
    /// <param name="now">The current moment.</param>
    /// <returns>What changed, so the caller can read a new visit's baseline.</returns>
    public CrucibleVisitChange Follow(uint territoryTypeId, DateTimeOffset now)
    {
        var change = visits.Update(territoryTypeId);

        // A `switch` statement runs the `case` whose value matches, like TypeScript's.
        switch (change)
        {
            // Zoning in sends the baseline as an enter; starting inside sends it as a change.
            case CrucibleVisitChange.Entered:
            case CrucibleVisitChange.AlreadyInside:
                // `freshEntry: ...` names the parameter it fills, so the call says what the flag means.
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
    /// A reading its territory refuses (see <see cref="CrucibleTerritories.Admits"/>) is dropped
    /// whole, and the window's remembered reading stays the last one that could go up. So a window
    /// redrawn on the first frames after crossing between the entrance and a board still closes on
    /// the side it was open: the roster pick under the entrance, a board's window under that board.
    /// </remarks>
    /// <param name="windowName">The window's internal name.</param>
    /// <param name="snapshot">What makes a snapshot of the reading.</param>
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
    /// A window closed: its closing snapshot goes in, marked closed, under the territory its last
    /// admitted reading was taken in. A window with no admitted reading since it opened, or one that
    /// sends no closing snapshot, adds nothing.
    /// </summary>
    /// <remarks>
    /// The snapshot is the window as read at the close, which is its final state: a window's values
    /// can change without a redraw (a purchase, a pick, loot taken). The last admitted reading stands
    /// in when the window could not be read at the close, or when that read is one its territory
    /// refuses, so every close that finds a remembered reading sends a closing snapshot.
    /// </remarks>
    /// <param name="windowName">The window's internal name.</param>
    /// <param name="now">The moment it closed.</param>
    /// <param name="atClose">
    /// What makes a snapshot of the window as read at the close, or null when it could not be read
    /// then. Defaults to null.
    /// </param>
    /// <returns>Which reading went up, for the caller's log.</returns>
    // `CrucibleSnapshot? atClose = null` is an optional parameter that may hold no function, like
    // `atClose?: CrucibleSnapshot` in TypeScript.
    public CrucibleCloseOutcome Close(
        string windowName, DateTimeOffset now, CrucibleSnapshot? atClose = null)
    {
        // `Remove(name, out var last)` takes the reading out and hands it back in one step (see
        // lastReadings for why a second close event then finds nothing).
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
    /// <param name="observation">The snapshot.</param>
    /// <param name="territoryTypeId">The territory it was read in.</param>
    /// <param name="now">The current moment.</param>
    public void Offer(CrucibleObservation observation, uint territoryTypeId, DateTimeOffset now)
    {
        if (CrucibleTerritories.Admits(observation, territoryTypeId))
            Queue(observation, territoryTypeId, now);
    }

    /// <summary>Hands an admitted snapshot to the scheduler.</summary>
    /// <param name="observation">The snapshot.</param>
    /// <param name="territoryTypeId">The territory it was read in.</param>
    /// <param name="now">The current moment.</param>
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
