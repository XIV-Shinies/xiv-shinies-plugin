using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Diagnostics;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// The live occult tracker's orchestrator: notices the character entering an Occult Crescent
/// instance, feeds CE and FATE state through the tracker, and uploads when the scheduler says to.
/// </summary>
/// <remarks>
/// The rules live in <see cref="OccultEncounterTracker"/>, <see cref="OccultUploadScheduler"/>,
/// <see cref="OccultUploadBuilder"/> and <see cref="OccultOutcomePolicy"/>; this class only moves data
/// between the game, them, and the API client. Game state is read in the framework <c>Update</c>
/// handler once a second, and the upload runs on a background task, which reports to the scheduler
/// directly because the scheduler locks. Every tick asks <see cref="OccultGate.CanTrack"/> and stands
/// down while the user-action halt shared with /sync is set, so revoking any switch stops the tracker
/// within a second.
/// </remarks>
internal sealed class OccultManager : IDisposable
{
    /// <summary>
    /// One game-state read per second: phases last minutes, so nothing is missed, and the per-frame
    /// cost stays negligible.
    /// </summary>
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);

    /// <summary>The longest the enter upload waits for the snapshot to become fingerprintable.</summary>
    /// <remarks>
    /// The FATE table and CE deadlines stream in after zoning, most sharply on a rejoin, and an enter
    /// sent before any server epoch lands matches no tracker and splits one instance across two. Past
    /// this the enter goes out anyway; a genuinely quiet instance answers <c>unresolved</c> and
    /// resolves on its first timestamped change.
    /// </remarks>
    private static readonly TimeSpan MaxEnterSettle = TimeSpan.FromSeconds(10);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IFateTable fateTable;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly ApiClient apiClient;
    private readonly PluginSettings settings;

    /// <summary>
    /// The /sync orchestrator, which owns what every upload path shares: the hashed character
    /// identity, the latest <c>/config</c>, and the user-action halt with the session and press counts
    /// that scope it. The tracker raises the halt through <see cref="SyncManager.HaltFromLiveUpload"/>.
    /// </summary>
    private readonly SyncManager syncManager;

    private readonly string pluginVersion;

    private readonly TimeProvider timeProvider;

    private readonly OccultEncounterTracker tracker = new();
    private readonly OccultUploadScheduler scheduler = new();

    /// <summary>
    /// Tick failures already reported in full. The tick runs every second, so a deterministic bug would
    /// otherwise fill the log. Framework thread only.
    /// </summary>
    private readonly RepeatedFailures tickFailures = new();

    /// <summary>Upload failures already reported in full.</summary>
    /// <remarks>
    /// Separate from <see cref="tickFailures"/>, because uploads run on the thread pool. It needs no
    /// lock: <see cref="uploadInFlight"/> lets one upload run at a time and, being volatile, publishes
    /// what the previous one wrote.
    /// </remarks>
    private readonly RepeatedFailures uploadFailures = new();

    /// <summary>
    /// Canceled on unload, so an upload in flight stops instead of completing against disposed state.
    /// </summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>A copy of the token, taken before the source can be disposed.</summary>
    /// <remarks>
    /// Reading <c>lifetime.Token</c> after disposal throws, and an upload started before
    /// <see cref="Dispose"/> may still be running then. A token whose source was canceled and then
    /// disposed is still safe to observe.
    /// </remarks>
    private readonly CancellationToken lifetimeToken;

    // Except uploadInFlight, the fields below are framework-thread only and need no lock: the Update
    // handler and the logout event, raised from a hook on the game's logout callback, both run there.

    private DateTimeOffset nextReadAt = DateTimeOffset.MinValue;

    /// <summary>
    /// True from the first readable tick in an occult territory until the character leaves it.
    /// </summary>
    private bool inside;

    /// <summary>
    /// While the enter is held for a fingerprintable snapshot, the deadline past which it goes anyway;
    /// null once the scheduler has it.
    /// </summary>
    /// <remarks>
    /// Change notices wait too, since one sent ahead of the enter would create the duplicate the hold
    /// prevents.
    /// </remarks>
    private DateTimeOffset? enterHoldDeadline;

    /// <summary>
    /// The occult territory the character is, or was last, inside: the one a leave upload names, since
    /// by then the character stands elsewhere.
    /// </summary>
    private uint activeTerritory;

    /// <summary>The leave upload captured at exit (see <see cref="QueueLeave"/>).</summary>
    private OccultInstanceStateRequest? pendingLeave;

    /// <summary>
    /// True while an upload is on the wire, so a second never starts under it. Volatile because the
    /// background task clears it.
    /// </summary>
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

        // Every subscription here has a matching unsubscribe in Dispose.
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
    /// <remarks>
    /// The scheduler's backoff survives, since it belongs to the token. No leave upload is sent:
    /// presence ages out on the server, and the logged-out client may have no session to send from.
    /// </remarks>
    private void OnLogout(int type, int code)
    {
        inside = false;
        enterHoldDeadline = null;
        pendingLeave = null;
        tracker.Reset();
        scheduler.Reset();
    }

    /// <summary>
    /// Runs every frame on the framework thread, so it must stay cheap; the real work runs once a
    /// second.
    /// </summary>
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
            // A tracker bug must never escape into the game's frame dispatch. Only the first
            // sighting gets the full report (see tickFailures).
            if (tickFailures.IsFirstSighting(ex))
                log.Error(ex, "Occult tracker tick failed.");
            else
                log.Debug(ex, "Occult tracker tick failed again.");
        }
    }

    /// <summary>One per-second pass: gates, enter and leave detection, state diff, upload dispatch.</summary>
    private void Tick(DateTimeOffset now)
    {
        var config = syncManager.RemoteConfig;

        // The halt shared with /sync: once any path is refused for something only the user can fix,
        // this one goes quiet rather than earn its own refusal, and all resume on "Sync now".
        var enabled = OccultGate.CanTrack(settings, config)
            && !syncManager.BlockedPendingUserAction;

        if (!enabled)
        {
            // Disabled mid-visit: no leave upload, since the closed gate refuses it; presence ages out on the
            // server. A queued leave is dropped too, or it would ship on re-enable for a visit long over.
            if (inside || pendingLeave is not null)
            {
                inside = false;
                enterHoldDeadline = null;
                pendingLeave = null;
                tracker.Reset();
                scheduler.Reset();

                // The reset drops the down ghosts; the server's absence self-heal repairs a missed end
                // from the next full snapshot (see docs/api-contract.md, occult/instance-state).
                log.Debug("Occult tracking gate closed mid-instance; local state discarded.");
            }

            return;
        }

        // Non-null whenever the gate passes: CanTrack requires the occultTracker block.
        scheduler.ApplyHeartbeat(config!.OccultTracker!.HeartbeatSeconds);

        // SyncManager captures the identity a few seconds after login. Until then, or after a capture
        // that gave up, there is nothing to attribute an upload to.
        var identity = syncManager.Identity;
        if (identity is null)
            return;

        var territory = (uint)clientState.TerritoryType;
        var inOccultZone = OccultZones.IsOccultTerritory(territory);

        // Set only when readable is true; the read is skipped outside the zones.
        List<DynamicEventReading>? events = null;
        List<FateReading>? fates = null;
        var readable = inOccultZone && OccultInstanceReader.TryRead(fateTable, out events, out fates);

        // Leaving is judged by territory, not readability: the director can be briefly unreadable
        // while state re-syncs, and treating that as an exit would drop every down stamp.
        if (inside && !inOccultZone)
        {
            QueueLeave(identity, now);
        }

        // A hop straight from one occult zone to the other closes the old visit, whose leave ships before
        // the new zone's enter. Otherwise the new zone's encounters would upload into the old tracker.
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

                // Hold the enter until the snapshot can identify the instance (see MaxEnterSettle).
                // A pending leave from the last visit survives, and the scheduler ships it first.
                enterHoldDeadline = now + MaxEnterSettle;
                log.Debug($"Entered occult territory {territory}; live tracker armed.");
            }

            var changed = tracker.Apply(events!, fates!, now);

            if (enterHoldDeadline is { } deadline)
            {
                // The enter carries the full state at issue, so changes during the hold ride along.
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

        // Asked again: an upload that just finished may have raised the halt after the gate above. It
        // writes the halt before clearing uploadInFlight, so a tick that sees it finished sees its halt.
        if (syncManager.BlockedPendingUserAction)
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

        // Defensive: every path that queues a Leave captures its payload first, and the disabled
        // path resets both.
        if (request is null)
            return;

        uploadInFlight = true;

        // Captured on the framework thread, so a refusal landing after a logout or after the
        // player's fix is dropped (see SyncManager.HaltFromLiveUpload).
        var generation = syncManager.SessionGeneration;
        var haltEpoch = syncManager.HaltEpoch;

        // Off the framework thread, including the JSON serialization inside the client. Nothing
        // awaits the task, and UploadAsync lets nothing escape.
        _ = Task.Run(() => UploadAsync(request, due.Value, generation, haltEpoch));
    }

    /// <summary>
    /// Closes the current visit: captures the leave payload from the instance being left, resets the
    /// tracker, and queues the leave.
    /// </summary>
    /// <remarks>
    /// The payload must be built here: the tracker resets right after, and the scheduler may defer the
    /// send past that, for a backoff or an upload in flight.
    /// </remarks>
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
    /// The character's current <c>World</c> row id, or null while the game has not set it. Current, not
    /// home, because a data-center traveler's instance belongs to the visited data center.
    /// </summary>
    /// <remarks>
    /// Framework thread only. It sends the raw row id unresolved: the server resolves ids the installed
    /// game data lacks, and a <c>RowId</c> read cannot throw the way a name lookup can. Zero is the
    /// game's unset world, so <c>&gt; 0</c> is the whole readability test.
    /// </remarks>
    private uint? ReadCurrentWorldId()
    {
        var worldId = playerState.CurrentWorld.RowId;
        return worldId > 0 ? worldId : null;
    }

    /// <summary>
    /// Uploads off the framework thread and carries out what the answer calls for: the scheduler
    /// hears about successes and waits, and a refusal only the player can fix raises the halt.
    /// </summary>
    /// <remarks>
    /// No retry loop: every upload is a full snapshot, so the next change or heartbeat resends
    /// everything a failed one would have. The scheduler's backoff covers a server that asked for quiet.
    /// </remarks>
    /// <param name="request">The snapshot to send.</param>
    /// <param name="trigger">Why it is being sent.</param>
    /// <param name="startedFor">The login session the upload was sent for.</param>
    /// <param name="haltEpochAtSend">The "Sync now" press count when the upload was sent.</param>
    private async Task UploadAsync(
        OccultInstanceStateRequest request, OccultTrigger trigger, int startedFor, int haltEpochAtSend)
    {
        try
        {
            var response = await apiClient
                .PostOccultInstanceStateAsync(request, lifetimeToken)
                .ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();

            // OccultOutcomePolicy decides; this switch carries it out.
            var outcome = OccultOutcomePolicy.Classify(response.Status, response.RetryAfter, now);
            switch (outcome.Kind)
            {
                case OccultOutcomeKind.Applied:
                    scheduler.MarkUploaded(now);
                    LogApplied(response.Value, trigger);
                    break;

                // The server asked for quiet (429, or a 503 kill switch or tracker_unavailable), or
                // rejected the payload outright (400, 405, 413).
                case OccultOutcomeKind.BackOff or OccultOutcomeKind.Pause when outcome.Until is { } until:
                    scheduler.BackOffUntil(until);
                    log.Debug($"Occult tracker quiet until {until:u} after {response.Status}.");
                    break;

                // Nothing to pace (see OccultOutcomeKind.Skip).
                case OccultOutcomeKind.Skip:
                    log.Debug($"Occult {trigger} upload skipped: not configured.");
                    break;

                // The same halt a refused sync raises: it names the fix on the sync card and on every
                // sharing card it stops, and the gate in Tick stops this path until the player acts.
                case OccultOutcomeKind.Halt:
                    syncManager.HaltFromLiveUpload(
                        UploadLogSource.LiveTracker,
                        response.Status, response.HttpStatusCode, startedFor, haltEpochAtSend);
                    break;

                // Transient: the next change or heartbeat carries a fresh full snapshot.
                default:
                    log.Debug($"Occult {trigger} upload failed: {response.Status}.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin unloaded mid-upload; the log service may itself be gone.
        }
        catch (Exception ex)
        {
            // An exception in a discarded task would otherwise go unobserved, with no context. Only
            // the first gets the full report: the heartbeat would repeat it for the whole visit.
            if (uploadFailures.IsFirstSighting(ex))
                log.Error(ex, "Unexpected failure during occult upload.");
            else
                log.Debug(ex, "Occult upload failed again.");
        }
        finally
        {
            uploadInFlight = false;
        }
    }

    /// <summary>Logs an applied upload with the tracker the server matched it to.</summary>
    /// <param name="value">The 200 body, when one was parsed.</param>
    /// <param name="trigger">Why the upload was sent.</param>
    private void LogApplied(OccultInstanceStateResponse? value, OccultTrigger trigger)
    {
        // The tracker id, a server UUID rather than player data, shows whether a rejoin landed on the
        // same tracker. Server strings are clamped: the log is durable and the backend overridable.
        var outcome = ServerText.Clamp(value?.Outcome ?? "ok");
        var tracker = value?.TrackerId is { } id
            ? $" tracker={ServerText.Clamp(id)}"
            : string.Empty;

        log.Debug(
            $"Occult {trigger} upload: {outcome}{tracker}" +
            $"{(value?.Created == true ? " (created)" : string.Empty)}.");
    }
}
