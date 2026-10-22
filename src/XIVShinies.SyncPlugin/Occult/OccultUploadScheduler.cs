using System;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Decides when the occult uploader fires and with which trigger. The tracker owns what is sent, and
/// the snapshot is read fresh at send time.
/// </summary>
/// <remarks>
/// It reads nothing from the game, sends nothing, and keeps no clock: callers pass <c>now</c>.
/// <c>Notify*</c>, <c>Poll</c> and <c>SyncResumed</c> run on the framework thread, and
/// <c>MarkUploaded</c>, <c>BackOffUntil</c> and <c>BackOffUntilSyncResumes</c> on a background task
/// after the HTTP call, so a lock guards the state. When several are due, leave wins, then enter, then
/// a settled change, then the heartbeat; enter and leave carry the full snapshot, so either swallows a
/// pending change.
/// </remarks>
public sealed class OccultUploadScheduler
{
    /// <summary>The shortest heartbeat the plugin honors, whatever /config says.</summary>
    /// <remarks>
    /// The backend URL is user-overridable, so a zero or negative cadence from a bad server must not
    /// become a request flood. At the floor, heartbeats alone use half the server's default rate
    /// budget (240 an hour, one per 15 seconds), leaving headroom for change uploads.
    /// </remarks>
    public static readonly TimeSpan MinHeartbeat = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest heartbeat the plugin honors. The server ages a reporter out after about three
    /// missed heartbeats, so a huge value would silently break presence.
    /// </summary>
    public static readonly TimeSpan MaxHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a change burst settles before it uploads. A CE flip often lands with FATE flips within
    /// a second or two.
    /// </summary>
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);

    /// <summary>Guards every field below; the class remarks say which threads call.</summary>
    private readonly object gate = new();

    private bool pendingEnter;
    private bool pendingLeave;

    private bool pendingChange;
    private DateTimeOffset changeDueAt;

    /// <summary>True between an enter and its leave.</summary>
    private bool inside;

    /// <summary>
    /// The heartbeat clock's reference point: set by any trigger <see cref="Poll"/> issues and by a
    /// completed upload. Null until the first of those.
    /// </summary>
    private DateTimeOffset? heartbeatAnchor;

    private DateTimeOffset? backoffUntil;

    /// <summary>
    /// When the wait that syncing coming back on can end early runs out (see
    /// <see cref="BackOffUntilSyncResumes"/>). Null when not in that wait.
    /// </summary>
    private DateTimeOffset? syncResumesBy;

    /// <summary>How long between idle re-uploads.</summary>
    public TimeSpan Heartbeat { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Adopts the server's heartbeat cadence from <c>/config</c>, clamped between
    /// <see cref="MinHeartbeat"/> and <see cref="MaxHeartbeat"/>.
    /// </summary>
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

    /// <summary>The character entered an occult instance: the first snapshot is due at once.</summary>
    /// <remarks><paramref name="now"/> is unused; it keeps the Notify methods alike.</remarks>
    public void NotifyEntered(DateTimeOffset now)
    {
        lock (gate)
        {
            pendingEnter = true;
            inside = true;
            pendingChange = false;
        }
    }

    /// <summary>An encounter's status changed; restarts the debounce window.</summary>
    /// <remarks>
    /// Each change pushes the deadline out, so a burst produces one upload. The deadline is assigned
    /// outright, so a clock that jumps backward cannot leave a window that never closes.
    /// </remarks>
    public void NotifyChanged(DateTimeOffset now)
    {
        lock (gate)
        {
            pendingChange = true;
            changeDueAt = now + ChangeDebounce;
        }
    }

    /// <summary>The character left the instance: the final upload is due at once.</summary>
    /// <remarks>
    /// The uploader must build the leave payload from the territory and snapshot being left: the
    /// scheduler cannot tell a plain exit from a same-tick hop into another instance.
    /// </remarks>
    public void NotifyLeft(DateTimeOffset now)
    {
        lock (gate)
        {
            pendingLeave = true;
            inside = false;
            pendingChange = false;

            // An enter queued before this leave describes a finished visit. One notified after it
            // is a new visit and survives.
            pendingEnter = false;
        }
    }

    /// <summary>Records a successful upload, restarting the idle heartbeat clock.</summary>
    /// <remarks>
    /// <see cref="Poll"/> also anchors the clock when it issues work; this credits the completion time.
    /// </remarks>
    public void MarkUploaded(DateTimeOffset now)
    {
        lock (gate)
        {
            heartbeatAnchor = now;
        }
    }

    /// <summary>Suppresses all work until the given moment, as the server instructed.</summary>
    /// <remarks>
    /// Pending work is deferred, never dropped. The deadline never shortens: over-waiting is the safe
    /// direction with a server that asked us to stop.
    /// </remarks>
    public void BackOffUntil(DateTimeOffset until)
    {
        lock (gate)
        {
            if (backoffUntil is null || until > backoffUntil)
                backoffUntil = until;
        }
    }

    /// <summary>
    /// Suppresses all work until <paramref name="until"/> or <see cref="SyncResumed"/>, whichever comes
    /// first: the wait a 503 <c>sync_disabled</c> starts if <c>SyncResumeWait.Arm</c> lets it end early.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="BackOffUntil"/>'s deadline, so an early end leaves a rate limit in
    /// force; it never shortens either.
    /// </remarks>
    public void BackOffUntilSyncResumes(DateTimeOffset until)
    {
        lock (gate)
        {
            if (syncResumesBy is null || until > syncResumesBy)
                syncResumesBy = until;
        }
    }

    /// <summary>
    /// Ends the <see cref="BackOffUntilSyncResumes"/> wait, because the website says syncing is back
    /// on. A <see cref="BackOffUntil"/> wait stays in force.
    /// </summary>
    public void SyncResumed()
    {
        lock (gate)
            syncResumesBy = null;
    }

    /// <summary>Forgets all queued work and the heartbeat, as at logout.</summary>
    /// <remarks>
    /// Both waits survive: they are the server talking to the token, and relogging must not shake off
    /// a rate limit.
    /// </remarks>
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
    /// Returns the upload due now and removes it from the queue, or null. Called once per framework
    /// tick; each piece of work is handed out once.
    /// </summary>
    /// <remarks>
    /// Every issued trigger re-anchors the heartbeat at issue time, not on success. A failed upload
    /// waits a fresh interval instead of retrying every tick, and even a failed enter leads to a
    /// heartbeat one interval later, so one transient error cannot end presence for the whole visit.
    /// </remarks>
    public OccultTrigger? Poll(DateTimeOffset now)
    {
        lock (gate)
        {
            if (backoffUntil is { } until)
            {
                if (now < until)
                    return null;

                backoffUntil = null;
            }

            // Likewise a wait for syncing to resume.
            if (syncResumesBy is { } resumesBy)
            {
                if (now < resumesBy)
                    return null;

                syncResumesBy = null;
            }

            // A clock that jumped back past the anchor would stall the heartbeat by the size of the
            // jump, possibly long enough for the server to age the reporter out.
            if (heartbeatAnchor is { } a && now < a)
                heartbeatAnchor = now;

            // 1. Leave first: presence should clear promptly, and its snapshot is the final word on
            //    the visit. It covers a debouncing change too, since the snapshot is read at issue.
            if (pendingLeave)
            {
                pendingLeave = false;
                pendingChange = false;
                heartbeatAnchor = now;
                return OccultTrigger.Leave;
            }

            // 2. Enter. An instance's first Apply always registers as a change, since every id is
            //    new, so without this swallow every entry would send an identical second upload.
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

            // 4. The idle heartbeat.
            if (inside && heartbeatAnchor is { } anchor && now >= anchor + Heartbeat)
            {
                heartbeatAnchor = now;
                return OccultTrigger.Heartbeat;
            }

            return null;
        }
    }
}
