using System;
using System.Collections.Generic;
using System.Text.Json;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>One upload the scheduler hands out.</summary>
/// <param name="Observations">The snapshots, at most one per kind, oldest first.</param>
public sealed record CrucibleUpload(
    CrucibleTrigger Trigger,
    uint TerritoryTypeId,
    IReadOnlyList<CrucibleObservation> Observations);

/// <summary>
/// Decides which Crucible snapshots go up, grouped into which uploads, when, and under which trigger.
/// Snapshots are queued here as windows are read, and entering and leaving a board are reported here.
/// </summary>
/// <remarks>
/// It reads nothing from the game, sends nothing, and keeps no clock. The framework thread feeds and
/// polls it while the background task sending each request settles it, so a lock guards the state.
/// Every upload draws on a budget (see <see cref="BudgetInterval"/>); a retry always goes first, and
/// the heartbeat only when nothing is waiting.
/// </remarks>
public sealed class CrucibleUploadScheduler
{
    /// <summary>
    /// The shortest heartbeat honored, whatever <c>/config</c> says. The backend URL is
    /// user-overridable, so a zero or negative cadence must not become a request flood.
    /// </summary>
    public static readonly TimeSpan MinHeartbeat = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest heartbeat honored, so a bad value cannot leave a live run silent for long.
    /// </summary>
    public static readonly TimeSpan MaxHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a burst of snapshots settles before it uploads, and how long a leave waits. Opening one
    /// window often refreshes another within a second or two, and this gathers them into one upload.
    /// </summary>
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest the front snapshots wait on the sliding debounce, from when the oldest began waiting,
    /// so a window that keeps changing still goes up.
    /// </summary>
    private static readonly TimeSpan MaxChangeWait = TimeSpan.FromSeconds(10);

    /// <summary>How many uploads may go back to back before the budget makes the next one wait.</summary>
    private const int BudgetBurst = 4;

    /// <summary>
    /// How often the budget allows one more upload. Four at once, then one every sixteen seconds, is
    /// at most 229 in any hour, under the endpoint's 240.
    /// </summary>
    private static readonly TimeSpan BudgetInterval = TimeSpan.FromSeconds(16);

    /// <summary>
    /// The least wait before a retry, however soon the server says it may come back. Doubled for each
    /// failure in a row, up to <see cref="MaxRetryWait"/>.
    /// </summary>
    private static readonly TimeSpan MinRetryWait = TimeSpan.FromSeconds(15);

    /// <summary>The cap on the doubled least wait. A server can still ask for longer.</summary>
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an unsent snapshot is kept. With no run active the server keeps a snapshot only 30
    /// minutes past its <c>observedAtUtc</c>, and letting go also stops a long hold piling them up.
    /// </summary>
    /// <remarks>
    /// A request waiting to retry is exempt: it goes again unchanged, nothing newer overtakes it, and
    /// it may carry an enter or a leave.
    /// </remarks>
    private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(30);

    /// <summary>A snapshot, or a marker where the character entered or left a board.</summary>
    private enum EntryType
    {
        Snapshot,
        Enter,
        Leave,
    }

    /// <summary>One queue entry.</summary>
    /// <param name="TerritoryTypeId">The territory it was read in, or the board entered or left.</param>
    /// <param name="Observation">Null for a marker.</param>
    /// <param name="Key">Its kind and territory: the unit change detection and replacement work on.</param>
    /// <param name="Kind">Its kind's wire name.</param>
    /// <param name="Closed">True for a window's closing snapshot.</param>
    /// <param name="WaitingSince">
    /// When it began waiting. A replacement inherits this from the snapshot it replaced.
    /// </param>
    /// <param name="QueuedAt">When a snapshot was queued; unset on a marker.</param>
    private sealed record Entry(
        EntryType Type,
        uint TerritoryTypeId,
        CrucibleObservation? Observation = null,
        string Key = "",
        string Kind = "",
        bool Closed = false,
        DateTimeOffset WaitingSince = default,
        DateTimeOffset QueuedAt = default);

    /// <summary>Guards every field below; the class remarks say which threads call.</summary>
    private readonly object gate = new();

    /// <summary>
    /// Snapshots and markers waiting to go, in the order queued, except where <see cref="Queue"/>
    /// places a snapshot for a place just left.
    /// </summary>
    private readonly List<Entry> queue = [];

    /// <summary>
    /// The content, without its moment, of the last snapshot queued for each kind and territory: what
    /// change detection compares against.
    /// </summary>
    /// <remarks>
    /// The last one queued, not the last one accepted, so content the server refused is not sent
    /// again until it changes.
    /// </remarks>
    private readonly Dictionary<string, string> lastContent = [];

    /// <summary>True between entering a board and leaving it; the heartbeat runs only inside.</summary>
    private bool inside;

    /// <summary>The board of the current visit, which a heartbeat carries.</summary>
    private uint visitTerritory;

    /// <summary>When the waiting snapshots have settled long enough to go.</summary>
    private DateTimeOffset changeDueAt;

    /// <summary>
    /// Where the heartbeat clock counts from: set on entering a board, by every upload handed out, and
    /// when one is accepted or dropped. Null until the first of those, and after a reset.
    /// </summary>
    private DateTimeOffset? heartbeatAnchor;

    /// <summary>
    /// When the budget would next be full if nothing more went. Each upload pushes it one interval
    /// later, and an upload may go while it lies at most <see cref="BudgetBurst"/> - 1 intervals ahead.
    /// </summary>
    private DateTimeOffset? budgetFullAt;

    /// <summary>When the budget was last spent, so a backward clock jump can move it back.</summary>
    private DateTimeOffset? budgetSetAt;

    /// <summary>The upload handed out and not yet settled.</summary>
    private CrucibleUpload? inFlight;

    /// <summary>An upload waiting to be sent again unchanged.</summary>
    private CrucibleUpload? retry;

    private int retriesInARow;

    /// <summary>When the current wait ends (see <see cref="Hold"/>).</summary>
    private DateTimeOffset? waitUntil;

    /// <summary>When the wait was set, so a backward clock jump can move it back.</summary>
    private DateTimeOffset? waitSetAt;

    /// <summary>How long a quiet board waits before a heartbeat.</summary>
    public TimeSpan Heartbeat { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wire name of an observation's kind: <c>board</c>, <c>team</c>, <c>bag</c>, <c>offer</c>,
    /// <c>results</c> or <c>self</c>.
    /// </summary>
    public static string KindOf(CrucibleObservation observation) =>
        observation switch
        {
            CrucibleBoardObservation => "board",
            CrucibleTeamObservation => "team",
            CrucibleBagObservation => "bag",
            CrucibleOfferObservation => "offer",
            CrucibleResultsObservation => "results",
            CrucibleSelfObservation => "self",
            _ => throw new ArgumentException("An unknown observation kind.", nameof(observation)),
        };

    /// <summary>Adopts the server's heartbeat cadence from <c>/config</c>, held within its bounds.</summary>
    public void ApplyHeartbeat(int heartbeatSeconds)
    {
        lock (gate)
        {
            var requested = TimeSpan.FromSeconds(heartbeatSeconds);
            Heartbeat = requested < MinHeartbeat ? MinHeartbeat
                : requested > MaxHeartbeat ? MaxHeartbeat
                : requested;
        }
    }

    /// <summary>The character is inside a board, and its baseline is about to be queued.</summary>
    /// <param name="freshEntry">
    /// True when the character just zoned in, so the baseline goes up as an enter; false when the
    /// plugin started or reloaded with the character already inside, so it goes up as a change.
    /// </param>
    /// <remarks>
    /// Change detection starts over, so the baseline goes up in full: the server needs a first reading
    /// of each visit to compare later ones with. Entering another board, or a fresh entry into the
    /// same one, first leaves the old visit.
    /// </remarks>
    public void NotifyEntered(uint territoryTypeId, DateTimeOffset now, bool freshEntry)
    {
        lock (gate)
        {
            if (inside && (visitTerritory != territoryTypeId || freshEntry))
                queue.Add(new Entry(EntryType.Leave, visitTerritory, WaitingSince: now));

            inside = true;
            visitTerritory = territoryTypeId;
            lastContent.Clear();
            heartbeatAnchor = now;
            changeDueAt = now + ChangeDebounce;

            if (freshEntry)
                queue.Add(new Entry(EntryType.Enter, territoryTypeId, WaitingSince: now));
        }
    }

    /// <summary>Queues a snapshot read from a window, or the character's own HP.</summary>
    /// <param name="observation">
    /// The snapshot. One read earlier than a snapshot queued before it starts a new upload (see
    /// <see cref="TakeBatch"/>).
    /// </param>
    /// <param name="territoryTypeId">The territory it was read in.</param>
    /// <remarks>
    /// A snapshot matching the last of its kind and territory is dropped, unless it is a window's
    /// closing snapshot. A newer snapshot replaces an unsent one of its kind and territory and moves to
    /// the back of its run, but never replaces a closing one or reaches across a marker.
    /// </remarks>
    public void Queue(CrucibleObservation observation, uint territoryTypeId, DateTimeOffset now)
    {
        var kind = KindOf(observation);
        var key = $"{kind}@{territoryTypeId}";
        var content = ContentOf(observation);

        // Only the three window kinds that close carry the flag.
        var closed = observation is CrucibleBoardObservation { Closed: true }
            or CrucibleTeamObservation { Closed: true }
            or CrucibleOfferObservation { Closed: true };

        lock (gate)
        {
            // A closing snapshot goes even when unchanged: it marks the window's final state.
            if (!closed && lastContent.TryGetValue(key, out var last) && last == content)
                return;

            lastContent[key] = content;

            // A snapshot for a place just left, as when a window closes during a zone-out, goes ahead
            // of the unsent markers that ended its visit: an enter elsewhere, or a leave of its board.
            var insertAt = queue.Count;
            for (var index = queue.Count - 1; index >= 0; index--)
            {
                var entry = queue[index];
                if (entry.Type == EntryType.Snapshot)
                    continue;

                if (entry.Type == EntryType.Enter && entry.TerritoryTypeId != territoryTypeId)
                {
                    insertAt = index;
                    continue;
                }

                if (entry.Type == EntryType.Leave && entry.TerritoryTypeId == territoryTypeId)
                    insertAt = index;

                break;
            }

            // A closing snapshot is the window's final word, so a fresh one queues behind it instead
            // of replacing it.
            var waitingSince = now;
            for (var index = insertAt - 1; index >= 0 && queue[index].Type == EntryType.Snapshot; index--)
            {
                if (queue[index].Key != key)
                    continue;

                if (!queue[index].Closed)
                {
                    // It inherits the replaced one's waiting start, so MaxChangeWait still bounds it.
                    waitingSince = queue[index].WaitingSince;
                    queue.RemoveAt(index);
                    insertAt--;
                }

                break;
            }

            var snapshot = new Entry(
                EntryType.Snapshot, territoryTypeId, observation, key, kind, closed, waitingSince, now);
            queue.Insert(insertAt, snapshot);
            changeDueAt = now + ChangeDebounce;
        }
    }

    /// <summary>The character left the board: what it left queued goes up, then the leave.</summary>
    public void NotifyLeft(DateTimeOffset now)
    {
        lock (gate)
        {
            if (!inside)
                return;

            inside = false;
            queue.Add(new Entry(EntryType.Leave, visitTerritory, WaitingSince: now));
        }
    }

    /// <summary>
    /// Returns the upload due now and marks it in flight, or null. Polled once per framework tick.
    /// </summary>
    /// <remarks>
    /// Settle every upload it hands out with <see cref="MarkAccepted"/>, <see cref="MarkRetry"/> or
    /// <see cref="MarkDropped"/>, in a <c>finally</c> block so every ending is covered. Nothing more is
    /// handed out meanwhile, so an unsettled upload holds back every later one until <see cref="Reset"/>.
    /// </remarks>
    public CrucibleUpload? Poll(DateTimeOffset now)
    {
        lock (gate)
        {
            if (inFlight is not null)
                return null;

            BringBackFromTheFuture(now);

            LetGoOfOldSnapshots(now);

            // A wait holds every upload; old snapshots still expire during it (above).
            if (waitUntil is { } until)
            {
                if (now < until)
                    return null;

                waitUntil = null;
            }

            if (!BudgetAllows(now))
                return null;

            // 1. A retry goes first, unchanged.
            if (retry is { } again)
            {
                retry = null;
                return Issue(again, now);
            }

            // 2. Nothing waiting: inside a board, a quiet interval sends a heartbeat.
            if (queue.Count == 0)
            {
                if (inside && heartbeatAnchor is { } since && now >= since + Heartbeat)
                    return Issue(new CrucibleUpload(CrucibleTrigger.Heartbeat, visitTerritory, []), now);

                return null;
            }

            // 3. A leave at the front waits one debounce: a window closing during the zone-out is read
            //    a moment later, and its closing snapshot must still go up before the leave.
            var head = queue[0];
            if (head.Type == EntryType.Leave)
            {
                if (now < head.WaitingSince + ChangeDebounce)
                    return null;

                queue.RemoveAt(0);
                return Issue(new CrucibleUpload(CrucibleTrigger.Leave, head.TerritoryTypeId, []), now);
            }

            // 4. Snapshots and an enter go when the burst settles, the oldest has waited MaxChangeWait,
            //    or a leave is queued, since the board is finished. A replacement keeps its waiting start
            //    but moves to the back of its run, so the oldest is found by scanning to the next marker.
            var oldest = head.WaitingSince;
            for (var index = 1; index < queue.Count && queue[index].Type == EntryType.Snapshot; index++)
            {
                if (queue[index].WaitingSince < oldest)
                    oldest = queue[index].WaitingSince;
            }

            var due = now >= changeDueAt
                || now >= oldest + MaxChangeWait
                || queue.Exists(entry => entry.Type == EntryType.Leave);
            if (!due)
                return null;

            if (head.Type == EntryType.Enter)
            {
                queue.RemoveAt(0);
                return Issue(TakeBatch(CrucibleTrigger.Enter, head.TerritoryTypeId), now);
            }

            return Issue(TakeBatch(CrucibleTrigger.Change, head.TerritoryTypeId), now);
        }
    }

    /// <summary>The server accepted the upload; the heartbeat clock restarts.</summary>
    /// <param name="upload">The upload <see cref="Poll"/> handed out.</param>
    /// <remarks>
    /// Accepting an upload other than the one in flight does nothing, so a request that finishes
    /// after a <see cref="Reset"/> cannot disturb the state that followed it.
    /// </remarks>
    public void MarkAccepted(CrucibleUpload upload, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!IsInFlight(upload))
                return;

            inFlight = null;
            retriesInARow = 0;
            heartbeatAnchor = now;
        }
    }

    /// <summary>
    /// The upload goes again unchanged: after a 429 or 503 with its <c>Retry-After</c>, or a network
    /// failure after a backoff.
    /// </summary>
    /// <param name="upload">The upload <see cref="Poll"/> handed out.</param>
    /// <param name="retryAt">When the server, or the caller's backoff, says it may go again.</param>
    /// <remarks>
    /// The wait is at least <see cref="MinRetryWait"/>, doubled for each failure in a row, so no answer
    /// can become a tight loop. It applies even when the upload is no longer in flight: a 429 or 503
    /// limits the token, and a network failure means the server is unreachable, whichever upload met it.
    /// </remarks>
    public void MarkRetry(CrucibleUpload upload, DateTimeOffset retryAt, DateTimeOffset now)
    {
        lock (gate)
        {
            // 15 s, 30 s, 60 s and so on. Doubling stops at the cap, so no run of failures overflows it.
            var leastWait = MinRetryWait;
            for (var failure = 0; failure < retriesInARow && leastWait < MaxRetryWait; failure++)
                leastWait *= 2;

            if (leastWait > MaxRetryWait)
                leastWait = MaxRetryWait;

            var waitEnds = retryAt > now + leastWait ? retryAt : now + leastWait;
            Hold(waitEnds, now);

            if (!IsInFlight(upload))
                return;

            inFlight = null;
            retry = upload;
            retriesInARow++;
        }
    }

    /// <summary>
    /// The upload was refused in a way resending cannot fix, such as a 400: it is let go, and the
    /// queue carries on.
    /// </summary>
    /// <param name="upload">The upload <see cref="Poll"/> handed out.</param>
    /// <param name="holdUntil">
    /// When given, nothing more goes until then, so a repeating refusal does not repeat at full speed.
    /// </param>
    public void MarkDropped(CrucibleUpload upload, DateTimeOffset now, DateTimeOffset? holdUntil = null)
    {
        lock (gate)
        {
            if (holdUntil is { } until)
                Hold(until, now);

            if (!IsInFlight(upload))
                return;

            inFlight = null;
            retriesInARow = 0;
            heartbeatAnchor = now;
        }
    }

    /// <summary>
    /// Forgets the queue, the visit, the upload in flight, the retry, and change detection's content,
    /// as on logout or a character switch.
    /// </summary>
    /// <remarks>
    /// A wait and the budget survive: they belong to the token, not the character, and logging in
    /// again must not shake them off.
    /// </remarks>
    public void Reset()
    {
        lock (gate)
        {
            queue.Clear();
            lastContent.Clear();
            inside = false;
            heartbeatAnchor = null;
            inFlight = null;
            retry = null;
            retriesInARow = 0;
        }
    }

    /// <summary>Whether this is the very upload in flight.</summary>
    /// <remarks>
    /// By identity, not by value: two heartbeats for the same board are equal records, and a late
    /// answer for one must not settle the other.
    /// </remarks>
    private bool IsInFlight(CrucibleUpload upload) => ReferenceEquals(inFlight, upload);

    /// <summary>Holds everything until the given moment; the later of two holds wins.</summary>
    private void Hold(DateTimeOffset until, DateTimeOffset now)
    {
        if (waitUntil is null || until > waitUntil)
        {
            waitUntil = until;
            waitSetAt = now;
        }
    }

    /// <summary>
    /// Pulls every reference point past now back into line after the clock jumps backwards, so
    /// nothing timed from a later moment stalls by the size of the jump.
    /// </summary>
    private void BringBackFromTheFuture(DateTimeOffset now)
    {
        if (heartbeatAnchor is { } anchor && now < anchor)
            heartbeatAnchor = now;

        if (changeDueAt > now + ChangeDebounce)
            changeDueAt = now + ChangeDebounce;

        // The budget and a wait keep their lengths: each moves back by as far as the clock did.
        if (budgetSetAt is { } spent && now < spent && budgetFullAt is { } full)
        {
            budgetFullAt = full - (spent - now);
            budgetSetAt = now;
        }

        if (waitSetAt is { } set && now < set && waitUntil is { } until)
        {
            waitUntil = until - (set - now);
            waitSetAt = now;
        }

        for (var index = 0; index < queue.Count; index++)
        {
            var entry = queue[index];
            if (entry.WaitingSince > now || entry.QueuedAt > now)
            {
                queue[index] = entry with
                {
                    WaitingSince = entry.WaitingSince > now ? now : entry.WaitingSince,
                    QueuedAt = entry.QueuedAt > now ? now : entry.QueuedAt,
                };
            }
        }
    }

    /// <summary>
    /// Lets go of every snapshot left unsent for longer than <see cref="MaxSnapshotAge"/>; the
    /// markers always stay.
    /// </summary>
    /// <remarks>
    /// A snapshot let go never reached the server, so change detection forgets its content too, and
    /// the same content read again goes up. A newer reading that differs stays the one to compare
    /// against.
    /// </remarks>
    private void LetGoOfOldSnapshots(DateTimeOffset now)
    {
        for (var index = queue.Count - 1; index >= 0; index--)
        {
            var entry = queue[index];
            if (entry.Type != EntryType.Snapshot || now - entry.QueuedAt <= MaxSnapshotAge)
                continue;

            queue.RemoveAt(index);
            if (lastContent.TryGetValue(entry.Key, out var last) && last == ContentOf(entry.Observation!))
                lastContent.Remove(entry.Key);
        }
    }

    /// <summary>Whether the budget allows an upload now. Nothing spent yet always allows one.</summary>
    private bool BudgetAllows(DateTimeOffset now) =>
        budgetFullAt is not { } full || now >= full - (BudgetInterval * (BudgetBurst - 1));

    /// <summary>
    /// Marks an upload in flight, spends one from the budget, and restarts the heartbeat clock.
    /// </summary>
    private CrucibleUpload Issue(CrucibleUpload upload, DateTimeOffset now)
    {
        var from = budgetFullAt is { } full && full > now ? full : now;
        budgetFullAt = from + BudgetInterval;
        budgetSetAt = now;

        inFlight = upload;
        heartbeatAnchor = now;
        return upload;
    }

    /// <summary>
    /// Takes the next upload's snapshots off the front of the queue: the longest run in the given
    /// territory that repeats no kind, never goes back in time, and stops at a marker.
    /// </summary>
    private CrucibleUpload TakeBatch(CrucibleTrigger trigger, uint territory)
    {
        var kinds = new HashSet<string>();
        var observations = new List<CrucibleObservation>();
        string? previousMoment = null;

        var taken = 0;
        while (taken < queue.Count)
        {
            var entry = queue[taken];
            var moment = entry.Observation?.ObservedAtUtc;

            // The moment text is fixed width, so comparing it character by character orders it.
            if (entry.Type != EntryType.Snapshot
                || entry.TerritoryTypeId != territory
                || (previousMoment is not null && string.CompareOrdinal(moment, previousMoment) < 0)
                || !kinds.Add(entry.Kind))
            {
                break;
            }

            observations.Add(entry.Observation!);
            previousMoment = moment;
            taken++;
        }

        queue.RemoveRange(0, taken);
        return new CrucibleUpload(trigger, territory, observations);
    }

    /// <summary>
    /// A snapshot's content without its moment, as JSON text: two snapshots read at different
    /// moments with the same content give the same text.
    /// </summary>
    // Covers every field of the snapshot's own kind, plus the `kind` key. CrucibleFeed compares a
    // closing reading with its last one through this too.
    internal static string ContentOf(CrucibleObservation observation) =>
        JsonSerializer.Serialize(observation with { ObservedAtUtc = string.Empty }, ApiJson.Options);
}
