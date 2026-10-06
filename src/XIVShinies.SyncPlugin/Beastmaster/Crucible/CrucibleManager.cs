using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
// Control, which holds the game's pointer to the local character alone.
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Diagnostics;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// The Crucible run sharing's orchestrator: follows the character in and out of the boards, feeds
/// what the windows and the character's own HP show into the scheduler, and uploads what the scheduler
/// hands out.
/// </summary>
/// <remarks>
/// <para>
/// The rules live in classes that touch no game state: whether the sharing may run in
/// <see cref="CrucibleGate"/>, what may go up from where in <see cref="CrucibleTerritories"/>, how
/// visits, readings and closes reach the queue in <see cref="CrucibleFeed"/>, what goes up when in
/// <see cref="CrucibleUploadScheduler"/>, and what each answer means in
/// <see cref="CrucibleOutcomePolicy"/>. This class moves data between the game, those classes and the
/// API client.
/// </para>
/// <para>
/// <b>Threading.</b> The game is read on its main thread: the per-frame <c>Update</c> handler, and the
/// window events the observer receives there. Each upload is a plain object with no game handles, sent
/// on a background task that settles it with the scheduler, which locks internally.
/// </para>
/// <para>
/// <b>Consent.</b> The observer asks the gate itself before every read, and every frame stands down
/// when the gate has closed, discarding what was waiting to go. No leave is sent then: it would itself
/// be an upload the gate just refused.
/// </para>
/// </remarks>
// `internal` makes it visible only inside this plugin, and `sealed` means nothing can subclass it.
// `IDisposable` is the interface for an object with a `Dispose` method that releases what it holds,
// like the cleanup function a React effect returns.
internal sealed class CrucibleManager : IDisposable
{
    /// <summary>
    /// How often the character's own HP is read inside a board out of combat, and how often a visit
    /// still waiting for its bag tries the run HUD again.
    /// </summary>
    // `static readonly` rather than `const`: a `TimeSpan` (a length of time) is built when the program
    // runs, and `const` only takes values fixed when the code compiles.
    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly ApiClient apiClient;
    private readonly PluginSettings settings;

    /// <summary>
    /// The /sync orchestrator, which owns what every upload path shares: the character identity, the
    /// latest <c>/config</c>, and the user-action halt with the session and press counts that scope it.
    /// </summary>
    private readonly SyncManager syncManager;

    private readonly string pluginVersion;

    /// <summary>The source of "now".</summary>
    private readonly TimeProvider timeProvider;

    /// <summary>What goes up when; settled from the upload task as well as fed on the main thread.</summary>
    private readonly CrucibleUploadScheduler scheduler = new();

    /// <summary>The visits, readings and closes, on their way to the scheduler.</summary>
    private readonly CrucibleFeed feed;

    /// <summary>The window listener.</summary>
    private readonly CrucibleObserver observer;

    /// <summary>Which frame failures have already been reported in full.</summary>
    /// <remarks>
    /// The handler runs every frame, so a fault that repeats would otherwise fill the log. Game main
    /// thread only, like the handler it guards.
    /// </remarks>
    private readonly RepeatedFailures tickFailures = new();

    /// <summary>Which upload failures have already been reported in full.</summary>
    /// <remarks>
    /// Touched from upload tasks, never the game's main thread. A reset frees the scheduler's slot
    /// while an earlier request may still be on the wire, so two tasks can reach it at once, and every
    /// use takes its lock.
    /// </remarks>
    private readonly RepeatedFailures uploadFailures = new();

    /// <summary>
    /// Canceled on unload, so an upload in flight when the plugin is torn down stops rather than
    /// completing against disposed state.
    /// </summary>
    // A `CancellationTokenSource` is what cancels; the `CancellationToken` it hands out is what work
    // checks, like an `AbortController` and its `signal`.
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>A copy of the token, taken before the source can ever be disposed.</summary>
    private readonly CancellationToken lifetimeToken;

    /// <summary>
    /// Whether a refused payload has been reported at warning level since the sharing last started
    /// over (a login, a logout, or a stand-down); later ones are logged at debug level.
    /// </summary>
    // `volatile` because the upload task writes it, and that may be any thread. A field with no
    // `= ...` starts at its type's default: false, or the earliest moment.
    private volatile bool rejectionReported;

    // The fields below live on the game's main thread only.

    /// <summary>True from a frame the gate stood open until the frame that stands down.</summary>
    private bool sharing;

    /// <summary>Whether the character was in combat on the last frame.</summary>
    private bool wasInCombat;

    /// <summary>When the character's own HP and a pending bag are next due to be read.</summary>
    private DateTimeOffset nextReadAt;

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing on its own.</summary>
    // The parameters are the Dalamud services this reads through, the API client and settings it
    // shares with /sync, the /sync orchestrator, the plugin's version, and the source of "now".
    // `TimeProvider? timeProvider = null` is an optional parameter, like `timeProvider?: TimeProvider`.
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

        // `??` uses the right-hand value when the left is null, as in TypeScript.
        this.timeProvider = timeProvider ?? TimeProvider.System;

        lifetimeToken = lifetime.Token;
        feed = new CrucibleFeed(scheduler);

        // The gate, `OnWindowRead` and `OnWindowClosed` are handed over as function values, like
        // passing callbacks in TypeScript: the observer asks the first before each read and reports
        // to the other two. `() => ...` is a lambda, like an arrow function. The names the results
        // screen's text is resolved against are game data that does not change while the game runs,
        // so they are read once, here.
        observer = new CrucibleObserver(
            addonLifecycle, gameGui, condition, () => GateOpen(syncManager.RemoteConfig),
            OnWindowRead, OnWindowClosed, CrucibleNameSheets.Load(dataManager, log), log);

        // `+=` subscribes a handler to a C# event, like `addEventListener`. Every `+=` here has a
        // matching `-=` in Dispose.
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
    /// liveness lapses on the server instead. The scheduler keeps its wait and budget, which belong to
    /// the token rather than the character.
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
        // heartbeat must agree about the same one.
        var config = syncManager.RemoteConfig;

        if (!GateOpen(config))
        {
            StandDown();
            return;
        }

        sharing = true;

        // Non-null whenever the gate is open: CanShare requires the crucibleRuns block. `!` tells the
        // compiler so.
        scheduler.ApplyHeartbeat(config!.CrucibleRuns!.HeartbeatSeconds);

        Follow(now);

        var inCombat = condition[ConditionFlag.InCombat];

        // The loot window can open a moment before the in-combat flag clears, and is not certain to be
        // redrawn after it does, so it is read once when the flag clears.
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
        // `is not { } upload` is true when nothing is due; otherwise it names the upload.
        if (scheduler.Poll(now) is not { } upload)
            return;

        try
        {
            // Asked after the upload is handed out: a refusal that just landed may have raised the halt
            // since the gate at the top of this frame read it. The upload is let go, and the next frame
            // stands down and discards the rest.
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

            // Off the game's main thread, including the serialization inside the client. `_ =` discards
            // the task on purpose: nothing awaits it, and UploadAsync lets nothing escape.
            _ = Task.Run(() => UploadAsync(upload, request, generation, haltEpoch));
        }
        catch
        {
            // Settled however this ends (see CrucibleUploadScheduler.Poll). A bare `throw;` passes the
            // same exception on, with its original stack trace, to the frame handler that logs it.
            scheduler.MarkDropped(upload, now, now + CrucibleOutcomePolicy.RejectionHold);
            throw;
        }
    }

    /// <summary>
    /// Brings the visit up to date with the territory the character stands in, and reads a new
    /// visit's baseline.
    /// </summary>
    /// <remarks>
    /// Called before each reading is fed in as well as every frame, so a window drawn on the frame the
    /// character zones in lands inside the new visit.
    /// </remarks>
    private void Follow(DateTimeOffset now)
    {
        var territory = (uint)clientState.TerritoryType;

        // A `switch` statement runs the `case` whose value matches, like TypeScript's.
        switch (feed.Follow(territory, now))
        {
            // `$"..."` is an interpolated string, like a template literal: `{territory}` is filled in.
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
    // `unsafe` because the HP is read through a raw pointer into the game's memory, where C#'s
    // references and bounds checks do not apply; the null check below guards it.
    private unsafe void ReadOwnHp(DateTimeOffset now)
    {
        if (condition[ConditionFlag.InCombat])
            return;

        var character = Control.GetLocalPlayer();

        // `->` reaches a member through a pointer, as `.` does through a reference.
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

    /// <summary>Feeds in a window's close.</summary>
    private void OnWindowClosed(string windowName)
    {
        var now = timeProvider.GetUtcNow();
        Follow(now);
        feed.Close(windowName, now);
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
    /// <param name="upload">What the scheduler handed out.</param>
    /// <param name="request">The request built from it.</param>
    /// <param name="startedFor">The login session the upload was sent for.</param>
    /// <param name="haltEpochAtSend">The "Sync now" press count when the upload was sent.</param>
    // `async Task` is an asynchronous method, like an `async` function returning a `Promise<void>`.
    private async Task UploadAsync(
        CrucibleUpload upload, CrucibleObservationsRequest request, int startedFor, int haltEpochAtSend)
    {
        var settled = false;

        try
        {
            // `ConfigureAwait(false)` lets the rest of this method carry on on any thread rather than
            // returning to the one that started it; nothing below touches the game.
            var response = await apiClient
                .PostCrucibleObservationsAsync(request, lifetimeToken)
                .ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            var outcome = CrucibleOutcomePolicy.Classify(response.Status, response.RetryAfter, now);

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
                    log.Debug($"Crucible {upload.Trigger} upload will be sent again: {response.Status}.");
                    break;

                case CrucibleOutcomeKind.Drop:
                    scheduler.MarkDropped(upload, now, outcome.Until);
                    settled = true;
                    LogRejected(upload.Trigger, response);
                    break;

                // The same halt a refused sync raises: it names the fix on the sync card and on every
                // sharing card it stops, and the gate stops this path until the player acts. Raised
                // before the upload is let go, so the next upload the scheduler hands out already
                // sees it.
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
            // An exception in a discarded task would otherwise surface with no context. `lock` lets one
            // thread at a time run the code inside.
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
    /// <param name="trigger">Why the upload was sent.</param>
    private void LogAccepted(CrucibleObservationsResponse? value, CrucibleTrigger trigger)
    {
        // The run id is the server's identity for the run, so the log shows which run the uploads
        // attached to. Both server strings are clamped: the log is durable, and the backend is
        // user-overridable.
        var outcome = ServerText.Clamp(value?.Outcome ?? "ok");
        var run = value?.RunId is { } id ? $" run={ServerText.Clamp(id)}" : string.Empty;
        var events = value?.Events is { } count ? $" events={count}" : string.Empty;

        log.Debug($"Crucible {trigger} upload: {outcome}{run}{events}.");
    }

    /// <summary>Logs an upload the server refused as a payload it will not take.</summary>
    /// <remarks>
    /// The first since the sharing last started over is a warning, since it means the plugin and the
    /// server disagree about the contract; the rest are debug lines, so a run whose every upload is
    /// refused does not fill the log.
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
