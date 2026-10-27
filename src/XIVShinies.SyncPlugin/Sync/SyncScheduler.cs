using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides <b>when</b> the plugin should upload, and what it should collect when it does.
/// </summary>
/// <remarks>
/// Bookkeeping only: it never reads the game, the network or the clock, and every method that needs
/// the time takes <c>now</c>. Work comes from four sources, in descending priority: a user-driven
/// request (<c>manual</c>, <c>login</c>), a debounced burst of unlocks, the periodic full sweep, and
/// held facts nothing else carried. A backoff suppresses all four without discarding any.
/// </remarks>
public sealed class SyncScheduler
{
    /// <summary>The shortest unlock debounce the plugin will honor.</summary>
    public static readonly TimeSpan MinUnlockDebounce = TimeSpan.FromSeconds(1);

    /// <summary>The longest unlock debounce the plugin will honor.</summary>
    public static readonly TimeSpan MaxUnlockDebounce = TimeSpan.FromSeconds(60);

    /// <summary>The shortest full-sweep interval the plugin will honor.</summary>
    public static readonly TimeSpan MinFullSyncInterval = TimeSpan.FromMinutes(5);

    /// <summary>The longest full-sweep interval the plugin will honor.</summary>
    public static readonly TimeSpan MaxFullSyncInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How long held facts wait for another upload before going on their own, outside a backoff. At
    /// most twelve such uploads an hour, well inside the server's sixty.
    /// </summary>
    public static readonly TimeSpan HeldFactsWait = TimeSpan.FromMinutes(5);

    /// <summary>Guards every field below.</summary>
    /// <remarks>
    /// <c>MarkUploaded</c>, <c>MarkFailed</c> and both <c>BackOffUntil*</c> methods run on the upload
    /// task, <c>ApplyIntervals</c> on the poll task, <c>IsBackingOff</c> on the window's draw call, and
    /// the rest on the framework thread. The interval properties are written under it; the sync card's
    /// draw call reads <c>FullSyncInterval</c> without it.
    /// </remarks>
    private readonly object gate = new();

    /// <summary>Categories unlocked since the last upload, deduplicated.</summary>
    private readonly HashSet<string> pendingUnlockCategories = [];

    /// <summary>When the current debounce window closes. Meaningless if no categories are pending.</summary>
    private DateTimeOffset unlockDueAt;

    /// <summary>Categories whose facts wait for the next upload (see <see cref="NotifyHeld"/>).</summary>
    private readonly HashSet<string> heldCategories = [];

    /// <summary>When held facts go on their own. Meaningless if none are held.</summary>
    private DateTimeOffset heldDueAt;

    /// <summary>A queued full sweep, if the user or a login asked for one.</summary>
    private SyncTrigger? pendingFullSweep;

    /// <summary>
    /// When the last full sweep happened. Null until one has, so the interval never fires first; only
    /// a sweep owed on resume (see <see cref="intervalSweepOwed"/>) can come before it.
    /// </summary>
    private DateTimeOffset? lastFullSweepAt;

    /// <summary>The moment a server-instructed backoff expires. Null when not backing off.</summary>
    private DateTimeOffset? backoffUntil;

    /// <summary>
    /// When the wait that syncing coming back on can end early runs out (see
    /// <see cref="BackOffUntilSyncResumes"/>). Null when not in that wait.
    /// </summary>
    private DateTimeOffset? syncResumesBy;

    /// <summary>
    /// The trigger of the upload a switched-off 503 refused, sent again as a sweep when
    /// <see cref="SyncResumed"/> ends the wait. Null when nothing is owed.
    /// </summary>
    private SyncTrigger? owedOnResume;

    /// <summary>
    /// True when a periodic sweep is due at once, whatever the interval clock says: the sweep owed for
    /// a refused interval or unlock upload.
    /// </summary>
    private bool intervalSweepOwed;

    /// <summary>How long to wait after an unlock before uploading, batching the burst.</summary>
    public TimeSpan UnlockDebounce { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often to run a full-sweep upload.</summary>
    public TimeSpan FullSyncInterval { get; private set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Adopts the server's cadence from <c>/config</c>, clamped to a sane range.
    /// </summary>
    /// <remarks>
    /// A zero or negative interval would flood the server with requests, and an enormous one would
    /// silently stop syncing. The backend URL is user-overridable, so the cadence may come from a
    /// misconfigured or compromised server.
    /// </remarks>
    public void ApplyIntervals(ConfigIntervals intervals)
    {
        lock (gate)
        {
            UnlockDebounce = Clamp(
                TimeSpan.FromSeconds(intervals.UnlockDebounceSeconds), MinUnlockDebounce, MaxUnlockDebounce);
            FullSyncInterval = Clamp(
                TimeSpan.FromMinutes(intervals.FullSyncMinutes), MinFullSyncInterval, MaxFullSyncInterval);
        }
    }

    /// <summary>
    /// Queues a full sweep. Only <see cref="SyncTrigger.Login"/> and <see cref="SyncTrigger.Manual"/>
    /// qualify.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">For any other trigger.</exception>
    public void Request(SyncTrigger trigger, DateTimeOffset now)
    {
        if (trigger is not (SyncTrigger.Login or SyncTrigger.Manual))
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Only login and manual sweeps can be requested.");

        lock (gate)
        {
            // A manual request outranks a queued login: the user is standing at the window watching.
            if (pendingFullSweep is null || Rank(trigger) > Rank(pendingFullSweep.Value))
                pendingFullSweep = trigger;
        }
    }

    /// <summary>
    /// Queues a full sweep like <see cref="Request"/>, unless a full sweep has been handed out or
    /// accepted at or after <paramref name="since"/> (see <see cref="lastFullSweepAt"/>).
    /// </summary>
    /// <param name="trigger"><see cref="SyncTrigger.Login"/> or <see cref="SyncTrigger.Manual"/>.</param>
    /// <param name="since">The moment the reason for the sweep arose.</param>
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// For a request that reaches the scheduler a moment after its reason: a sweep handed out in
    /// between already sent everything this one would, so a second would only spend an upload.
    /// </remarks>
    public void RequestUnlessSweptSince(SyncTrigger trigger, DateTimeOffset since, DateTimeOffset now)
    {
        lock (gate)
        {
            if (lastFullSweepAt is { } sweptAt && sweptAt >= since)
                return;
        }

        Request(trigger, now);
    }

    /// <summary>Records that a category just unlocked, and (re)opens the debounce window.</summary>
    /// <remarks>
    /// The window <b>slides</b>: each unlock pushes the deadline out, so a burst of unlocks a second
    /// apart makes one upload. Assigning the deadline, rather than keeping the later of the two, means
    /// a clock that jumps backwards cannot leave a window that never closes.
    /// </remarks>
    public void NotifyUnlock(string categoryKey, DateTimeOffset now)
    {
        lock (gate)
        {
            pendingUnlockCategories.Add(categoryKey);
            unlockDueAt = now + UnlockDebounce;
        }
    }

    /// <summary>
    /// Records that a category holds facts that can wait for the next upload, up to
    /// <see cref="HeldFactsWait"/>.
    /// </summary>
    /// <remarks>
    /// The facts ride whatever upload goes first, an unlock's included, so they must carry their own
    /// observation times (see <see cref="SyncDue.Categories"/>). If none goes, they go on their own as an
    /// <c>interval</c> upload naming only them. Unlike the unlock debounce, the wait does not slide, so
    /// steady activity uploads at most once per wait.
    /// </remarks>
    public void NotifyHeld(string categoryKey, DateTimeOffset now)
    {
        lock (gate)
        {
            if (heldCategories.Count == 0)
                heldDueAt = now + HeldFactsWait;

            heldCategories.Add(categoryKey);
        }
    }

    /// <summary>Suppresses all work until the given moment, as instructed by the server.</summary>
    /// <remarks>
    /// Pending work is deferred, never dropped, so a 429 mid-login does not cost that sync. The deadline
    /// is absolute, so a clock that jumps backwards lengthens the wait, which is the safe direction
    /// against a server that asked us to stop.
    /// </remarks>
    public void BackOffUntil(DateTimeOffset until)
    {
        lock (gate)
        {
            // Never shorten a backoff already in force.
            if (backoffUntil is null || until > backoffUntil)
                backoffUntil = until;
        }
    }

    /// <summary>
    /// Suppresses all work until <paramref name="until"/> or <see cref="SyncResumed"/>: the wait a 503
    /// <c>sync_disabled</c> starts if <see cref="SyncResumeWait.Arm"/> allows it to end early.
    /// </summary>
    /// <param name="until">When the wait ends on its own.</param>
    /// <param name="refused">The trigger of the upload the 503 refused.</param>
    /// <remarks>
    /// Kept apart from <see cref="BackOffUntil"/>'s deadline, so syncing coming back on ends this wait
    /// without cutting short a rate limit in force at the same time. It never shortens either.
    /// </remarks>
    public void BackOffUntilSyncResumes(DateTimeOffset until, SyncTrigger refused)
    {
        lock (gate)
        {
            if (syncResumesBy is null || until > syncResumesBy)
                syncResumesBy = until;

            // Two refusals owe the stronger of the two (see OwedRank).
            if (owedOnResume is null || OwedRank(refused) > OwedRank(owedOnResume.Value))
                owedOnResume = refused;
        }
    }

    /// <summary>
    /// Ends the <see cref="BackOffUntilSyncResumes"/> wait, because <c>/config</c> says syncing is back
    /// on, and queues the upload it refused again.
    /// </summary>
    /// <remarks>
    /// The refused upload was already handed out, so without this nothing would go up until the next
    /// unlock or interval. A refused login or manual sweep goes again as itself; a refused interval or
    /// unlock upload brings the periodic sweep forward, which sends every category the unlock upload
    /// would have. A <see cref="BackOffUntil"/> wait stays in force and holds the queued sweep.
    /// </remarks>
    public void SyncResumed()
    {
        lock (gate)
        {
            syncResumesBy = null;

            if (owedOnResume is SyncTrigger.Login or SyncTrigger.Manual)
            {
                if (pendingFullSweep is null || Rank(owedOnResume.Value) > Rank(pendingFullSweep.Value))
                    pendingFullSweep = owedOnResume;
            }
            else if (owedOnResume is not null && pendingFullSweep is null)
            {
                // A requested sweep already queued covers the refused upload.
                intervalSweepOwed = true;
            }

            owedOnResume = null;
        }
    }

    /// <summary>True while a server-instructed wait is in force at the given moment.</summary>
    /// <remarks>
    /// Unlike <see cref="Poll"/>, asking never ends the wait, so the sync card can ask every frame.
    /// </remarks>
    /// <param name="now">The moment to ask about.</param>
    public bool IsBackingOff(DateTimeOffset now)
    {
        lock (gate)
            return (backoffUntil is { } until && now < until)
                || (syncResumesBy is { } resumesBy && now < resumesBy);
    }

    /// <summary>Forgets all queued work, as when the player logs out.</summary>
    /// <remarks>
    /// Queued work uploaded after a character switch would be credited to a character that never
    /// earned it. Both waits survive, because they answer the token, not the character, so switching
    /// characters cannot shake off a rate limit. The cadence survives too: it came from <c>/config</c>.
    /// </remarks>
    public void Reset()
    {
        lock (gate)
        {
            pendingUnlockCategories.Clear();
            heldCategories.Clear();
            pendingFullSweep = null;

            // An upload owed on resume belongs to the previous character too.
            owedOnResume = null;
            intervalSweepOwed = false;

            // The next character's interval is not measured from the previous character's sweep.
            lastFullSweepAt = null;
        }
    }

    /// <summary>
    /// Records that an upload completed, restarting the interval clock if it was a full sweep.
    /// </summary>
    /// <remarks>
    /// An upload that names its categories, an unlock's or held facts', is not a sweep and must not
    /// postpone the next one, or a player who keeps unlocking things would never get a full sweep.
    /// </remarks>
    /// <param name="due">The work the upload carried out.</param>
    /// <param name="now">When it completed.</param>
    public void MarkUploaded(SyncDue due, DateTimeOffset now)
    {
        lock (gate)
        {
            if (due.Categories is null)
                lastFullSweepAt = now;
        }
    }

    /// <summary>
    /// Holds again the held categories an upload took, after it failed in a way a later try could fix.
    /// </summary>
    /// <remarks>
    /// Held facts last only the session, so leaving them to the next periodic sweep risks a logout first.
    /// Not for a rejected body: holding it again would send the same rejection every wait.
    /// </remarks>
    /// <param name="due">The work the failed upload was for.</param>
    /// <param name="now">When it failed; a new wait starts here unless facts are already held.</param>
    public void MarkFailed(SyncDue due, DateTimeOffset now)
    {
        foreach (var categoryKey in due.HeldCategories)
            NotifyHeld(categoryKey, now);
    }

    /// <summary>
    /// Returns the work that is due right now and removes it from the queue, or null if nothing is.
    /// </summary>
    /// <remarks>
    /// Called once per framework tick. It hands each piece of work out exactly once.
    /// </remarks>
    public SyncDue? Poll(DateTimeOffset now)
    {
        lock (gate)
        {
            // The server said wait; nothing queued is lost.
            if (backoffUntil is { } until)
            {
                if (now < until)
                    return null;

                backoffUntil = null;
            }

            // Likewise a wait for syncing to resume. One that runs out on its own owes nothing: the
            // next upload goes on its own schedule.
            if (syncResumesBy is { } resumesBy)
            {
                if (now < resumesBy)
                    return null;

                syncResumesBy = null;
                owedOnResume = null;
            }

            // A clock that jumped backwards past the last sweep would delay the next one by the jump,
            // so the earlier "now" becomes the reference point.
            if (lastFullSweepAt is { } lastSweep && now < lastSweep)
                lastFullSweepAt = now;

            // Likewise held facts would wait out the jump on top of their wait.
            if (heldCategories.Count > 0 && heldDueAt > now + HeldFactsWait)
                heldDueAt = now + HeldFactsWait;

            // 1. A user-driven sweep wins. It covers whatever a pending unlock or held facts would have
            //    sent: the unlock is dropped, and the held facts ride along (see SyncDue.HeldCategories).
            if (pendingFullSweep is { } requested)
            {
                pendingFullSweep = null;
                pendingUnlockCategories.Clear();

                // It covers a periodic sweep owed on resume too.
                intervalSweepOwed = false;

                // Anchored when issued, not on success: a login sweep that failed would leave this
                // null, and the periodic sweep below would never come due for the session.
                lastFullSweepAt = now;

                return new SyncDue { Trigger = requested, HeldCategories = TakeHeld() };
            }

            // 2. A settled burst of unlocks: upload exactly the categories that changed, and any held
            //    facts with them (see SyncDue.Categories).
            if (pendingUnlockCategories.Count > 0 && now >= unlockDueAt)
            {
                var held = TakeHeld();
                var categories = new HashSet<string>(pendingUnlockCategories);
                categories.UnionWith(held);
                pendingUnlockCategories.Clear();
                return new SyncDue
                {
                    Trigger = SyncTrigger.Unlock, Categories = categories, HeldCategories = held,
                };
            }

            // 3. The periodic sweep, stamped here so a failed upload waits out the next interval instead
            //    of retrying every tick. It drops any unlock still debouncing: the sweep sends the same
            //    ids, and no later upload is promised to date the quest rows a snapshot leaves undated.
            if (intervalSweepOwed || (lastFullSweepAt is { } sweptAt && now >= sweptAt + FullSyncInterval))
            {
                intervalSweepOwed = false;
                lastFullSweepAt = now;
                pendingUnlockCategories.Clear();
                return new SyncDue { Trigger = SyncTrigger.Interval, HeldCategories = TakeHeld() };
            }

            // 4. Held facts that nothing else carried. An unlock still settling carries them instead,
            //    sparing an upload. Not a sweep, so the interval clock is untouched.
            if (heldCategories.Count > 0 && now >= heldDueAt && pendingUnlockCategories.Count == 0)
            {
                var held = TakeHeld();
                return new SyncDue
                {
                    Trigger = SyncTrigger.Interval, Categories = held, HeldCategories = held,
                };
            }

            return null;
        }
    }

    /// <summary>
    /// Hands out the held facts at once, ahead of their wait, or null when none are held or the server
    /// asked for a wait.
    /// </summary>
    /// <remarks>For a logout, after which held facts would be lost.</remarks>
    public SyncDue? TakeHeldNow(DateTimeOffset now)
    {
        lock (gate)
        {
            if (heldCategories.Count == 0)
                return null;

            if ((backoffUntil is { } until && now < until)
                || (syncResumesBy is { } resumesBy && now < resumesBy))
            {
                return null;
            }

            var held = TakeHeld();
            return new SyncDue
            {
                Trigger = SyncTrigger.Interval, Categories = held, HeldCategories = held,
            };
        }
    }

    /// <summary>Empties the held categories, returning what they were. Call under the lock.</summary>
    private HashSet<string> TakeHeld()
    {
        var held = new HashSet<string>(heldCategories);
        heldCategories.Clear();
        return held;
    }

    /// <summary>Ranks the full-sweep triggers so the stronger user signal wins.</summary>
    private static int Rank(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => 2,
        _ => 1,
    };

    /// <summary>
    /// Ranks a refused upload owed on resume: a requested sweep, manual above login, outranks an
    /// interval or unlock upload, which the periodic sweep covers.
    /// </summary>
    private static int OwedRank(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => 3,
        SyncTrigger.Login => 2,
        _ => 1,
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
