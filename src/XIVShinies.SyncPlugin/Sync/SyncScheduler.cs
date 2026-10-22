using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides <b>when</b> the plugin should upload, and what it should collect when it does.
/// </summary>
/// <remarks>
/// Bookkeeping only: it never reads the game, the network or the clock, and every method that needs
/// the time takes <c>now</c>. Work comes from three sources, in descending priority: a user-driven
/// request (<c>manual</c>, <c>login</c>), a debounced burst of unlocks, and the periodic full sweep.
/// A backoff suppresses all three without discarding any.
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
    /// Guards every field below: <c>NotifyUnlock</c> and <c>Poll</c> run on the framework thread,
    /// <c>MarkUploaded</c> and <c>BackOffUntil</c> on the upload task.
    /// </summary>
    private readonly object gate = new();

    /// <summary>Categories unlocked since the last upload, deduplicated.</summary>
    private readonly HashSet<string> pendingUnlockCategories = [];

    /// <summary>When the current debounce window closes. Meaningless if no categories are pending.</summary>
    private DateTimeOffset unlockDueAt;

    /// <summary>A queued full sweep, if the user or a login asked for one.</summary>
    private SyncTrigger? pendingFullSweep;

    /// <summary>
    /// When the last full sweep happened. Null until one has, so the interval never fires first.
    /// </summary>
    private DateTimeOffset? lastFullSweepAt;

    /// <summary>The moment a server-instructed backoff expires. Null when not backing off.</summary>
    private DateTimeOffset? backoffUntil;

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

    /// <summary>True while a server-instructed wait is in force at the given moment.</summary>
    /// <remarks>
    /// Unlike <see cref="Poll"/>, asking never ends the wait, so the sync card can ask every frame.
    /// </remarks>
    /// <param name="now">The moment to ask about.</param>
    public bool IsBackingOff(DateTimeOffset now)
    {
        lock (gate)
            return backoffUntil is { } until && now < until;
    }

    /// <summary>Forgets all queued work, as when the player logs out.</summary>
    /// <remarks>
    /// Queued work uploaded after a character switch would be credited to a character that never
    /// earned it. The backoff survives, because it answers the token, not the character, so switching
    /// characters cannot shake off a rate limit. The cadence survives too: it came from <c>/config</c>.
    /// </remarks>
    public void Reset()
    {
        lock (gate)
        {
            pendingUnlockCategories.Clear();
            pendingFullSweep = null;

            // The next character's interval is not measured from the previous character's sweep.
            lastFullSweepAt = null;
        }
    }

    /// <summary>
    /// Records that an upload completed, restarting the interval clock if it was a full sweep.
    /// </summary>
    /// <remarks>
    /// An <c>unlock</c> upload is not a sweep and must not postpone the next one, or a player who keeps
    /// unlocking things would never get a full sweep.
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

            // A clock that jumped backwards past the last sweep would delay the next one by the jump,
            // so the earlier "now" becomes the reference point.
            if (lastFullSweepAt is { } lastSweep && now < lastSweep)
                lastFullSweepAt = now;

            // 1. A user-driven sweep wins. It covers whatever a pending unlock would have sent, so
            //    that unlock is dropped rather than uploaded twice.
            if (pendingFullSweep is { } requested)
            {
                pendingFullSweep = null;
                pendingUnlockCategories.Clear();

                // Anchored when issued, not on success: a login sweep that failed would leave this
                // null, and the periodic sweep below would never come due for the session.
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

            // 3. The periodic sweep, stamped here so a failed upload waits out the next interval instead
            //    of retrying every tick. It drops any unlock still debouncing: the sweep sends the same
            //    ids, and no later upload is promised to date the quest rows a snapshot leaves undated.
            if (lastFullSweepAt is { } sweptAt && now >= sweptAt + FullSyncInterval)
            {
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

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
