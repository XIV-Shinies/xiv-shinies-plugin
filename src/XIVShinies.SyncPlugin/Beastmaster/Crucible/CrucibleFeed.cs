using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Takes what the game shows and hands it to the scheduler: the character's visits to the boards,
/// each snapshot filed under the territory its window was read in, and the closing snapshot each window
/// sends once per opening.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot belongs to the place its window was read in, not to where the character stands when the
/// snapshot is handed over. A window that closes just after the character zones out still belongs to
/// the board it showed, so its closing snapshot is filed under that board, and the scheduler sends it
/// ahead of the leave; the entrance's roster pick, closing just after zoning in, is filed under the
/// entrance in the same way.
/// </para>
/// <para>
/// A closing snapshot is the window's last reading, stamped with the moment it closed: the window's
/// values are not read again at the close, so a close in a fight reads nothing.
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
    /// The last reading of each open window that sends a closing snapshot, with the territory it was
    /// read in, by window name.
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
    /// Takes in a window reading: remembered for the window's closing snapshot if it sends one, and
    /// offered as an open snapshot.
    /// </summary>
    /// <param name="windowName">The window's internal name.</param>
    /// <param name="snapshot">What makes a snapshot of the reading.</param>
    /// <param name="territoryTypeId">The territory the window was read in.</param>
    /// <param name="now">The moment it was read.</param>
    public void Read(string windowName, CrucibleSnapshot snapshot, uint territoryTypeId, DateTimeOffset now)
    {
        if (CrucibleWindows.Closes(windowName))
            lastReadings[windowName] = (snapshot, territoryTypeId);

        Offer(snapshot(now, false), territoryTypeId, now);
    }

    /// <summary>
    /// A window closed: its last reading goes in once more, marked closed, under the territory it was
    /// read in. A window with no reading since it opened, or one that sends no closing snapshot, adds
    /// nothing.
    /// </summary>
    /// <param name="windowName">The window's internal name.</param>
    /// <param name="now">The moment it closed.</param>
    public void Close(string windowName, DateTimeOffset now)
    {
        // `Remove(name, out var last)` takes the reading out and hands it back in one step.
        if (lastReadings.Remove(windowName, out var last))
            Offer(last.Snapshot(now, true), last.Territory, now);
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
        if (!CrucibleTerritories.Admits(observation, territoryTypeId))
            return;

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
