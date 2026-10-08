using System;
using System.Collections.Generic;
using System.Text.Json;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>One upload the scheduler hands out: its trigger, its territory, and its snapshots.</summary>
/// <param name="Trigger">Why it is being sent.</param>
/// <param name="TerritoryTypeId">The territory it concerns.</param>
/// <param name="Observations">The snapshots, at most one per kind, oldest first.</param>
// A positional `record` is an immutable data type declared by its parameter list alone: each
// parameter becomes a read-only property. `sealed` means nothing can subclass it. `uint` is a
// whole number that can never be negative (an unsigned integer). `IReadOnlyList<T>` is a list its
// holder cannot change, like `readonly T[]`.
public sealed record CrucibleUpload(
    CrucibleTrigger Trigger,
    uint TerritoryTypeId,
    IReadOnlyList<CrucibleObservation> Observations);

/// <summary>
/// Decides <b>which</b> Crucible snapshots go up, in which uploads, <b>when</b>, and under which
/// trigger word. Snapshots are queued here as each window is read, and entering and leaving a
/// board are reported here.
/// </summary>
/// <remarks>
/// <para>
/// It only decides: it reads nothing from the game, sends nothing itself, and keeps no clock (a
/// method that needs the time is handed it). It is fed and polled on the framework thread and
/// settled from the background task that sends each request, so a lock guards the state.
/// </para>
/// <para>
/// The queue holds the snapshots with a marker wherever the character entered or left a board
/// (see <see cref="Queue"/> for where each snapshot goes, and <see cref="TakeBatch"/> for what one
/// upload takes). Every upload draws on a budget (see <see cref="BudgetInterval"/>). The heartbeat
/// goes only when nothing is waiting, and a retry always goes first.
/// </para>
/// </remarks>
// A class (not a record) because it holds changing state behind a lock.
public sealed class CrucibleUploadScheduler
{
    /// <summary>The shortest heartbeat the plugin will honor, whatever <c>/config</c> says.</summary>
    /// <remarks>
    /// The backend URL is user-overridable, so a zero or negative cadence from a misconfigured or
    /// hostile server must not become a request flood.
    /// </remarks>
    // `static readonly` rather than `const`: a `TimeSpan` (a length of time) is built when the
    // program runs, and `const` only takes values fixed when the code compiles.
    public static readonly TimeSpan MinHeartbeat = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest heartbeat the plugin will honor, so a misconfigured or hostile value cannot leave
    /// a live run silent for long.
    /// </summary>
    public static readonly TimeSpan MaxHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a burst of snapshots gets to settle before it uploads, and how long a leave waits
    /// before it goes (see <see cref="Poll"/>). Opening one window often refreshes another within a
    /// second or two; the sliding wait gathers them into one upload.
    /// </summary>
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest the snapshots at the front wait for the sliding debounce, counted from when the
    /// oldest of them started waiting (a replacement inherits the waiting start of the one it
    /// replaced), so a window that keeps changing still goes up.
    /// </summary>
    private static readonly TimeSpan MaxChangeWait = TimeSpan.FromSeconds(10);

    /// <summary>How many uploads may go at once before the budget makes the next one wait.</summary>
    private const int BudgetBurst = 4;

    /// <summary>
    /// How often the budget allows one more upload. Four at once and then one every sixteen
    /// seconds is at most 229 in any hour, under the endpoint's 240.
    /// </summary>
    private static readonly TimeSpan BudgetInterval = TimeSpan.FromSeconds(16);

    /// <summary>The least wait before a retry, however soon the server says it may come back.</summary>
    /// <remarks>Doubled for each failure in a row, up to <see cref="MaxRetryWait"/>.</remarks>
    private static readonly TimeSpan MinRetryWait = TimeSpan.FromSeconds(15);

    /// <summary>The cap on the doubled least wait. A server can still ask for longer.</summary>
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an unsent snapshot is kept. With no run active, the server keeps a snapshot only
    /// until 30 minutes past its <c>observedAtUtc</c>, so it would not keep an older one; letting it
    /// go also stops a long hold from piling up snapshots.
    /// </summary>
    /// <remarks>
    /// A request waiting to be retried is not held to this age: it goes again as the very same
    /// request, nothing newer overtakes it, and it may carry an enter or a leave.
    /// </remarks>
    private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(30);

    /// <summary>What a queue entry is: a snapshot, or a marker where the character entered or left.</summary>
    private enum EntryType
    {
        Snapshot,
        Enter,
        Leave,
    }

    /// <summary>One queue entry and what the queue needs to know about it.</summary>
    /// <param name="Type">A snapshot, or an enter or leave marker.</param>
    /// <param name="TerritoryTypeId">The territory it was read in, or the board entered or left.</param>
    /// <param name="Observation">The snapshot itself; null for a marker.</param>
    /// <param name="Key">Its kind and territory, the unit change detection and replacement work on.</param>
    /// <param name="Kind">Its kind's wire name.</param>
    /// <param name="Closed">Whether it is a window's closing snapshot.</param>
    /// <param name="WaitingSince">
    /// For a snapshot, when the one it replaced started waiting, or its own queuing; for a marker,
    /// when it was queued.
    /// </param>
    /// <param name="QueuedAt">When a snapshot was queued; left at its default on a marker.</param>
    // A record declared inside the class and `private` to it: only this class can see it. A `?`
    // after a type means the value may be null, like `T | null`. `default` is a type's zero value:
    // false, zero, or null.
    private sealed record Entry(
        EntryType Type,
        uint TerritoryTypeId,
        CrucibleObservation? Observation = null,
        string Key = "",
        string Kind = "",
        bool Closed = false,
        DateTimeOffset WaitingSince = default,
        DateTimeOffset QueuedAt = default);

    /// <summary>Guards every field below (see the class remarks for who calls from where).</summary>
    // A plain object used only as the lock's token. `lock (gate) { ... }` lets one thread at a time
    // run the code inside; a page's JavaScript runs on one thread, so React code never needs one.
    private readonly object gate = new();

    /// <summary>
    /// The snapshots and markers waiting to go, in the order they were queued (see
    /// <see cref="Queue"/> for the one exception).
    /// </summary>
    // `[]` is an empty collection of the declared type: a List here, a Dictionary below.
    private readonly List<Entry> queue = [];

    /// <summary>
    /// The content of the last snapshot queued for each kind and territory, without its moment:
    /// what a new snapshot is compared against to tell whether anything changed.
    /// </summary>
    /// <remarks>
    /// Compared against the last snapshot queued rather than the last one accepted, so content the
    /// server refused is not sent again until it changes.
    /// </remarks>
    // A `Dictionary` maps keys to values, like a TypeScript `Map`.
    private readonly Dictionary<string, string> lastContent = [];

    /// <summary>True between entering a board and leaving it; the heartbeat only runs inside.</summary>
    // A field with no `= ...` starts at its type's default.
    private bool inside;

    /// <summary>The board of the current visit, which a heartbeat carries.</summary>
    private uint visitTerritory;

    /// <summary>When the snapshots now waiting have settled long enough to go.</summary>
    private DateTimeOffset changeDueAt;

    /// <summary>
    /// The heartbeat clock's reference point: set on entering a board and by every upload handed
    /// out, and refreshed when one is accepted or dropped. Null until the first of those, and again
    /// after a reset.
    /// </summary>
    private DateTimeOffset? heartbeatAnchor;

    /// <summary>
    /// When the budget would next be full if nothing more went: each upload pushes it one interval
    /// later, and an upload may go while it lies no more than <see cref="BudgetBurst"/> - 1
    /// intervals ahead of now.
    /// </summary>
    private DateTimeOffset? budgetFullAt;

    /// <summary>When the budget was last spent, so a backward clock jump can move it back.</summary>
    private DateTimeOffset? budgetSetAt;

    /// <summary>The upload handed out and not yet settled, or null.</summary>
    private CrucibleUpload? inFlight;

    /// <summary>An upload waiting to be sent again, unchanged, or null.</summary>
    private CrucibleUpload? retry;

    /// <summary>How many failures in a row the current retry follows.</summary>
    private int retriesInARow;

    /// <summary>The moment a wait ends, or null when not waiting.</summary>
    private DateTimeOffset? waitUntil;

    /// <summary>When the wait was set, so a backward clock jump can move it back.</summary>
    private DateTimeOffset? waitSetAt;

    /// <summary>How long a quiet board waits before a heartbeat.</summary>
    // `{ get; private set; }`: anyone may read it, only this class may change it.
    public TimeSpan Heartbeat { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wire name of an observation's kind (<c>board</c>, <c>team</c>, <c>bag</c>, <c>offer</c>,
    /// <c>results</c> or <c>self</c>).
    /// </summary>
    /// <param name="observation">The snapshot.</param>
    // The `=>` after the signature makes the `switch` expression the whole method body. The switch
    // picks the arm whose type matches the value, like a chain of `instanceof` checks; inside it,
    // each `=>` gives an arm's result, and `_` matches anything else. That last arm throws: `throw`
    // can stand where a value is expected, and `nameof(observation)` is the parameter's name as text.
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
    /// <param name="heartbeatSeconds">The cadence the server asked for.</param>
    public void ApplyHeartbeat(int heartbeatSeconds)
    {
        lock (gate)
        {
            // `<` and `>` compare lengths of time directly, as they would compare numbers.
            var requested = TimeSpan.FromSeconds(heartbeatSeconds);
            Heartbeat = requested < MinHeartbeat ? MinHeartbeat
                : requested > MaxHeartbeat ? MaxHeartbeat
                : requested;
        }
    }

    /// <summary>The character is inside a board: its baseline is about to be queued.</summary>
    /// <param name="territoryTypeId">The board's territory.</param>
    /// <param name="now">The current moment.</param>
    /// <param name="freshEntry">
    /// True when the character just zoned in, so the baseline goes up as an enter; false when the
    /// plugin started or reloaded with the character already inside, so it goes up as a change.
    /// </param>
    /// <remarks>
    /// Change detection starts over, so the baseline goes up in full even where it matches what
    /// was sent before: the server needs a first reading of the visit to compare later ones with.
    /// Entering another board while still in one, or a fresh entry into the same one, is a new
    /// visit, so the old visit is left here first.
    /// </remarks>
    public void NotifyEntered(uint territoryTypeId, DateTimeOffset now, bool freshEntry)
    {
        lock (gate)
        {
            // `WaitingSince: now` names the parameter it fills, skipping the optional ones before
            // it; the nearest TypeScript has is an options object.
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
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// A snapshot the same as the last one of its kind in the same territory is dropped, except a
    /// window's closing snapshot, which always goes. Snapshots queue in the order they are handed
    /// in, except that one for a place the character has just left goes ahead of the unsent markers
    /// that ended that visit. A newer snapshot replaces an unsent one of its kind in the same
    /// territory (never a closing one, and never across a marker) and takes the newest place in
    /// its run.
    /// </remarks>
    public void Queue(CrucibleObservation observation, uint territoryTypeId, DateTimeOffset now)
    {
        var kind = KindOf(observation);
        var key = $"{kind}@{territoryTypeId}";
        var content = ContentOf(observation);

        // Only the three window kinds that close carry the flag. `is Type { Closed: true }` matches
        // that type with that value, and `or` joins the three.
        var closed = observation is CrucibleBoardObservation { Closed: true }
            or CrucibleTeamObservation { Closed: true }
            or CrucibleOfferObservation { Closed: true };

        lock (gate)
        {
            // Nothing changed since the last snapshot of this kind and territory, so there is
            // nothing to say. A closing snapshot goes anyway, since it marks the window's final
            // state. `TryGetValue` returns whether the key is there and, when it is, writes its
            // value into `last`.
            if (!closed && lastContent.TryGetValue(key, out var last) && last == content)
                return;

            lastContent[key] = content;

            // Where it goes: at the back, unless it belongs to the place the character has just
            // moved on from, as when a window closes while the character zones out. Such a
            // snapshot goes ahead of the unsent markers that ended its visit: past an enter into
            // somewhere else, and in front of a leave of its own board.
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

            // A newer snapshot replaces an unsent one of its kind and territory, searching back
            // only as far as the marker before it. A closing snapshot is never replaced: it is the
            // window's final word, and a fresh one after it queues behind it instead.
            var waitingSince = now;
            for (var index = insertAt - 1; index >= 0 && queue[index].Type == EntryType.Snapshot; index--)
            {
                if (queue[index].Key != key)
                    continue;

                if (!queue[index].Closed)
                {
                    // The replacement inherits the replaced one's waiting start (see MaxChangeWait).
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
    /// <param name="now">The current moment.</param>
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
    /// Returns the upload due right now and marks it in flight, or null. Polled once per framework
    /// tick; nothing more is handed out until the upload in flight is settled with
    /// <see cref="MarkAccepted"/>, <see cref="MarkRetry"/> or <see cref="MarkDropped"/>.
    /// </summary>
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// Every upload handed out must be settled, however its request ends (a <c>finally</c> block
    /// is the place for it): an upload left in flight holds back every one after it until
    /// <see cref="Reset"/>.
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
            // `is { } until` matches when there is a value and names it.
            if (waitUntil is { } until)
            {
                if (now < until)
                    return null;

                waitUntil = null;
            }

            // The budget holds everything too, until it allows one more upload.
            if (!BudgetAllows(now))
                return null;

            // 1. A retry goes first, as the very same request.
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

            // 3. A leave at the front goes once the snapshots ahead of it have, after a short
            //    wait: a window that closes as the character zones out is read a moment later,
            //    and the wait lets its closing snapshot still go up before the leave.
            var head = queue[0];
            if (head.Type == EntryType.Leave)
            {
                if (now < head.WaitingSince + ChangeDebounce)
                    return null;

                queue.RemoveAt(0);
                return Issue(new CrucibleUpload(CrucibleTrigger.Leave, head.TerritoryTypeId, []), now);
            }

            // 4. Snapshots, and an enter, go once the burst has settled or the oldest of them has
            //    waited the longest wait; a leave behind them sends them at once, since the board
            //    is finished. The oldest is looked for up to the next marker, because a
            //    replacement rejoins the queue behind snapshots that started waiting after it.
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
    /// <param name="now">The current moment.</param>
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
    /// The upload is to be sent again, unchanged, once the given moment has passed: a 429 or 503
    /// with its <c>Retry-After</c>, or a network failure after a backoff.
    /// </summary>
    /// <param name="upload">The upload <see cref="Poll"/> handed out.</param>
    /// <param name="retryAt">When the server, or the caller's backoff, says it may go again.</param>
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// The wait is never shorter than <see cref="MinRetryWait"/>, doubled for each failure in a
    /// row, so no answer can turn into a tight loop of requests. The wait applies even when the
    /// upload is no longer the one in flight: a 429 or 503 limits the token, not one upload, and a
    /// network failure means the server is unreachable whichever upload met it.
    /// </remarks>
    public void MarkRetry(CrucibleUpload upload, DateTimeOffset retryAt, DateTimeOffset now)
    {
        lock (gate)
        {
            // 15 s, 30 s, 60 s and so on, up to the cap. The loop doubles at most until the cap,
            // so the wait can never overflow however many failures came in a row.
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
    /// The upload was refused in a way sending it again cannot fix (a 400), so it is let go and
    /// the queue carries on with what came after it.
    /// </summary>
    /// <param name="upload">The upload <see cref="Poll"/> handed out.</param>
    /// <param name="now">The current moment.</param>
    /// <param name="holdUntil">
    /// When given, nothing more goes until then, so a refusal that keeps happening does not keep
    /// happening at full speed.
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
    /// Forgets everything queued, the visit, the upload in flight, the retry, and the content change
    /// detection compares against, as when the player logs out or switches character.
    /// </summary>
    /// <remarks>
    /// A wait and the budget survive: they are about the token, not the character, and logging in
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
    /// Pulls every reference point that lies past now back into line after the clock jumps
    /// backwards, so nothing timed from a later moment stalls by however far the clock jumped.
    /// </summary>
    private void BringBackFromTheFuture(DateTimeOffset now)
    {
        // The heartbeat anchor clamps to now, and the debounce to one debounce from now.
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
                // `with { ... }` copies a record with the listed properties replaced, like
                // `{ ...entry, waitingSince: now }` in TypeScript.
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
    /// the same content read again goes up. It forgets only content still current for that kind and
    /// territory: a newer reading that differs stays the one to compare against.
    /// </remarks>
    private void LetGoOfOldSnapshots(DateTimeOffset now)
    {
        // Walked from the back, so removing an entry does not shift the ones still to be visited.
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

    /// <summary>Whether the budget allows an upload right now.</summary>
    // `is not { } full` is true when there is no value; otherwise it names the value `full`, and
    // the right of `||`, which runs only then, can use it. No budget spent yet means an upload may
    // go.
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
        // `HashSet.Add` returns false for a kind already in the upload, which ends the run there.
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
    // `with` copies the snapshot as its own kind, so the copy keeps every field that kind carries;
    // serializing it as the base type adds the `kind` key. `internal` so the feed can compare a
    // window's closing reading with its last one the same way.
    internal static string ContentOf(CrucibleObservation observation) =>
        JsonSerializer.Serialize(observation with { ObservedAtUtc = string.Empty }, ApiJson.Options);
}
