using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides <b>when</b> the plugin should upload, and what it should collect when it does.
/// </summary>
/// <remarks>
/// <para>
/// Pure bookkeeping: it never reads the game, never touches the network, and never asks the clock
/// what time it is. Every method takes <c>now</c> as an argument instead, which is what lets the
/// whole thing be unit-tested without waiting real seconds. The caller (<c>SyncManager</c>) owns the
/// clock and does the actual work.
/// </para>
/// <para>
/// Three sources of work, in descending priority: a user-driven request (<c>manual</c>, <c>login</c>),
/// a debounced burst of unlock events, and the periodic full sweep. A backoff deadline suppresses all
/// three without discarding any of them.
/// </para>
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
    /// Guards every field below. <c>NotifyUnlock</c>, <c>Poll</c> and <c>SyncResumed</c> run on the
    /// framework thread, but <c>MarkUploaded</c>, <c>BackOffUntil</c> and
    /// <c>BackOffUntilSyncResumes</c> run on a background task once the HTTP call finishes, so the
    /// state really is touched from more than one thread.
    /// </summary>
    /// <remarks>
    /// C# note: <c>lock (x) { … }</c> lets one thread at a time into the block. JavaScript has no
    /// equivalent because its runtime is single-threaded — this is the cost of real concurrency.
    /// </remarks>
    private readonly object gate = new();

    /// <summary>Categories unlocked since the last upload, deduplicated.</summary>
    private readonly HashSet<string> pendingUnlockCategories = [];

    /// <summary>When the current debounce window closes. Meaningless if no categories are pending.</summary>
    private DateTimeOffset unlockDueAt;

    /// <summary>A queued full sweep, if the user (or a login) asked for one.</summary>
    private SyncTrigger? pendingFullSweep;

    /// <summary>
    /// When the last full sweep happened. Null until one has, so the interval clock never fires first;
    /// only a sweep owed on resume (see <see cref="intervalSweepOwed"/>) can come before it.
    /// </summary>
    private DateTimeOffset? lastFullSweepAt;

    /// <summary>The moment a server-instructed backoff expires. Null when not backing off.</summary>
    private DateTimeOffset? backoffUntil;

    /// <summary>
    /// The deadline of a wait that syncing coming back on ends early (see
    /// <see cref="BackOffUntilSyncResumes"/>). Null when not waiting on one.
    /// </summary>
    private DateTimeOffset? syncResumesBy;

    /// <summary>
    /// The trigger of the upload a switched-off 503 refused, sent again as a sweep when
    /// <see cref="SyncResumed"/> ends the wait. Null when nothing is owed.
    /// </summary>
    private SyncTrigger? owedOnResume;

    /// <summary>
    /// True when a periodic sweep is due at once, whatever the interval clock says: the sweep owed
    /// for a refused interval or unlock upload.
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
    /// The server chooses the cadence, but it does not get to choose an absurd one. A zero or
    /// negative interval would turn the plugin into a request flood against the very server that
    /// configured it, and an enormous one would silently stop it syncing forever. Clamping keeps a
    /// misconfiguration (or a compromised backend, since the backend URL is user-overridable) from
    /// becoming either.
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

    /// <summary>Queues a full sweep. Only <see cref="SyncTrigger.Login"/> and <see cref="SyncTrigger.Manual"/> qualify.</summary>
    /// <exception cref="ArgumentOutOfRangeException">For a trigger the caller does not get to request.</exception>
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
    /// <param name="trigger">
    /// The sweep's trigger: <see cref="SyncTrigger.Login"/> or <see cref="SyncTrigger.Manual"/>.
    /// </param>
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
    /// The window <b>slides</b>: each new unlock pushes the deadline out, so a quest turn-in that
    /// fires a mount, a minion, and three achievements a second apart produces one upload rather
    /// than five. Assigning the deadline outright (rather than keeping the later of the two) also
    /// means a clock that jumps backwards cannot leave a window that never closes.
    /// </remarks>
    public void NotifyUnlock(string categoryKey, DateTimeOffset now)
    {
        lock (gate)
        {
            pendingUnlockCategories.Add(categoryKey);
            unlockDueAt = now + UnlockDebounce;
        }
    }

    /// <summary>Suppresses all work until the given moment, as instructed by the server.</summary>
    /// <remarks>
    /// Pending work is deferred, never dropped — a 429 mid-login must not cost that sync. Note the
    /// deadline is absolute, so a clock that jumps backwards lengthens a backoff rather than ending
    /// it early. That errs toward waiting too long, which is the safe direction against a server that
    /// asked us to stop; the interval clock, where over-waiting costs the user data, is clamped instead.
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
    /// Suppresses all work until the given moment, or until <see cref="SyncResumed"/>, whichever
    /// comes first: the wait a 503 saying syncing is switched off starts (see
    /// <see cref="RetryPolicy.EndsWhenSyncResumes"/>).
    /// </summary>
    /// <param name="until">When the wait ends on its own.</param>
    /// <param name="refused">The trigger of the upload the 503 refused.</param>
    /// <remarks>
    /// Kept apart from <see cref="BackOffUntil"/>'s deadline, so syncing coming back on ends this
    /// wait without cutting short a rate limit in force at the same time. It never shortens either.
    /// The refused upload is remembered so that an early end can send it again (see
    /// <see cref="SyncResumed"/>).
    /// </remarks>
    public void BackOffUntilSyncResumes(DateTimeOffset until, SyncTrigger refused)
    {
        lock (gate)
        {
            if (syncResumesBy is null || until > syncResumesBy)
                syncResumesBy = until;

            // Two refusals owe the stronger of the two (see OwedRank). `is null` tests for null, like
            // `=== null`.
            if (owedOnResume is null || OwedRank(refused) > OwedRank(owedOnResume.Value))
                owedOnResume = refused;
        }
    }

    /// <summary>
    /// Ends the wait <see cref="BackOffUntilSyncResumes"/> started, because a <c>/config</c> answer
    /// says syncing is back on, and queues again the upload it refused. A
    /// <see cref="BackOffUntil"/> wait stays in force, and holds the queued sweep like any other.
    /// </summary>
    /// <remarks>
    /// The refused upload was handed out and settled, so without this nothing would go up until the
    /// next unlock or interval. A refused login or manual sweep goes again as itself; a refused
    /// interval or unlock upload brings the periodic sweep forward, since a full sweep sends every
    /// category the unlock upload would have.
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
                // A requested sweep already queued covers the refused upload, so only without one is
                // the periodic sweep brought forward.
                intervalSweepOwed = true;
            }

            owedOnResume = null;
        }
    }

    /// <summary>True while a server-instructed wait is in force at the given moment.</summary>
    /// <remarks>
    /// Read-only: unlike <see cref="Poll"/>, asking never ends the wait, so the sync card can ask
    /// every frame.
    /// </remarks>
    /// <param name="now">The moment to ask about.</param>
    public bool IsBackingOff(DateTimeOffset now)
    {
        // `lock` with no braces guards the single statement after it. `x is { } until` matches when x
        // is not null and names the value `until`, so the comparison only runs against a real moment.
        lock (gate)
            return (backoffUntil is { } until && now < until)
                || (syncResumesBy is { } resumesBy && now < resumesBy);
    }

    /// <summary>Forgets all queued work, as when the player logs out.</summary>
    /// <remarks>
    /// <para>
    /// Queued work belongs to the character it was queued for. A mount unlocked just before logout,
    /// uploaded after switching characters, would be filed under the wrong ContentId hash — the
    /// server would record it against a character that never earned it.
    /// </para>
    /// <para>
    /// The backoff deadline deliberately survives, because it is the server talking to the
    /// <i>token</i>, not to the character. Switching characters must not become a way to shake off a
    /// rate limit. The cadence survives too: it came from <c>/config</c> and has nothing to do with
    /// who is logged in.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        lock (gate)
        {
            pendingUnlockCategories.Clear();
            pendingFullSweep = null;

            // An upload owed on resume was for the previous character, like the queued work above.
            owedOnResume = null;
            intervalSweepOwed = false;

            // Forgetting the last sweep means the next login sweeps on its own merits, rather than
            // inheriting an interval measured against the previous character.
            lastFullSweepAt = null;
        }
    }

    /// <summary>Records that an upload completed, restarting the interval clock if it was a full sweep.</summary>
    /// <remarks>
    /// An <c>unlock</c> upload carried only the categories that changed, so it is not a sweep and must
    /// not postpone the next one. Otherwise a player who keeps unlocking things would never run a full
    /// sweep, and the categories nothing unlocked in would drift out of date indefinitely.
    /// </remarks>
    public void MarkUploaded(SyncTrigger trigger, DateTimeOffset now)
    {
        lock (gate)
        {
            if (trigger is not SyncTrigger.Unlock)
                lastFullSweepAt = now;
        }
    }

    /// <summary>
    /// Returns the work that is due right now and removes it from the queue, or null if nothing is.
    /// </summary>
    /// <remarks>
    /// Called once per framework tick. It hands each piece of work out exactly once, so the caller
    /// cannot accidentally upload the same thing on every frame.
    /// </remarks>
    public SyncDue? Poll(DateTimeOffset now)
    {
        lock (gate)
        {
            // The server said wait. Nothing is due, and nothing queued is lost.
            if (backoffUntil is { } until)
            {
                if (now < until)
                    return null;

                backoffUntil = null;
            }

            // The same for a wait that syncing coming back on would have ended sooner. One that runs
            // out on its own owes nothing: the next upload goes on its own schedule.
            if (syncResumesBy is { } resumesBy)
            {
                if (now < resumesBy)
                    return null;

                syncResumesBy = null;
                owedOnResume = null;
            }

            // A clock that jumped backwards past the last sweep would otherwise delay the next one by
            // however far it jumped. Treat the earlier "now" as the new reference point.
            if (lastFullSweepAt is { } lastSweep && now < lastSweep)
                lastFullSweepAt = now;

            // 1. A user-driven sweep wins. It also covers whatever the pending unlock would have
            //    sent, so that unlock is redundant and is dropped rather than uploaded twice.
            if (pendingFullSweep is { } requested)
            {
                pendingFullSweep = null;
                pendingUnlockCategories.Clear();

                // It covers a periodic sweep owed on resume too, for the same reason.
                intervalSweepOwed = false;

                // Anchor the interval clock the moment a sweep is ISSUED, not when it succeeds.
                // Otherwise a login sweep whose upload fails transiently would leave this null, and
                // since only a full sweep ever sets it, the periodic sweep below could never become
                // due again — one unlucky 500 at login would silently kill periodic syncing for the
                // whole session. Anchoring here means the next interval simply retries.
                lastFullSweepAt = now;

                return new SyncDue { Trigger = requested };
            }

            // 2. A settled burst of unlocks: upload exactly the categories that changed.
            if (pendingUnlockCategories.Count > 0 && now >= unlockDueAt)
            {
                var categories = new HashSet<string>(pendingUnlockCategories);
                pendingUnlockCategories.Clear();
                return new SyncDue { Trigger = SyncTrigger.Unlock, Categories = categories };
            }

            // 3. The periodic sweep. Stamping the clock here rather than in MarkUploaded means a
            //    failed upload waits out the next interval instead of being retried every tick.
            //
            //    It also drops any unlock still inside its debounce window. No fact is lost: the
            //    sweep is about to report the very same ids. The only thing the unlock upload would
            //    have added is an acquisition timestamp for the categories it named — and the
            //    contract does not promise that a later upload can supply a date for a row an earlier
            //    upload already created. So we neither rely on that nor spend a request finding out.
            if (intervalSweepOwed || (lastFullSweepAt is { } sweptAt && now >= sweptAt + FullSyncInterval))
            {
                intervalSweepOwed = false;
                lastFullSweepAt = now;
                pendingUnlockCategories.Clear();
                return new SyncDue { Trigger = SyncTrigger.Interval };
            }

            return null;
        }
    }

    /// <summary>Ranks the full-sweep triggers so the stronger user signal wins.</summary>
    private static int Rank(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => 2,
        _ => 1,
    };

    /// <summary>
    /// Ranks a refused upload owed on resume: a requested sweep (manual above login) outranks an
    /// interval or unlock upload, which the periodic sweep covers.
    /// </summary>
    private static int OwedRank(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => 3,
        SyncTrigger.Login => 2,
        _ => 1,
    };

    /// <summary>
    /// Keeps a value within bounds. <c>TimeSpan</c> is comparable, so this is a plain three-way test.
    /// </summary>
    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
