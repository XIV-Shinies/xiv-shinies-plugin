using System;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Decides <b>when</b> the occult uploader fires and with which trigger word. The tracker owns
/// <i>what</i> gets sent (the snapshot is always read fresh at send time).
/// </summary>
/// <remarks>
/// <para>
/// Pure bookkeeping in the <c>SyncScheduler</c> mold: no clock (every method takes <c>now</c>),
/// no game, no network. <c>Notify*</c> and <c>Poll</c> run on the framework thread, but
/// <c>MarkUploaded</c> and <c>BackOffUntil</c> run on a background task once the HTTP call
/// finishes, so a lock guards the state.
/// </para>
/// <para>
/// Priority when several things are due: leave, then enter, then a settled change, then the
/// heartbeat. Enter and leave both carry the same full snapshot a change would, so whichever
/// fires swallows a pending change rather than spending a second request on it.
/// </para>
/// </remarks>
public sealed class OccultUploadScheduler
{
    /// <summary>The shortest heartbeat the plugin will honor, whatever /config says.</summary>
    /// <remarks>
    /// The guard matters because the backend URL is user-overridable: a zero or negative
    /// cadence from a misconfigured or hostile server must not become a request flood. The
    /// floor sits at double the server's default rate budget (240/hour = one request per
    /// 15 s) so heartbeats alone can never consume the whole budget — change uploads need
    /// headroom inside it too.
    /// </remarks>
    public static readonly TimeSpan MinHeartbeat = TimeSpan.FromSeconds(30);

    /// <summary>The longest heartbeat the plugin will honor. Past ~3 missed heartbeats the
    /// server ages the reporter out, so an enormous value would silently break presence.</summary>
    public static readonly TimeSpan MaxHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>How long a change burst gets to settle before it uploads. A CE flip often
    /// lands alongside FATE flips within a second or two; the slide collapses them.</summary>
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);

    /// <summary>Guards every field below (see the class remarks for who calls from where).</summary>
    private readonly object gate = new();

    private bool pendingEnter;
    private bool pendingLeave;

    /// <summary>A change waiting out its debounce window, and when that window closes.</summary>
    private bool pendingChange;
    private DateTimeOffset changeDueAt;

    /// <summary>True between an observed enter and the matching leave — the heartbeat only
    /// runs while the character is actually inside.</summary>
    private bool inside;

    /// <summary>The heartbeat clock's reference point: set by any trigger <see cref="Poll"/>
    /// issues, and refreshed by a completed upload. Null until the first of those.</summary>
    private DateTimeOffset? heartbeatAnchor;

    /// <summary>The moment a server-instructed backoff expires. Null when not backing off.</summary>
    private DateTimeOffset? backoffUntil;

    /// <summary>How long between idle re-uploads.</summary>
    public TimeSpan Heartbeat { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>Adopts the server's heartbeat cadence from <c>/config</c>, clamped sane.</summary>
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

    /// <summary>The character entered an occult instance: first snapshot due immediately.</summary>
    /// <remarks><paramref name="now"/> is unused here (the work is due immediately, so there
    /// is no deadline to compute); the parameter exists for call-site symmetry with the other
    /// Notify methods, matching how <c>SyncScheduler.Request</c> is shaped.</remarks>
    public void NotifyEntered(DateTimeOffset now)
    {
        lock (gate)
        {
            pendingEnter = true;
            inside = true;

            // The enter carries the full snapshot; a change that raced it is redundant.
            pendingChange = false;
        }
    }

    /// <summary>Some encounter's status changed; (re)opens the debounce window.</summary>
    /// <remarks>The window slides — each new change pushes the deadline out, so a burst of
    /// simultaneous flips produces one upload. Assigning the deadline outright also means a
    /// clock that jumps backwards cannot leave a window that never closes.</remarks>
    public void NotifyChanged(DateTimeOffset now)
    {
        lock (gate)
        {
            pendingChange = true;
            changeDueAt = now + ChangeDebounce;
        }
    }

    /// <summary>The character left the instance: final upload due immediately.</summary>
    /// <remarks>
    /// The uploader must build the leave payload from the territory and snapshot being LEFT —
    /// the scheduler only orders the triggers; it cannot tell a plain exit from a same-tick
    /// hop into another instance.
    /// </remarks>
    public void NotifyLeft(DateTimeOffset now)
    {
        lock (gate)
        {
            pendingLeave = true;
            inside = false;

            // The leave upload carries the final snapshot; the debouncing change is redundant.
            pendingChange = false;

            // An enter queued before this leave is stale by construction — it describes a
            // visit that is already over. (An enter notified AFTER the leave is a new visit
            // and survives.)
            pendingEnter = false;
        }
    }

    /// <summary>Records a successful upload, restarting the idle-heartbeat clock.</summary>
    /// <remarks>
    /// <see cref="Poll"/> already anchors the clock when it ISSUES work, so the heartbeat
    /// survives an upload that fails; this refresh just credits the actual completion time.
    /// </remarks>
    public void MarkUploaded(DateTimeOffset now)
    {
        lock (gate)
        {
            heartbeatAnchor = now;
        }
    }

    /// <summary>Suppresses all work until the given moment, as instructed by the server.</summary>
    /// <remarks>Pending work is deferred, never dropped. The deadline is absolute and never
    /// shortens — over-waiting is the safe direction against a server that asked us to stop.</remarks>
    public void BackOffUntil(DateTimeOffset until)
    {
        lock (gate)
        {
            if (backoffUntil is null || until > backoffUntil)
                backoffUntil = until;
        }
    }

    /// <summary>Forgets all queued work and the heartbeat, as when the player logs out.</summary>
    /// <remarks>The backoff deadline deliberately survives, as in <c>SyncScheduler</c>: it is
    /// the server talking to the token, and relogging must not shake off a rate limit.</remarks>
    public void Reset()
    {
        lock (gate)
        {
            pendingEnter = false;
            pendingLeave = false;
            pendingChange = false;
            inside = false;
            heartbeatAnchor = null;
        }
    }

    /// <summary>
    /// Returns the upload that is due right now and removes it from the queue, or null.
    /// Called once per framework tick; each piece of work is handed out exactly once.
    /// </summary>
    /// <remarks>
    /// Every issued trigger re-anchors the heartbeat clock at issue time, not on success —
    /// the SyncScheduler pattern. A failed upload therefore waits out a fresh interval
    /// instead of retrying every tick, and even a failed ENTER still leads to a heartbeat
    /// one interval later, so a single transient error cannot silently end presence
    /// reporting for the whole visit.
    /// </remarks>
    public OccultTrigger? Poll(DateTimeOffset now)
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

            // A clock that jumped backwards past the anchor would otherwise stall the
            // heartbeat by however far it jumped — long enough, and the server ages the
            // reporter out. Treat the earlier "now" as the new reference point.
            if (heartbeatAnchor is { } a && now < a)
                heartbeatAnchor = now;

            // 1. Leave first: presence should clear promptly, and it supersedes everything
            //    else queued (its snapshot is the final word on this visit). A change still
            //    debouncing is covered by the leave's snapshot, so it is dropped here too —
            //    the snapshot is read at issue time, not when the change was noticed.
            if (pendingLeave)
            {
                pendingLeave = false;
                pendingChange = false;
                heartbeatAnchor = now;
                return OccultTrigger.Leave;
            }

            // 2. Enter: the first snapshot, due the moment it is queued. The very first
            //    Apply of an instance always registers as a change (every id is new), so
            //    without the swallow here every entry would spend a second, byte-identical
            //    upload two seconds after the enter.
            if (pendingEnter)
            {
                pendingEnter = false;
                pendingChange = false;
                heartbeatAnchor = now;
                return OccultTrigger.Enter;
            }

            // 3. A settled change burst.
            if (pendingChange && now >= changeDueAt)
            {
                pendingChange = false;
                heartbeatAnchor = now;
                return OccultTrigger.Change;
            }

            // 4. The idle heartbeat — only while inside, and only once an issued upload has
            //    anchored the clock.
            if (inside && heartbeatAnchor is { } anchor && now >= anchor + Heartbeat)
            {
                heartbeatAnchor = now;
                return OccultTrigger.Heartbeat;
            }

            return null;
        }
    }
}
