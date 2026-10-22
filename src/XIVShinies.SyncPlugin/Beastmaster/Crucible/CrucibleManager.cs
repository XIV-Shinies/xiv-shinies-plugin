using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Diagnostics;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Runs the Crucible run sharing: follows the character in and out of the boards, feeds what the
/// windows and the character's own HP show into the scheduler, and uploads what the scheduler hands out.
/// </summary>
/// <remarks>
/// The rules live in classes that touch no game state (<see cref="CrucibleGate"/>,
/// <see cref="CrucibleTerritories"/>, <see cref="CrucibleFeed"/>, <see cref="CrucibleUploadScheduler"/>,
/// <see cref="CrucibleOutcomePolicy"/>); this class moves data between them, the game and the API
/// client. The game is read on its main thread, and each upload, a plain object with no game handles,
/// is sent and settled on a background task. A frame that finds the gate closed stands down and
/// discards what was waiting, sending no leave, since a leave would be an upload the gate refused.
/// </remarks>
internal sealed class CrucibleManager : IDisposable
{
    /// <summary>
    /// How often the character's own HP is read inside a board out of combat, and how often a visit
    /// still waiting for its bag tries the run HUD again.
    /// </summary>
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly ApiClient apiClient;
    private readonly PluginSettings settings;

    /// <summary>
    /// Owns what every upload path shares: the character identity, the latest <c>/config</c>, and the
    /// user-action halt with the counts that scope it.
    /// </summary>
    private readonly SyncManager syncManager;

    private readonly string pluginVersion;
    private readonly TimeProvider timeProvider;

    /// <summary>Fed on the main thread and settled from upload tasks; it locks internally.</summary>
    private readonly CrucibleUploadScheduler scheduler = new();

    private readonly CrucibleFeed feed;
    private readonly CrucibleObserver observer;

    /// <summary>
    /// Which frame failures have been logged in full, so a fault repeating every frame does not fill
    /// the log. Main thread only.
    /// </summary>
    private readonly RepeatedFailures tickFailures = new();

    /// <summary>Which upload failures have been logged in full.</summary>
    /// <remarks>
    /// Used from upload tasks under its own lock: a reset frees the scheduler's slot while an earlier
    /// request may still be on the wire, so two tasks can reach it at once.
    /// </remarks>
    private readonly RepeatedFailures uploadFailures = new();

    /// <summary>
    /// Canceled on unload, so an upload in flight stops rather than completing against disposed state.
    /// </summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>A copy of the token, taken before the source can ever be disposed.</summary>
    private readonly CancellationToken lifetimeToken;

    /// <summary>
    /// Whether a refused payload has been logged as a warning since the sharing last started over: a
    /// login, a logout or a stand-down.
    /// </summary>
    // Volatile: the upload task writes it from any thread.
    private volatile bool rejectionReported;

    // The fields below live on the game's main thread only.

    /// <summary>True from a frame the gate stood open until the frame that stands down.</summary>
    private bool sharing;

    private bool wasInCombat;

    /// <summary>When the character's own HP and a pending bag are next due to be read.</summary>
    private DateTimeOffset nextReadAt;

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing on its own.</summary>
    public CrucibleManager(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IDataManager dataManager,
        IPluginLog log,
        ApiClient apiClient,
        PluginSettings settings,
        SyncManager syncManager,
        string pluginVersion,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.log = log;
        this.apiClient = apiClient;
        this.settings = settings;
        this.syncManager = syncManager;
        this.pluginVersion = pluginVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        lifetimeToken = lifetime.Token;
        feed = new CrucibleFeed(scheduler);

        // The results screen's names are game data that cannot change while the game runs, so they
        // load once, here.
        observer = new CrucibleObserver(
            addonLifecycle, gameGui, condition, () => GateOpen(syncManager.RemoteConfig),
            OnWindowRead, OnWindowClosed, CrucibleNameSheets.Load(dataManager, log), log);

        // Every `+=` here has a matching `-=` in Dispose.
        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
    }

    /// <summary>Unsubscribes everything the constructor subscribed to, then cancels work in flight.</summary>
    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        observer.Dispose();

        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>
    /// A character logged in: whatever was waiting belongs to a session that ended, even one whose
    /// logout never arrived, as after a disconnect.
    /// </summary>
    private void OnLogin() => Discard();

    /// <summary>The character logged out: what was waiting belongs to a session that ended.</summary>
    /// <remarks>
    /// No leave is sent, since the logged-out client may have no session to send from; the run's
    /// liveness lapses on the server instead.
    /// </remarks>
    private void OnLogout(int type, int code) => Discard();

    /// <summary>
    /// True when the sharing may read and upload right now: an identity to attribute uploads to, the
    /// gate open, and no halt waiting for the player.
    /// </summary>
    /// <remarks>
    /// The /sync orchestrator captures the identity a few seconds after login. The observer asks this
    /// before every read, so a gate that closes between frames stops the reading at once.
    /// </remarks>
    /// <param name="config">The latest <c>/config</c>, read once by the caller.</param>
    private bool GateOpen(ConfigResponse? config) =>
        syncManager.Identity is not null
        && CrucibleGate.CanShare(settings, config)
        && !syncManager.BlockedPendingUserAction;

    /// <summary>Runs every frame on the game's main thread, and stays cheap when nothing is due.</summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Tick(timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            // A fault here must never escape into the game's frame dispatch.
            if (tickFailures.IsFirstSighting(ex))
                log.Error(ex, "Crucible run sharing frame failed.");
            else
                log.Debug(ex, "Crucible run sharing frame failed again.");
        }
    }

    /// <summary>One frame: the gate, the visit, the reads that are due, and the upload that is.</summary>
    private void Tick(DateTimeOffset now)
    {
        // Read once: the background config poll can replace it at any moment, and the gate and the
        // heartbeat must see the same one.
        var config = syncManager.RemoteConfig;

        if (!GateOpen(config))
        {
            StandDown();
            return;
        }

        sharing = true;

        // Non-null whenever the gate is open: CanShare requires the crucibleRuns block.
        scheduler.ApplyHeartbeat(config!.CrucibleRuns!.HeartbeatSeconds);

        Follow(now);

        var inCombat = condition[ConditionFlag.InCombat];

        // The loot window can open a moment before the in-combat flag clears and may not redraw after,
        // so it is read once when the flag clears.
        if (wasInCombat && !inCombat && feed.Board is not null)
            observer.ReadIfOpen(CrucibleWindows.Loot);

        wasInCombat = inCombat;

        // A clock that jumped backwards would otherwise hold the reads off until it caught up.
        if (nextReadAt > now + ReadInterval)
            nextReadAt = now;

        if (feed.Board is not null && !inCombat && now >= nextReadAt)
        {
            nextReadAt = now + ReadInterval;
            ReadOwnHp(now);

            // The HUD may not exist yet on the frame the character zones in, so a visit still waiting
            // for its bag tries again (see CrucibleFeed.BagPending).
            if (feed.BagPending)
                observer.ReadIfOpen(CrucibleWindows.Hud);
        }

        Send(now);
    }

    /// <summary>Hands the upload that is due, if any, to a background task.</summary>
    private void Send(DateTimeOffset now)
    {
        if (scheduler.Poll(now) is not { } upload)
            return;

        try
        {
            // Checked again: a refusal that landed since this frame's gate check may have raised the
            // halt. The upload is let go, and the next frame stands down and discards the rest.
            if (syncManager.BlockedPendingUserAction || syncManager.Identity is not { } identity)
            {
                scheduler.MarkDropped(upload, now);
                return;
            }

            var request = CrucibleUploadBuilder.Request(
                identity.ContentIdHash, identity.Name, identity.HomeWorld, pluginVersion,
                upload.Trigger, upload.TerritoryTypeId, upload.Observations);

            // Taken on the main thread for SyncManager.HaltFromLiveUpload, which drops a stale refusal.
            var generation = syncManager.SessionGeneration;
            var haltEpoch = syncManager.HaltEpoch;

            // Off the game's main thread, including the serialization inside the client. Nothing awaits
            // the task, and UploadAsync lets nothing escape.
            _ = Task.Run(() => UploadAsync(upload, request, generation, haltEpoch));
        }
        catch
        {
            // Every upload handed out must be settled (see CrucibleUploadScheduler.Poll); the frame
            // handler logs the exception.
            scheduler.MarkDropped(upload, now, now + CrucibleOutcomePolicy.RejectionHold);
            throw;
        }
    }

    /// <summary>
    /// Brings the visit up to date with the character's territory, and reads a new visit's baseline.
    /// </summary>
    /// <remarks>
    /// Called every frame and before each reading is fed in, so a window drawn on the frame the
    /// character zones in lands in the new visit.
    /// </remarks>
    private void Follow(DateTimeOffset now)
    {
        var territory = (uint)clientState.TerritoryType;

        switch (feed.Follow(territory, now))
        {
            case CrucibleVisitChange.Entered:
                log.Debug($"Entered Crucible board {territory}; run sharing armed.");
                ReadBaseline(now, everyOpenWindow: false);
                break;

            // Starting inside a board, windows may already be open, and each belongs in the baseline.
            case CrucibleVisitChange.AlreadyInside:
                log.Debug($"Already inside Crucible board {territory}; run sharing armed.");
                ReadBaseline(now, everyOpenWindow: true);
                break;

            case CrucibleVisitChange.Left:
                log.Debug("Left the Crucible board; leave queued.");
                break;
        }
    }

    /// <summary>
    /// Reads what a visit starts from: the bag, from the run HUD, the character's own HP, and, when
    /// asked, every other Crucible window already open.
    /// </summary>
    private void ReadBaseline(DateTimeOffset now, bool everyOpenWindow)
    {
        nextReadAt = now + ReadInterval;
        ReadOwnHp(now);

        if (!everyOpenWindow)
        {
            observer.ReadIfOpen(CrucibleWindows.Hud);
            return;
        }

        foreach (var windowName in CrucibleWindows.Redrawn)
            observer.ReadIfOpen(windowName);
    }

    /// <summary>Reads the character's own HP and feeds it in, out of combat only.</summary>
    /// <remarks>
    /// Read through the game's own pointer to the local character: a static call that takes no index,
    /// so it cannot reach any other character, and that touches no object table.
    /// </remarks>
    private unsafe void ReadOwnHp(DateTimeOffset now)
    {
        if (condition[ConditionFlag.InCombat])
            return;

        var character = Control.GetLocalPlayer();

        if (character == null || character->MaxHealth == 0)
            return;

        feed.Offer(
            CrucibleUploadBuilder.Self((int)character->Health, (int)character->MaxHealth, now),
            (uint)clientState.TerritoryType,
            now);
    }

    /// <summary>Feeds in a window reading, filed under the territory the character stands in.</summary>
    private void OnWindowRead(string windowName, CrucibleSnapshot snapshot)
    {
        var now = timeProvider.GetUtcNow();
        Follow(now);
        feed.Read(windowName, snapshot, (uint)clientState.TerritoryType, now);
    }

    /// <summary>
    /// Feeds in a window's close, with the window as read at the close, or null when it could not be
    /// read then.
    /// </summary>
    private void OnWindowClosed(string windowName, CrucibleSnapshot? atClose)
    {
        var now = timeProvider.GetUtcNow();
        Follow(now);
        var outcome = feed.Close(windowName, now, atClose);

        // A close with no remembered reading, as on a window's second close event, logs nothing.
        if (outcome != CrucibleCloseOutcome.NoOpening)
            log.Debug($"Crucible window {windowName} closed: {outcome}.");
    }

    /// <summary>Goes quiet when the gate closes, discarding whatever was waiting to go.</summary>
    private void StandDown()
    {
        if (!sharing)
            return;

        sharing = false;
        Discard();
        log.Debug("Crucible run sharing stood down; what was waiting is discarded.");
    }

    /// <summary>Forgets the visit, the queue and every remembered reading.</summary>
    private void Discard()
    {
        feed.Reset();
        wasInCombat = false;
        rejectionReported = false;
    }

    /// <summary>
    /// Uploads off the game's main thread and settles the upload with the scheduler as the answer
    /// calls for.
    /// </summary>
    /// <param name="startedFor">The login session the upload was sent for.</param>
    /// <param name="haltEpochAtSend">The "Sync now" press count when the upload was sent.</param>
    private async Task UploadAsync(
        CrucibleUpload upload, CrucibleObservationsRequest request, int startedFor, int haltEpochAtSend)
    {
        var settled = false;

        try
        {
            // Nothing below touches the game, so the method may resume on any thread.
            var response = await apiClient
                .PostCrucibleObservationsAsync(request, lifetimeToken)
                .ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();

            var errorCode = response.Error?.Error;
            var outcome = CrucibleOutcomePolicy.Classify(
                response.Status, response.RetryAfter, now, errorCode);

            switch (outcome.Kind)
            {
                case CrucibleOutcomeKind.Accepted:
                    scheduler.MarkAccepted(upload, now);
                    settled = true;
                    LogAccepted(response.Value, upload.Trigger);
                    break;

                case CrucibleOutcomeKind.Retry:
                    scheduler.MarkRetry(upload, outcome.Until ?? now, now);
                    settled = true;
                    // The note is the plugin's own text, chosen by matching the code, so no
                    // server-supplied text reaches the log.
                    var busyNote = CrucibleOutcomePolicy.IsBusy(errorCode) ? " (busy)" : string.Empty;
                    log.Debug(
                        $"Crucible {upload.Trigger} upload will be sent again: {response.Status}{busyNote}.");
                    break;

                case CrucibleOutcomeKind.Drop:
                    scheduler.MarkDropped(upload, now, outcome.Until);
                    settled = true;
                    LogRejected(upload.Trigger, response);
                    break;

                // The halt names the fix on the sync card and each sharing card it stops, and keeps the
                // gate shut until the player acts. Raised before the drop, so the next upload sees it.
                case CrucibleOutcomeKind.Halt:
                    syncManager.HaltFromLiveUpload(
                        UploadLogSource.CrucibleRuns,
                        response.Status, response.HttpStatusCode, startedFor, haltEpochAtSend);
                    scheduler.MarkDropped(upload, now);
                    settled = true;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin unloaded mid-upload; the log service may itself be gone.
        }
        catch (Exception ex)
        {
            // An exception in a discarded task would otherwise surface with no context.
            lock (uploadFailures)
            {
                if (uploadFailures.IsFirstSighting(ex))
                    log.Error(ex, "Unexpected failure during a Crucible upload.");
                else
                    log.Debug(ex, "A Crucible upload failed again.");
            }
        }
        finally
        {
            // Settled however this ends (see CrucibleUploadScheduler.Poll). One that failed in this
            // code would fail the same way again, so it is let go.
            if (!settled)
            {
                var now = timeProvider.GetUtcNow();
                scheduler.MarkDropped(upload, now, now + CrucibleOutcomePolicy.RejectionHold);
            }
        }
    }

    /// <summary>Logs an accepted upload with what the server made of it.</summary>
    /// <param name="value">The 200 body, when one was parsed.</param>
    private void LogAccepted(CrucibleObservationsResponse? value, CrucibleTrigger trigger)
    {
        // Server strings are clamped: the log is durable, and the backend is user-overridable.
        var outcome = ServerText.Clamp(value?.Outcome ?? "ok");
        var run = value?.RunId is { } id ? $" run={ServerText.Clamp(id)}" : string.Empty;
        var events = value?.Events is { } count ? $" events={count}" : string.Empty;

        log.Debug($"Crucible {trigger} upload: {outcome}{run}{events}.");
    }

    /// <summary>Logs an upload the server refused as a payload it will not take.</summary>
    /// <remarks>
    /// The first since the sharing last started over is a warning, since the plugin and the server
    /// disagree about the contract; the rest are debug lines, so a run of refusals does not fill the log.
    /// </remarks>
    private void LogRejected(CrucibleTrigger trigger, ApiResponse<CrucibleObservationsResponse> response)
    {
        var message = $"Crucible {trigger} upload refused: {response.Status} ({response.HttpStatusCode}).";

        if (rejectionReported)
        {
            log.Debug(message);
            return;
        }

        rejectionReported = true;
        log.Warning(message);
    }
}
