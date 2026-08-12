using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// The live occult tracker's orchestrator: watches for the character entering an Occult
/// Crescent instance, feeds the game's CE/FATE state through the tracker, and uploads
/// snapshots when the scheduler says to.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="SyncManager"/>, at a fraction of the size: every policy decision
/// lives in a pure, unit-tested class (<see cref="OccultEncounterTracker"/> interprets the
/// readings, <see cref="OccultUploadScheduler"/> times the uploads,
/// <see cref="OccultUploadBuilder"/> shapes the body), so this class only moves data between
/// the game, those classes, and the API client — and is verified by in-game QA.
/// </para>
/// <para>
/// <b>Threading.</b> Game state is read inside the per-frame <c>Update</c> handler (throttled
/// to one read per second); the resulting request — a plain object with no game handles — is
/// uploaded on a background task. The scheduler locks internally, so the background task may
/// report outcomes to it directly.
/// </para>
/// <para>
/// <b>Consent.</b> Every tick starts by asking <see cref="OccultGate.CanTrack"/> (which owns
/// the full gate ladder) and stands down while /sync is halted on something only the user can
/// fix, so revoking any switch stops the tracker within a second.
/// </para>
/// </remarks>
internal sealed class OccultManager : IDisposable
{
    /// <summary>One game-state read per second: state phases live for minutes, so nothing is
    /// missed, and the per-frame cost stays negligible.</summary>
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to stay quiet after a failure that would recur on every attempt: one only the
    /// user can fix (bad token, unclaimed character), or a terminal rejection of a payload
    /// shape this build will keep producing. The /sync path surfaces those to the user; this
    /// path just stops hammering.
    /// </summary>
    private static readonly TimeSpan UserActionBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest the enter upload waits for the snapshot to become fingerprintable. The
    /// first seconds after zoning are a sync race — the FATE table and CE deadlines stream in
    /// — and an enter sent before any server epoch lands carries a fingerprint that matches
    /// no active tracker, splitting one real instance across two tracker rows. A rejoin is
    /// the sharpest case: the instant enter races the whole re-sync. Past this deadline the
    /// enter goes out regardless; a genuinely quiet instance answers <c>unresolved</c> and
    /// resolves itself on the first timestamped change.
    /// </summary>
    private static readonly TimeSpan MaxEnterSettle = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IFateTable fateTable;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly ApiClient apiClient;
    private readonly PluginSettings settings;

    /// <summary>
    /// The /sync orchestrator, read for two things it owns: the character identity (captured
    /// once, hash-side, for both upload paths) and the latest <c>/config</c>.
    /// </summary>
    private readonly SyncManager syncManager;

    private readonly string pluginVersion;

    /// <summary>The source of "now". Injected so tests of the pure parts never meet a wall clock.</summary>
    private readonly TimeProvider timeProvider;

    private readonly OccultEncounterTracker tracker = new();
    private readonly OccultUploadScheduler scheduler = new();

    /// <summary>
    /// Cancelled on unload, so an upload in flight when the plugin is torn down stops rather
    /// than completing against disposed state.
    /// </summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>A copy of the token, taken before the source can ever be disposed (see
    /// <see cref="SyncManager"/>'s field of the same name for the full reasoning).</summary>
    private readonly CancellationToken lifetimeToken;

    // The fields below are framework-thread only (written and read inside the Update handler
    // and the logout event, which Dalamud raises on that thread) — the same single-writer
    // discipline SyncManager documents. `uploadInFlight` is the exception: cleared by the
    // background task, hence volatile.

    /// <summary>When the next game-state read is due.</summary>
    private DateTimeOffset nextReadAt = DateTimeOffset.MinValue;

    /// <summary>True from the first readable tick inside an occult territory until the
    /// character leaves that territory. A tick whose read fails mid-instance does not clear
    /// it — the director can be momentarily unreadable while state re-syncs.</summary>
    private bool inside;

    /// <summary>
    /// Set while the enter upload is being HELD for a fingerprintable snapshot: the deadline
    /// past which it goes out anyway. Null once the enter has been handed to the scheduler.
    /// While the hold is on, change notifications are suppressed too — a sparse change
    /// sneaking out ahead of the enter would create the very duplicate the hold prevents.
    /// </summary>
    private DateTimeOffset? enterHoldDeadline;

    /// <summary>The occult territory the character is (or was last) inside — the territory a
    /// leave upload must name, since by then the character is standing somewhere else.</summary>
    private uint activeTerritory;

    /// <summary>
    /// A leave upload built at the moment the exit was observed. The snapshot must be captured
    /// then — the tracker is reset right after, and the scheduler may not issue the leave until
    /// later (a backoff can defer it).
    /// </summary>
    private OccultInstanceStateRequest? pendingLeave;

    /// <summary>True while an upload is on the wire, so a second one never starts under it.</summary>
    private volatile bool uploadInFlight;

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing on its own.</summary>
    public OccultManager(
        IFramework framework,
        IClientState clientState,
        IFateTable fateTable,
        IPlayerState playerState,
        IPluginLog log,
        ApiClient apiClient,
        PluginSettings settings,
        SyncManager syncManager,
        string pluginVersion,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.fateTable = fateTable;
        this.playerState = playerState;
        this.log = log;
        this.apiClient = apiClient;
        this.settings = settings;
        this.syncManager = syncManager;
        this.pluginVersion = pluginVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        lifetimeToken = lifetime.Token;

        // Every `+=` here has a matching `-=` in Dispose.
        framework.Update += OnFrameworkUpdate;
        clientState.Logout += OnLogout;
    }

    /// <summary>Unsubscribes everything the constructor subscribed to, then cancels work in flight.</summary>
    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Logout -= OnLogout;

        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>The character logged out: whatever was tracked belongs to a session that ended.</summary>
    /// <remarks>The scheduler's backoff deliberately survives (it belongs to the token, not the
    /// character); everything else resets. No leave upload is sent — presence ages out
    /// server-side, and the logged-out client may have no working session to send from.</remarks>
    private void OnLogout(int type, int code)
    {
        inside = false;
        enterHoldDeadline = null;
        pendingLeave = null;
        tracker.Reset();
        scheduler.Reset();
    }

    /// <summary>Runs every frame on the framework thread. Must stay cheap: the real work is
    /// gated to one pass per second.</summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        var now = timeProvider.GetUtcNow();
        if (now < nextReadAt)
            return;
        nextReadAt = now + ReadInterval;

        try
        {
            Tick(now);
        }
        catch (Exception ex)
        {
            // Never let a tracker bug escape into the game's frame dispatch. Once per second at
            // worst, and the log line names the culprit.
            log.Error(ex, "Occult tracker tick failed.");
        }
    }

    /// <summary>One per-second pass: gates, enter/leave detection, state diff, upload dispatch.</summary>
    private void Tick(DateTimeOffset now)
    {
        var config = syncManager.RemoteConfig;

        // The tracker's consent gate (see OccultGate for the full ladder), plus /sync's
        // user-action halt: when syncing is stopped on something only the user can fix (bad
        // token, unclaimed character), this path goes quiet too rather than earning its own
        // copy of the same 401 every minute — and both resume together on "Sync now".
        var enabled = OccultGate.CanTrack(settings, config)
            && !syncManager.BlockedPendingUserAction;

        if (!enabled)
        {
            // Disabled mid-instance (toggle, kill switch, website log-out): go quiet
            // immediately. No leave upload — sending one would itself be an upload the gates
            // just refused — so the reporter simply ages out of presence server-side. The
            // condition covers a queued leave as well as an active visit: a leave captured
            // moments before the gate closed would otherwise survive the reset and ship,
            // once re-enabled, for an instance left arbitrarily long ago.
            if (inside || pendingLeave is not null)
            {
                inside = false;
                enterHoldDeadline = null;
                pendingLeave = null;
                tracker.Reset();
                scheduler.Reset();

                // This reset discards the remembered down ghosts: an encounter that ends
                // before tracking resumes never gets a down from this client — the server's
                // absence self-heal corrects it from the next full snapshot (see
                // docs/api-contract.md § occult/instance-state). The log line marks the
                // discontinuity for anyone tracing a tracker history that skipped an end.
                log.Debug("Occult tracking gate closed mid-instance; local state discarded.");
            }

            return;
        }

        // Non-null whenever the gate passes: CanTrack requires the occultTracker block.
        scheduler.ApplyHeartbeat(config!.OccultTracker!.HeartbeatSeconds);

        // Without an identity there is nothing to attribute an upload to. SyncManager captures
        // it a few seconds after login; until then (and after a capture that gave up) stay idle.
        var identity = syncManager.Identity;
        if (identity is null)
            return;

        var territory = (uint)clientState.TerritoryType;
        var inOccultZone = OccultZones.IsOccultTerritory(territory);

        // Declared ahead of the && so they are definitely assigned on every path; they hold
        // values only when `readable` lands true (the read short-circuits outside the zones).
        List<DynamicEventReading>? events = null;
        List<FateReading>? fates = null;
        var readable = inOccultZone && OccultInstanceReader.TryRead(fateTable, out events, out fates);

        // Leaving is judged by the TERRITORY, not by readability: the director can be
        // momentarily unreadable mid-instance (state re-syncing), and treating that as an
        // exit would drop every down stamp and re-enter with a fresh hold. A failed read
        // while still standing in an occult zone is just a skipped tick.
        if (inside && !inOccultZone)
        {
            QueueLeave(identity, now);
        }

        // The occult→occult hop: readable in a DIFFERENT occult territory while still marked
        // inside the old one (South Horn → North Horn without an intervening non-occult
        // tick). The old visit must close first — its leave, built from ITS territory and
        // snapshot, ships before the new instance's enter — or the new zone's encounters
        // would upload under the old territory id into a shared tracker.
        if (inside && readable && territory != activeTerritory)
        {
            QueueLeave(identity, now);
        }

        if (readable)
        {
            if (!inside)
            {
                inside = true;
                activeTerritory = territory;
                tracker.Reset();

                // The enter is HELD, not queued: the snapshot needs a server epoch before it
                // can identify this instance (see MaxEnterSettle). A pendingLeave from a visit
                // that ended moments ago deliberately survives this re-arm — the scheduler
                // ships it first (leave outranks enter), closing the old visit properly.
                enterHoldDeadline = now + MaxEnterSettle;
                log.Debug($"Entered occult territory {territory}; live tracker armed.");
            }

            var changed = tracker.Apply(events!, fates!, now);

            if (enterHoldDeadline is { } deadline)
            {
                // Release the hold the moment the snapshot can fingerprint — or at the
                // deadline, whichever comes first. The enter carries the full state at issue
                // time, so the changes accumulated during the hold ride along with it.
                if (OccultFingerprint.IsFingerprintable(tracker.Current) || now >= deadline)
                {
                    enterHoldDeadline = null;
                    scheduler.NotifyEntered(now);
                }
            }
            else if (changed)
            {
                scheduler.NotifyChanged(now);
            }
        }

        if (uploadInFlight)
            return;

        var due = scheduler.Poll(now);
        if (due is null)
            return;

        // The leave ships the snapshot captured at exit; everything else ships the live state.
        var request = due == OccultTrigger.Leave
            ? pendingLeave
            : OccultUploadBuilder.Build(
                identity.ContentIdHash, identity.Name, identity.HomeWorld, pluginVersion,
                activeTerritory, due.Value, tracker.Current, ReadCurrentWorldId());

        if (due == OccultTrigger.Leave)
            pendingLeave = null;

        // Defensive only: every path that lets the scheduler issue a Leave also captured a
        // payload first, and the disabled path resets both together.
        if (request is null)
            return;

        uploadInFlight = true;

        // Off the framework thread, including the JSON serialization inside the client. `_ =`
        // discards the task deliberately: nothing awaits it, and UploadAsync lets nothing escape.
        _ = Task.Run(() => UploadAsync(request, due.Value));
    }

    /// <summary>
    /// Closes the current visit: captures the leave payload from the instance being left,
    /// resets the tracker, and queues the leave with the scheduler.
    /// </summary>
    /// <remarks>The payload must be built HERE — the tracker resets immediately after, and
    /// the scheduler may defer the actual send (a backoff, an upload in flight) past that.</remarks>
    private void QueueLeave(CharacterIdentity identity, DateTimeOffset now)
    {
        inside = false;
        enterHoldDeadline = null;

        pendingLeave = OccultUploadBuilder.Build(
            identity.ContentIdHash, identity.Name, identity.HomeWorld, pluginVersion,
            activeTerritory, OccultTrigger.Leave, tracker.Current, ReadCurrentWorldId());
        tracker.Reset();
        scheduler.NotifyLeft(now);
        log.Debug($"Left occult territory {activeTerritory}; final upload queued.");
    }

    /// <summary>
    /// The reporter's CURRENT world (<c>World</c> sheet row id) — where the character is
    /// standing, not their home world, because a data-center traveler's instance belongs to
    /// the visited DC. Null when the game has not populated it (the server then leaves the
    /// tracker un-scoped rather than mis-scoped).
    /// </summary>
    /// <remarks>
    /// Framework thread only, like every game read in this class. Reads the raw row id
    /// rather than resolving the sheet row: an id the installed game data does not know is
    /// still worth sending (the server resolves it against its own world table — the
    /// catalog-trailing rule), and a plain <c>RowId</c> read cannot throw the way a name
    /// resolution can. Zero is the game's unset world — the one "not readable yet" value —
    /// so <c>&gt; 0</c> is the whole readability test.
    /// </remarks>
    private uint? ReadCurrentWorldId()
    {
        var worldId = playerState.CurrentWorld.RowId;
        return worldId > 0 ? worldId : null;
    }

    /// <summary>Uploads off the framework thread and reports the outcome to the scheduler.</summary>
    /// <remarks>
    /// No retry loop, deliberately: every upload is a full snapshot, so the next change or
    /// heartbeat re-sends everything a failed attempt would have. The scheduler's backoff covers
    /// the cases where the server asked for quiet.
    /// </remarks>
    private async Task UploadAsync(OccultInstanceStateRequest request, OccultTrigger trigger)
    {
        try
        {
            var response = await apiClient
                .PostOccultInstanceStateAsync(request, lifetimeToken)
                .ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();

            if (response.Status == ApiStatus.Ok)
            {
                scheduler.MarkUploaded(now);

                // The tracker id is the server's identity for the instance — logging it is what
                // lets a QA session (or a bug report) prove that a rejoin landed on the same
                // tracker. It is a server-generated UUID, not player data.
                log.Debug(
                    $"Occult {trigger} upload: {response.Value?.Outcome ?? "ok"}" +
                    $"{(response.Value?.TrackerId is { } id ? $" tracker={id}" : string.Empty)}" +
                    $"{(response.Value?.Created == true ? " (created)" : string.Empty)}.");
                return;
            }

            // The server told us to wait (429, or a 503 kill switch / tracker_unavailable).
            if (RetryPolicy.BackoffUntil(response.Status, response.RetryAfter, now) is { } until)
            {
                log.Debug($"Occult tracker backing off until {until:u} after {response.Status}.");
                scheduler.BackOffUntil(until);
                return;
            }

            // A client-side refusal (unusable token or backend mid-edit): no request was even
            // sent, so there is nothing to pace — the per-tick gate owns recovery, and a
            // backoff here would only delay it for a user who just fixed their settings.
            if (response.Status == ApiStatus.NotConfigured)
            {
                log.Debug($"Occult {trigger} upload skipped: not configured.");
                return;
            }

            // Something only the user can fix (bad token, unclaimed character), or a terminal
            // rejection (400/405/413 — plugin bugs where the identical shape would fail
            // again). Either way, retrying at heartbeat cadence would fail all session long,
            // so go quiet for a while. The /sync path owns telling the user what is wrong.
            if (RetryPolicy.RequiresUserAction(response.Status) || ApiStatusMap.IsTerminal(response.Status))
            {
                scheduler.BackOffUntil(now + UserActionBackoff);
                log.Debug($"Occult tracker paused after {response.Status}.");
                return;
            }

            // Transient (a network blip, a 5xx): the next change/heartbeat carries a fresh
            // full snapshot, so just note it.
            log.Debug($"Occult {trigger} upload failed: {response.Status}.");
        }
        catch (OperationCanceledException)
        {
            // The plugin unloaded mid-upload; the log service may itself be gone.
        }
        catch (Exception ex)
        {
            // Last-resort net, as in SyncManager: an exception in a discarded task would
            // otherwise surface as an unobserved-task exception with no context.
            log.Error(ex, "Unexpected failure during occult upload.");
        }
        finally
        {
            uploadInFlight = false;
        }
    }
}
