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
/// Runs the Crucible run sharing: follows the character through the boards, feeds the windows and the
/// character's and familiar's HP into the scheduler, and uploads what it hands out.
/// </summary>
/// <remarks>
/// The rules live in classes that touch no game state (<see cref="CrucibleGate"/>,
/// <see cref="CrucibleTerritories"/>, <see cref="CrucibleFeed"/>, <see cref="CrucibleFamiliarTracker"/>,
/// <see cref="CrucibleUploadScheduler"/>, <see cref="CrucibleOutcomePolicy"/>); this class moves data
/// between them, the game and the API client. The game is read on its main thread, and each upload,
/// a plain object with no game handles, is sent and settled on a background task. A frame that finds
/// the gate closed stands down and discards what was waiting, sending no leave, since a leave would be
/// an upload the gate refused.
/// </remarks>
internal sealed class CrucibleManager : IDisposable
{
    /// <summary>
    /// How often a board reads the familiar that is out, fights included, and the character's own HP out
    /// of combat, and how often a visit still waiting for its bag tries the run HUD again.
    /// </summary>
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IBuddyList buddyList;
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
    /// Which familiar each <c>Pet</c> row belongs to, or null when the sheet could not be read, in
    /// which case no familiar HP is sent.
    /// </summary>
    private readonly CrucibleFamiliarLinks? familiarLinks;

    /// <summary>Decides when a snapshot of the familiars' HP is handed out.</summary>
    private readonly CrucibleFamiliarTracker familiarTracker = new();

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
    /// The wait after a switched-off 503, which can end early once the website says sharing is back on (see
    /// <see cref="SyncResumeWait"/>). Armed by the upload task, asked by the frame handler.
    /// </summary>
    private readonly SyncResumeWait syncWait = new();

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

    /// <summary>
    /// When the familiar, the character's own HP and a pending bag are next due to be read.
    /// </summary>
    private DateTimeOffset nextReadAt;

    /// <summary>
    /// Whether a familiar the links could not name has been logged since the sharing last started over.
    /// Only the first is, since every reading of it would log it again.
    /// </summary>
    private bool unnamedFamiliarReported;

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing on its own.</summary>
    public CrucibleManager(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IDataManager dataManager,
        IBuddyList buddyList,
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
        this.buddyList = buddyList;
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

        // The familiar links are fixed game data too, so they also load once.
        familiarLinks = CrucibleFamiliarLinkSheet.Load(dataManager, log);

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
        // Read once: the config poll can replace it at any moment, and the gate and heartbeat must agree.
        // The poll number is read first (see SyncManager.ConfigAnsweredPoll).
        var answeredPoll = syncManager.ConfigAnsweredPoll;
        var config = syncManager.RemoteConfig;

        // Once the latest /config says the sharing is on again, a switched-off 503's wait ends (see
        // SyncResumeWait). Asked before the gate, so a wait set while the gate was closed still ends.
        var sharingOn = config is not null && !CrucibleGate.ServerHasSwitchedOff(config);
        if (syncWait.TryEnd(answeredPoll, sharingOn, now))
        {
            scheduler.SyncResumed();
            log.Debug("Crucible run sharing: syncing is back on; uploads resume.");
        }

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
        var combatChanged = inCombat != wasInCombat;

        // The loot window can open a moment before the in-combat flag clears and may not redraw after,
        // so it is read once when the flag clears.
        if (wasInCombat && !inCombat && feed.Board is not null)
            observer.ReadIfOpen(CrucibleWindows.Loot);

        wasInCombat = inCombat;

        // A clock that jumped backwards would otherwise hold the reads off until it caught up.
        if (nextReadAt > now + ReadInterval)
            nextReadAt = now;

        // Also read on the frame the flag changes, so a fight's familiars go up as it ends and the
        // familiar out as it begins is its first reading.
        if (feed.Board is not null && (now >= nextReadAt || combatChanged))
        {
            nextReadAt = now + ReadInterval;
            ReadFamiliar(inCombat, now);

            if (!inCombat)
            {
                ReadOwnHp(now);

                // The HUD may not exist yet on the frame the character zones in, so a visit still
                // waiting for its bag tries again (see CrucibleFeed.BagPending).
                if (feed.BagPending)
                    observer.ReadIfOpen(CrucibleWindows.Hud);
            }
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
        var change = feed.Follow(territory, now);

        // Each visit follows its familiars afresh: a fight cut short by leaving the board never ends
        // inside it, so what was kept for that fight is dropped rather than sent.
        if (change != CrucibleVisitChange.None)
            familiarTracker.Reset();

        switch (change)
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

    /// <summary>
    /// Reads the familiar that is out, in combat or not, and feeds in the snapshot the reading calls for
    /// (see <see cref="CrucibleFamiliarTracker"/>).
    /// </summary>
    /// <remarks>
    /// Read from the game's record of the player's own pet, which holds the local player's familiar
    /// alone. Only its data id and HP are read, never its <c>GameObject</c>, which would look the pet up
    /// in the object table.
    /// </remarks>
    private void ReadFamiliar(bool inCombat, DateTimeOffset now)
    {
        // The links could not be read, so no familiar can be named and none is followed.
        if (familiarLinks is null)
            return;

        if (familiarTracker.Observe(SummonedFamiliar(familiarLinks), inCombat, now) is { } observation)
            feed.Offer(observation, (uint)clientState.TerritoryType, now);
    }

    /// <summary>
    /// The familiar that is out, or null when none is, the record holds no reading of one, or it cannot
    /// be named (see <see cref="CrucibleFamiliarLinks.Summoned"/>).
    /// </summary>
    private CrucibleSummoned? SummonedFamiliar(CrucibleFamiliarLinks links)
    {
        if (buddyList.PetBuddy is not { } pet)
            return null;

        var summoned = links.Summoned(pet.DataID, pet.CurrentHP, pet.MaxHP);

        // A pet the bestiary does not link is reported once, so a moved sheet column shows in the log.
        if (summoned is null && pet.DataID != 0 && links.PetIdFor(pet.DataID) is null
            && !unnamedFamiliarReported)
        {
            unnamedFamiliarReported = true;
            log.Debug($"Crucible run sharing: pet {pet.DataID} is no familiar the bestiary links.");
        }

        return summoned;
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
        familiarTracker.Reset();
        wasInCombat = false;
        rejectionReported = false;
        unnamedFamiliarReported = false;
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
                    syncWait.UploadAccepted();
                    settled = true;
                    LogAccepted(response.Value, upload.Trigger);
                    break;

                // syncWait.Arm decides whether Tick may end the wait early. It runs before the
                // scheduler's wait is set (see SyncResumeWait).
                case CrucibleOutcomeKind.Retry:
                    var retryAt = outcome.Until ?? now;
                    var endsEarly = outcome.EndsWhenSyncResumes
                        && syncWait.Arm(syncManager.ConfigPollsStarted, now, retryAt);
                    scheduler.MarkRetry(upload, retryAt, now, endsEarly);
                    settled = true;
                    if (outcome.EndsWhenSyncResumes)
                        syncManager.BringConfigPollForward();

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
