using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Serilog.Events;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// The orchestrator: listens to the game and does the work, but decides nothing itself.
/// </summary>
/// <remarks>
/// Each policy lives in a class with no game or network in it: <see cref="UploadGate"/> (whether),
/// <see cref="SyncScheduler"/> (when and what), <see cref="RetryPolicy"/> (failures),
/// <see cref="CollectorSelection"/> (which collectors) and <see cref="SyncPayloadBuilder"/> (the body).
/// Every path out of the game events runs through <see cref="UploadGate"/> first, so nothing here
/// contacts the server before the user opts in except <see cref="RequestOnboardingConfigPoll"/>, on
/// the user's Verify press. The threading rules sit with the fields below.
/// </remarks>
internal sealed class SyncManager : IDisposable
{
    /// <summary>
    /// How often to re-fetch <c>/config</c>, per the contract's "roughly every 30 minutes".
    /// </summary>
    private static readonly TimeSpan ConfigPollInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long to wait before retrying a <c>/config</c> poll that did not answer, and before asking
    /// again after a 503 <c>sync_disabled</c> (see <see cref="BringConfigPollForward"/>).
    /// </summary>
    /// <remarks>
    /// Without a config the plugin does not know which items to read, so a failed first poll must not
    /// leave the item collection unread for a full interval. Two minutes spares a struggling server and
    /// is short enough that the user is unlikely to notice.
    /// </remarks>
    private static readonly TimeSpan ConfigPollRetryDelay = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait after the login event before reading the character.</summary>
    /// <remarks>
    /// The <c>Login</c> event fires before unlock bitmaps, the achievement list and inventory have
    /// loaded. Most sheet-backed collections declare their read complete
    /// (<see cref="Collectors.CategoryInfo.EnumeratesCompleteDomain"/>), so an early read would tell the
    /// server the missing ids are unowned, and the site would question the user's own manual marks.
    /// Shortening this wait needs a readiness gate in its place, like the achievements collector's
    /// <c>precondition</c>.
    /// </remarks>
    private static readonly TimeSpan LoginSettleDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before re-reading a character whose details were not usable yet.</summary>
    private static readonly TimeSpan IdentityRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many times to re-read an unusable character before giving up until a "Sync now" press or
    /// the next login.
    /// </summary>
    private const int MaxIdentityAttempts = 12;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IUnlockState unlockState;
    private readonly IPluginLog log;
    private readonly ApiClient apiClient;
    private readonly PluginSettings settings;

    /// <summary>Saves the settings. A callback, so this class needs no Dalamud config reference.</summary>
    private readonly Action saveSettings;

    private readonly IReadOnlyList<ICollector> collectors;
    private readonly string pluginVersion;

    private readonly TimeProvider timeProvider;

    private readonly SyncScheduler scheduler = new();

    // The settings window's upload log. In memory only; see UploadLog for why.
    private readonly UploadLog uploadLog = new();

    // True once this load's first /config poll has answered, successfully or not. Sweeps wait for
    // it; see OnFrameworkUpdate.
    private volatile bool firstConfigAnswerSeen;

    /// <summary>Canceled on unload, so work in flight stops instead of using disposed state.</summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>A copy of <see cref="lifetime"/>'s token, taken before it can be disposed.</summary>
    /// <remarks>
    /// Reading <c>lifetime.Token</c> after Dispose throws, and background work may still be running
    /// then. A token whose source was canceled and disposed is still safe to observe.
    /// </remarks>
    private readonly CancellationToken lifetimeToken;

    // Threading rules, referred to throughout this class. Game state is read only on the framework
    // thread and HTTP never runs there: a collection pass runs in the Update handler, and a background
    // task uploads what it built.
    // - A framework-thread field is written only on that thread: by the Update handler, the Login,
    //   Logout and Unlock events, and delegates queued with RunOnFrameworkThread. One writer needs no
    //   lock. The window's draw call may run on another thread, so a call from it marshals.
    // - A volatile field crosses threads. The window reads these, and the plain identity reference,
    //   from its draw call, at worst one frame stale. An int stands in for a nullable enum and a box
    //   for a struct, because neither can be volatile.
    // - The status dictionaries are replaced whole and never mutated once handed out, so a reader sees
    //   the old map or the new one.
    // - A queued delegate still runs after Dispose has begun, so each checks lifetimeToken first.
    //   Nothing awaits the queued and background tasks, and none lets an exception escape.

    /// <summary>The most recent <c>/config</c>, or null until the first poll succeeds.</summary>
    private volatile ConfigResponse? remoteConfig;

    /// <summary>True while an upload is in flight, so the framework thread never starts a second.</summary>
    private volatile bool uploadInFlight;

    /// <summary>True while a <c>/config</c> poll is in flight.</summary>
    private volatile bool configPollInFlight;

    /// <summary>
    /// How many <c>/config</c> polls have started, so each poll knows its number and a refusal or a
    /// press can note how many had started before it (see <see cref="ConfigPollsStarted"/>).
    /// </summary>
    /// <remarks>
    /// Written only on the framework thread, where every poll starts, and read from the upload tasks.
    /// </remarks>
    private volatile int configPollsStarted;

    /// <summary>
    /// Backs <see cref="ConfigAnsweredPoll"/>, which gives the read order. The poll task writes it
    /// right after <see cref="remoteConfig"/>.
    /// </summary>
    private volatile int configAnsweredPoll;

    /// <summary>
    /// True while the wizard is owed a config answer: set when it asks for one, cleared when any poll
    /// answers.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="firstConfigAnswerSeen"/>, which latches for the whole load, so a second
    /// Verify press after pasting a different token still waits.
    /// </remarks>
    private volatile bool onboardingConfigPending;

    /// <summary>
    /// The collections upload's wait after a 503 <c>sync_disabled</c>, which can end early once the
    /// website says syncing is back on (see <see cref="SyncResumeWait"/>). It takes its own lock.
    /// </summary>
    private readonly SyncResumeWait syncWait = new();

    /// <summary>
    /// A "Sync now" press made during a pause, waiting for its <c>/config</c> answer. It takes its own
    /// lock.
    /// </summary>
    private readonly PausedPress pausedPress = new();

    /// <summary>True once a collection pass has run for the character currently logged in.</summary>
    /// <remarks>
    /// The read-status panel reads "no skip reason" as "read", so before any pass every enabled
    /// collection would claim to be read. Source notes cannot stand in: a user with items switched off
    /// has none, yet their other collections were read. Cleared on logout.
    /// </remarks>
    private volatile bool hasCollected;

    // Which warnings and failures have already been logged. The first four are framework-thread
    // fields; the last three belong to the upload and poll tasks, which run one at a time behind
    // uploadInFlight and configPollInFlight.

    /// <summary>Decides which manifest truncation warnings this pass should log.</summary>
    private readonly ManifestTruncationWarnings truncationWarnings = new();

    /// <summary>Decides which payload-cap warnings this pass should log.</summary>
    private readonly PayloadCapWarnings capWarnings = new();

    /// <summary>Decides which frame-budget overruns this pass should warn about.</summary>
    private readonly FrameBudgetWarnings budgetWarnings = new();

    /// <summary>Which collection-pass failures have already been reported in full.</summary>
    private readonly RepeatedFailures assembleFailures = new();

    /// <summary>Which failed upload outcomes have already been reported.</summary>
    private readonly SyncOutcomeWarnings outcomeWarnings = new();

    /// <summary>Which upload failures have already been reported in full.</summary>
    private readonly RepeatedFailures uploadFailures = new();

    /// <summary>Which config-poll failures have already been reported in full.</summary>
    /// <remarks>
    /// Separate from <see cref="uploadFailures"/>, because a poll and an upload can be in flight at
    /// once and neither holds the other's flag.
    /// </remarks>
    private readonly RepeatedFailures configPollFailures = new();

    /// <summary>
    /// True once a pass in which at least one collector ran has completed since the plugin loaded.
    /// </summary>
    /// <remarks>
    /// Lets the cost report exempt the session's first pass (see <see cref="CollectionCost.IsAlarming"/>).
    /// Scoped to the plugin, not the character, because the warm-up survives a logout. A
    /// framework-thread field the window never reads, so it is not volatile.
    /// </remarks>
    private bool anyPassHasRun;

    /// <summary>
    /// Set when the server reported a failure only the user can fix. Halts collecting and uploading
    /// until the user acts, rather than looping against a server that will keep refusing.
    /// </summary>
    /// <remarks>
    /// The config poll still runs through the halts it can see past; see
    /// <see cref="SyncTickPlan.PollSurvivesHalt"/>.
    /// </remarks>
    private volatile bool blockedPendingUserAction;

    /// <summary>
    /// The status that raised the current halt, cast to int, or -1 before any halt. Meaningful only
    /// while <see cref="blockedPendingUserAction"/> is set.
    /// </summary>
    /// <remarks>
    /// Every place that raises a halt writes this before the flag, so a reader that sees the flag also
    /// sees its status. The window reads it through <see cref="HaltStatus"/> to name the fix, and the
    /// frame tick to ask <see cref="SyncTickPlan.PollSurvivesHalt"/>. <c>LastStatus</c> cannot stand
    /// in, because only the /sync upload writes it.
    /// </remarks>
    private volatile int haltStatusCode = -1;

    /// <summary>The "Sync now" press count; see <see cref="HaltEpoch"/>.</summary>
    private volatile int haltEpoch;

    /// <summary>
    /// The last outcome as an <see cref="ApiStatus"/> cast to int, or -1 when nothing has completed.
    /// </summary>
    private volatile int lastStatusCode = -1;

    /// <summary>
    /// When the last successful upload completed, boxed, or null if none has this session.
    /// </summary>
    private volatile object? lastSyncedAtBox;

    /// <summary>
    /// Bumped on logout, so a response for a character who already left is recognized and discarded.
    /// </summary>
    /// <remarks>
    /// Logout does not cancel a request in flight (only unload does), so its answer could otherwise put
    /// the previous character's status, or a halt, onto the next one's session. Work captures the
    /// generation it started for and drops an answer to a superseded one. Its one writer is the
    /// framework thread, so the plain increment is safe.
    /// </remarks>
    private volatile int sessionGeneration;

    // Framework-thread fields, from here through nextConfigPollAt (see the threading rules above).
    // The constructor's OnLogin call also writes them, before any handler is subscribed.

    /// <summary>The character to attribute uploads to. Null whenever nobody is logged in.</summary>
    private CharacterIdentity? identity;

    /// <summary>When the login settle delay expires, or null when not waiting for one.</summary>
    private DateTimeOffset? loginSettledAt;

    /// <summary>How many times the character has been read but found unusable since the last login.</summary>
    private int identityAttempts;

    /// <summary>
    /// True once identity capture has given up for this login, until a login, a logout or a "Sync
    /// now" press clears it. See <see cref="IdentityCaptureGaveUp"/>.
    /// </summary>
    private volatile bool identityCaptureGaveUp;

    /// <summary>True from a login to the next logout. See <see cref="IsLoggedIn"/>.</summary>
    /// <remarks>
    /// The window reads this rather than the game, because it draws outside the framework thread's
    /// guarantee.
    /// </remarks>
    private volatile bool loggedIn;

    /// <summary>When the next <c>/config</c> poll is due; null means at the first opportunity.</summary>
    private DateTimeOffset? nextConfigPollAt;

    /// <summary>
    /// Why each category was omitted the last time a pass ran it, keyed by category. Merged across
    /// passes because an unlock pass runs only some collectors (see <see cref="CollectionStatusMemory"/>).
    /// </summary>
    /// <remarks>
    /// The window renders whatever reason it is handed, such as the achievements list needing to be
    /// opened once, so the hint reaches the user without a branch on a category name.
    /// </remarks>
    private volatile IReadOnlyDictionary<string, string> lastSkipped = new Dictionary<string, string>();

    /// <summary>
    /// Each category's latest partial-read phrase (see <see cref="Collectors.CollectResult.PartialNote"/>),
    /// for the settings window. Merged by <see cref="CollectionStatusMemory.MergePartialNotes"/>.
    /// </summary>
    private volatile IReadOnlyDictionary<string, string> lastPartialNotes =
        new Dictionary<string, string>();

    /// <summary>
    /// Each category's latest healthy-chip hover copy (see
    /// <see cref="Collectors.CollectResult.CollectedDetail"/>), remembered like
    /// <see cref="lastPartialNotes"/>. For the settings window.
    /// </summary>
    private volatile IReadOnlyDictionary<string, string> lastCollectedDetails =
        new Dictionary<string, string>();

    /// <summary>
    /// Per-source scan status from the latest pass that reported any, keyed by source (see
    /// <see cref="SourceKeys"/>), such as "the saddlebag was read from a cache". For the settings window.
    /// </summary>
    /// <remarks>
    /// Replaced, not merged, and only by a pass that reported notes. A note describes storage as one
    /// pass saw it, and an unlock pass that skipped the item collectors reports none, so merging or
    /// clearing would erase a true "the armoire is unscanned" hint.
    /// </remarks>
    private volatile IReadOnlyDictionary<string, ItemSourceStatus> lastSourceNotes =
        new Dictionary<string, ItemSourceStatus>();

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing.</summary>
    public SyncManager(
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IUnlockState unlockState,
        IPluginLog log,
        ApiClient apiClient,
        PluginSettings settings,
        Action saveSettings,
        IReadOnlyList<ICollector> collectors,
        string pluginVersion,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.unlockState = unlockState;
        this.log = log;
        this.apiClient = apiClient;
        this.settings = settings;
        this.saveSettings = saveSettings;
        this.collectors = collectors;
        this.pluginVersion = pluginVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        lifetimeToken = lifetime.Token;

        // A plugin enabled while logged in gets no Login event, so treat this as one, before subscribing:
        // the constructor is off the framework thread, and a frame could read these fields mid-write.
        if (clientState.IsLoggedIn)
            OnLogin();

        // Each has a matching `-=` in Dispose; a handler left attached would run against a torn-down
        // plugin.
        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        unlockState.Unlock += OnUnlock;
    }

    /// <summary>The last upload's outcome, or null if none has completed. For the settings window.</summary>
    public ApiStatus? LastStatus => lastStatusCode < 0 ? null : (ApiStatus)lastStatusCode;

    /// <summary>When the last successful upload completed, or null if none has this session.</summary>
    public DateTimeOffset? LastSyncedAt => lastSyncedAtBox as DateTimeOffset?;

    /// <summary>
    /// True when syncing is halted until the user fixes something (a bad token, or a character the
    /// server would not match).
    /// </summary>
    public bool BlockedPendingUserAction => blockedPendingUserAction;

    /// <summary>
    /// The status that raised the current halt, or null when syncing is not halted. See
    /// <see cref="haltStatusCode"/> for its two readers.
    /// </summary>
    /// <remarks>
    /// Two volatile reads, so a halt raised or cleared between them leaves it one frame stale.
    /// </remarks>
    public ApiStatus? HaltStatus =>
        blockedPendingUserAction && haltStatusCode >= 0 ? (ApiStatus)haltStatusCode : null;

    /// <summary>The latest server config, which the settings window draws its switches from.</summary>
    public ConfigResponse? RemoteConfig => remoteConfig;

    /// <summary>
    /// How many <c>/config</c> polls have started. An upload path arms its
    /// <see cref="SyncResumeWait"/> with this when a 503 <c>sync_disabled</c> arrives.
    /// </summary>
    public int ConfigPollsStarted => configPollsStarted;

    /// <summary>
    /// The number of the poll <see cref="RemoteConfig"/> came from, or 0 until a poll has brought a
    /// config back.
    /// </summary>
    /// <remarks>
    /// Read this before <see cref="RemoteConfig"/>: the poll writes the config first and this second,
    /// so the config read afterwards is from this poll or a later one, never an earlier one.
    /// </remarks>
    public int ConfigAnsweredPoll => configAnsweredPoll;

    /// <summary>
    /// Brings the next <c>/config</c> poll forward to a few minutes away, after a 503
    /// <c>sync_disabled</c> on any upload path.
    /// </summary>
    /// <remarks>
    /// The sync card then names the switch within minutes, not at the next scheduled poll, and the
    /// plugin learns sooner when it is back on. Never pushes back a poll due sooner (see
    /// <see cref="SyncTickPlan.BringPollForward"/>). Safe from any thread: the upload tasks call it as
    /// their answers arrive.
    /// </remarks>
    public void BringConfigPollForward()
    {
        log.Information("Syncing is switched off; checking /config again in a few minutes.");
        RescheduleConfigPollSoon();
    }

    /// <summary>
    /// True while the wizard is still owed the config answer it asked for when the token verified.
    /// </summary>
    /// <remarks>
    /// The wizard holds its consent step shut while this is true, so the step opens with every checkbox
    /// it will show. Any answer lowers it, a failure included, because a wait only success could end
    /// would trap the user in the wizard while the server is unreachable. A failed poll just means no
    /// group checkboxes, which the one-time consent migration makes good later.
    /// </remarks>
    public bool OnboardingConfigPending => onboardingConfigPending;

    /// <summary>
    /// The current automatic full-sweep cadence after the server's tuning and the plugin's clamps, so
    /// the settings window can show the real number.
    /// </summary>
    public TimeSpan FullSyncInterval => scheduler.FullSyncInterval;

    /// <summary>True while an upload is on the wire, so the Sync now button can show it.</summary>
    public bool UploadInFlight => uploadInFlight;

    /// <summary>
    /// True while the server has asked the plugin to wait before its next upload, so a "Sync now"
    /// press would upload nothing until the wait ends.
    /// </summary>
    /// <remarks>
    /// A wait a 503 <c>sync_disabled</c> started can also end once the website says syncing is back
    /// on (see <see cref="SyncResumeWait"/>). The scheduler takes its lock to answer, so this is safe
    /// from the draw call.
    /// </remarks>
    public bool IsBackingOff => scheduler.IsBackingOff(timeProvider.GetUtcNow());

    /// <summary>The recent uploads, newest first, for the settings window's upload log.</summary>
    public IReadOnlyList<UploadLogEntry> UploadHistory => uploadLog.Entries;

    /// <summary>Empties the upload log, for the settings window's clear button.</summary>
    public void ClearUploadHistory() => uploadLog.Clear();

#if DEBUG
    /// <summary>Replaces the upload log with fabricated entries for capturing screenshots of it.</summary>
    /// <remarks>
    /// DEBUG-only, like <see cref="UploadLogSeed"/>: a released plugin must never show invented sync
    /// history. It clears first, so repeated runs give the same picture. The rest of the window moves to
    /// match, since a screenshot shows more than the log.
    /// </remarks>
    public void SeedUploadHistoryForScreenshots(
        IReadOnlyList<ICollector> collectors, DateTimeOffset now)
    {
        uploadLog.Clear();

        // Per-collection statuses last until logout and would say "not read yet" beside seeded rows
        // that say otherwise. Per-container notes are real readings the screenshot shows, so they stay.
        lastSkipped = new Dictionary<string, string>();
        lastPartialNotes = new Dictionary<string, string>();
        lastCollectedDetails = new Dictionary<string, string>();

        // Only collections a real pass would collect: one switched off draws no chip in the panel
        // above, so a row claiming it synced would contradict the window.
        var config = remoteConfig;
        var collectable = new List<ICollector>(collectors.Count);
        foreach (var collector in collectors)
        {
            if (CollectorGate.IsEnabled(collector.CategoryKey, settings, config))
                collectable.Add(collector);
        }

        var entries = UploadLogSeed.Build(collectable, now);
        foreach (var entry in entries)
            uploadLog.Record(entry);

        // The "Reading from:" panel draws only once a pass has run. With the maps cleared above, it
        // reports every switched-on collection as read, matching the seeded rows.
        if (entries.Count > 0)
            hasCollected = true;

        // From the newest seeded entry, so the card and the log's top row agree.
        if (entries.Count > 0)
        {
            var newest = entries[^1];
            lastStatusCode = (int)newest.Status;
            lastSyncedAtBox = newest.At;
        }
    }

    /// <summary>
    /// Puts identity capture in the state it reaches when it gives up, so the way out a "Sync now"
    /// press offers can be checked in game.
    /// </summary>
    /// <remarks>
    /// DEBUG-only. A real give-up needs a character that cannot be read, or whose name or home world
    /// never becomes readable, which an ordinary session does not produce. A "Sync now" press, a login
    /// or a logout leaves this state as they leave a real give-up.
    /// </remarks>
    public void ForgetCharacterForTesting()
    {
        // Marshaled: these are framework-thread fields, and a command can arrive from the console's
        // draw call.
        _ = framework.RunOnFrameworkThread(() =>
        {
            // A give-up happens only while logged in.
            if (lifetimeToken.IsCancellationRequested || !clientState.IsLoggedIn)
                return;

            identity = null;
            loginSettledAt = null;
            identityAttempts = 0;
            identityCaptureGaveUp = true;

            // A real give-up precedes the login's first upload, so these take the values a logout leaves.
            lastStatusCode = -1;
            lastSyncedAtBox = null;
            hasCollected = false;

            log.Information("Character forgotten as if reading it gave up. Press Sync now to read it again.");
        });
    }
#endif

    /// <summary>
    /// Why each category was skipped the last time a pass ran it. Empty before the first pass.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastSkipped => lastSkipped;

    /// <summary>
    /// Each category's latest partial-read phrase, keyed by category. Empty before the first pass
    /// and for every category read in full.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastPartialNotes => lastPartialNotes;

    /// <summary>
    /// Each category's latest healthy-chip hover copy, keyed by category. Empty before the first
    /// pass and for every category whose chip needs none.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastCollectedDetails => lastCollectedDetails;

    /// <summary>
    /// Per-source scan status from the most recent collection pass that reported any (see
    /// <see cref="lastSourceNotes"/>). Empty before the first such pass.
    /// </summary>
    public IReadOnlyDictionary<string, ItemSourceStatus> LastSourceNotes => lastSourceNotes;

    /// <summary>
    /// True once a collection pass has run for the current character. See <see cref="hasCollected"/>.
    /// </summary>
    public bool HasCollected => hasCollected;

    /// <summary>True when a character is loaded and identified, so an upload could actually happen.</summary>
    public bool HasCharacter => identity is not null;

    /// <summary>
    /// True when the plugin has stopped trying to read the logged-in character, so nothing uploads
    /// until a "Sync now" press or the next login starts it again.
    /// </summary>
    /// <remarks>Lets the window tell this apart from the ordinary wait after login.</remarks>
    public bool IdentityCaptureGaveUp => identityCaptureGaveUp && identity is null;

    /// <summary>True while a character is logged in, whether or not it has been read yet.</summary>
    /// <remarks>
    /// Lets the window tell nobody being logged in apart from the ordinary wait after login.
    /// </remarks>
    public bool IsLoggedIn => loggedIn;

    /// <summary>
    /// The identified character, or null when nobody usable is loaded. Every upload path reads this,
    /// so all attribute work to one identity; the window reads it to name the character.
    /// </summary>
    internal CharacterIdentity? Identity => identity;

    /// <summary>
    /// Which login session is current. The live tracker and the Crucible sharing capture it when they
    /// send an upload and hand it back with a refusal, so a refusal that lands after a logout is
    /// recognized as the previous character's (see <see cref="sessionGeneration"/>).
    /// </summary>
    internal int SessionGeneration => sessionGeneration;

    /// <summary>
    /// How many times "Sync now" has been pressed this plugin session, halt or not. The live tracker
    /// and the Crucible sharing capture it with each upload and hand it back with a refusal, so a
    /// refusal sent before the player's fix and answered after it does not halt them again.
    /// </summary>
    internal int HaltEpoch => haltEpoch;

    /// <summary>
    /// Raises the same halt a refused sync raises, for a live tracker or Crucible sharing upload the
    /// server refused in a way only the player can fix, and records it in the upload log.
    /// </summary>
    /// <remarks>
    /// One halt serves every path: their refusals name the same character or token, and "Sync now" or
    /// a relog lifts them all. Without it, a player sharing only live data never uploads to
    /// <c>/sync</c> and is never told why sharing stopped. A refusal from an ended session or sent
    /// before the last "Sync now" is dropped, though a logout or press landing just after that check
    /// can still let one through until the next press or relog.
    /// </remarks>
    /// <param name="source">The upload path the server refused.</param>
    /// <param name="status">The refusal's status; anything that is not a user-action halt is ignored.</param>
    /// <param name="httpStatusCode">The literal HTTP status, for the upload log's diagnostic.</param>
    /// <param name="startedFor">The <see cref="SessionGeneration"/> the upload was sent for.</param>
    /// <param name="haltEpochAtSend">The <see cref="HaltEpoch"/> when the upload was sent.</param>
    internal void HaltFromLiveUpload(
        UploadLogSource source, ApiStatus status, int? httpStatusCode, int startedFor, int haltEpochAtSend)
    {
        if (!RetryPolicy.RequiresUserAction(status)
            || startedFor != sessionGeneration
            || haltEpochAtSend != haltEpoch)
        {
            return;
        }

        // The status first, then the flag: see haltStatusCode.
        haltStatusCode = (int)status;
        blockedPendingUserAction = true;
        log.Warning($"{source} upload halted: {status}. The user must resolve this.");

        // Rechecked, so a logout since the first check leaves no row in the next character's log.
        if (startedFor == sessionGeneration)
        {
            uploadLog.Record(
                UploadLogEntry.LiveHalt(source, timeProvider.GetUtcNow(), status, httpStatusCode));
        }
    }

    /// <summary>Queues an immediate full sweep, as when the user presses "Sync now".</summary>
    /// <remarks>
    /// Clears the user-action halt, since a press says the user fixed it. Any backoff still holds: a
    /// button must not become a way to hammer a server that said stop. While the website has syncing
    /// switched off, the press asks <c>/config</c> once instead, and sweeps only if the answer says
    /// syncing is back on (see <see cref="PausedPress"/>).
    /// </remarks>
    public void RequestManualSync()
    {
        // Count the press first, so a live refusal sent before the fix is stale when it lands.
        // Interlocked, because the press may arrive on any thread.
        Interlocked.Increment(ref haltEpoch);

        // Cleared on the press itself, not behind the marshal below.
        blockedPendingUserAction = false;

        // Marshaled, because the rearm writes framework-thread fields. The scheduler request rides
        // inside, so the rearm lands before the sweep it unblocks.
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (lifetimeToken.IsCancellationRequested)
                return;

            var now = timeProvider.GetUtcNow();

            // Only a login or this press re-arms a capture that gave up; otherwise the sweep meets a
            // null identity and the button seems to do nothing. A pending capture is left alone, since
            // re-arming it would skip the login settle wait (see LoginSettleDelay).
            if (identityCaptureGaveUp && identity is null)
            {
                loginSettledAt = now;
                identityAttempts = 0;
                identityCaptureGaveUp = false;
            }

            // During a pause the press waits for its own poll (see PausedPress).
            if (remoteConfig is { Enabled: false })
            {
                if (UploadGate.CanContactServer(settings))
                    pausedPress.Press(configPollsStarted);

                return;
            }

            scheduler.Request(SyncTrigger.Manual, now);
        });
    }

    /// <summary>
    /// Fetches <c>/config</c> once when the user verifies their token: the one request that runs
    /// before onboarding is complete.
    /// </summary>
    /// <remarks>
    /// It skips <see cref="UploadGate.CanContactServer"/>, which keeps a fresh install silent. Dalamud's
    /// rules allow this: it runs only on the user's Verify press, which already sends <c>GET /me</c>,
    /// and it sends nothing about the player, reading only the server's switches, cadence and item
    /// manifest. It lets the wizard ask consent for the server's item groups instead of the user
    /// meeting them later as "New".
    /// </remarks>
    public void RequestOnboardingConfigPoll()
    {
        // The bypass is justified only inside the wizard. Past onboarding the ordinary gate governs
        // every poll, and a request from here would skip the master switch.
        if (settings.OnboardingComplete)
            return;

        // The one part of CanContactServer that survives the bypass: without a usable token the answer
        // could only be a 401.
        if (!settings.HasUsableToken())
            return;

        // Raised on the press, not when the poll starts, so the wizard never thinks it has nothing to
        // wait for. Every path lowers it: a poll answers (the HTTP timeout makes even an unreachable
        // server answer), a press during a poll shares its answer, and a post-teardown marshal lowers it.
        onboardingConfigPending = true;

        // Marshaled, because StartConfigPoll writes framework-thread fields.
        _ = framework.RunOnFrameworkThread(() =>
        {
            // Lowered here, since the poll it waits on will now never run.
            if (lifetimeToken.IsCancellationRequested)
            {
                onboardingConfigPending = false;
                return;
            }

            StartConfigPoll(timeProvider.GetUtcNow());
        });
    }

    /// <summary>Unsubscribes everything the constructor subscribed to, then cancels work in flight.</summary>
    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        unlockState.Unlock -= OnUnlock;

        // Unsubscribe first, then cancel: no new work can start once the handlers are detached.
        lifetime.Cancel();
        lifetime.Dispose();
    }

    // --- Game events -----------------------------------------------------------------------

    /// <summary>
    /// The character began loading. The identity is read later, once the game has populated it.
    /// </summary>
    private void OnLogin()
    {
        loginSettledAt = timeProvider.GetUtcNow() + LoginSettleDelay;
        identityAttempts = 0;
        identityCaptureGaveUp = false;
        loggedIn = true;
    }

    /// <summary>The character logged out. Everything character-specific is dropped.</summary>
    private void OnLogout(int type, int code)
    {
        identity = null;
        loginSettledAt = null;
        identityAttempts = 0;
        identityCaptureGaveUp = false;
        loggedIn = false;

        // The halt's most common cause, a character the server would not match, leaves with the
        // character; kept, it would silently block the next character's login sync. A revoked token
        // halts again on the next login sync: a request or two per relog, never a loop.
        blockedPendingUserAction = false;

        // Responses still in flight discard themselves when they land; see sessionGeneration.
        sessionGeneration++;

        // The status line describes the departing character's last upload.
        lastStatusCode = -1;
        lastSyncedAtBox = null;

        // The read-status snapshots describe the ended session and the departing character's storage.
        // The achievements list, for one, unloads on logout.
        lastSkipped = new Dictionary<string, string>();

        lastPartialNotes = new Dictionary<string, string>();
        lastCollectedDetails = new Dictionary<string, string>();

        lastSourceNotes = new Dictionary<string, ItemSourceStatus>();

        // Left set, with the skip reasons cleared above, the panel would report every enabled
        // collection read before any pass had run.
        hasCollected = false;

        // Queued work uploaded after a character switch would credit one character's unlocks to another.
        scheduler.Reset();

        // A cap cut depends on how much the departing character collected; kept, the next
        // character's cut would go unlogged.
        capWarnings.Reset();

        // An assembly failure leaves no upload-log row, only its log line; clearing lets a relog report
        // it in full again.
        assembleFailures.Reset();

        // outcomeWarnings stays: the upload task owns it and may be running, so NoteSuccess clears it.

        // The log diffs each row against the nearest older one, so the next character's first sync
        // would show "(changed)" against the departing character's counts.
        uploadLog.Clear();
    }

    /// <summary>
    /// The local player unlocked something. Routes it to the collector that owns that sheet.
    /// </summary>
    /// <remarks>
    /// It names no category: each collector says whether the row is its own. Routing rather than
    /// sweeping matters because an <c>unlock</c> upload permanently stamps its moment as the acquisition
    /// date of every category in it, so a sweep would date every first-time quest to one mount unlock.
    /// The <c>identity is null</c> guard keeps this inert before opt-in, since the identity is captured
    /// only past the upload gate.
    /// </remarks>
    private void OnUnlock(RowRef rowRef)
    {
        if (identity is null)
            return;

        var now = timeProvider.GetUtcNow();

        foreach (var collector in collectors)
        {
            // A collector with no unlockable sheet, such as a possession count, is not IUnlockAware.
            if (collector is IUnlockAware aware && aware.Handles(rowRef))
            {
                scheduler.NotifyUnlock(collector.CategoryKey, now);
                return;
            }
        }
    }

    /// <summary>
    /// Schedules an incremental upload for one category, for a collection whose facts arrive as a
    /// game event rather than as an unlock.
    /// </summary>
    /// <param name="categoryKey">The category to upload, named by whatever captured the change.</param>
    /// <remarks>
    /// The upload is an unlock's in every way, debounce and acquisition stamping included. The
    /// <c>identity is null</c> guard keeps this inert before opt-in; see <see cref="OnUnlock"/>.
    /// </remarks>
    public void NotifyCategoryChanged(string categoryKey)
    {
        if (identity is null)
            return;

        scheduler.NotifyUnlock(categoryKey, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Holds a category's new facts for the next upload, for a collection whose facts can wait (see
    /// <see cref="SyncScheduler.NotifyHeld"/>).
    /// </summary>
    /// <param name="categoryKey">The category holding them, named by whatever captured them.</param>
    /// <remarks>
    /// Inert before opt-in, like <see cref="OnUnlock"/>. Facts held before the character is read ride
    /// the login sweep.
    /// </remarks>
    public void NotifyCategoryHeld(string categoryKey)
    {
        if (identity is null)
            return;

        scheduler.NotifyHeld(categoryKey, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Runs every frame on the framework thread. Must stay cheap: this is the game's render loop.
    /// </summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        // SyncTickPlan decides how much of this frame runs and which halts the poll survives.
        var plan = SyncTickPlan.Decide(
            UploadGate.CanContactServer(settings),
            pollIsWorthwhile: SyncTickPlan.PollSurvivesHalt(HaltStatus),
            blockedPendingUserAction);

        if (plan == TickAction.Nothing)
            return;

        var now = timeProvider.GetUtcNow();

        // Polled even while the kill switch is off: that is how the plugin learns it flipped back.
        PollConfigIfDue(now);

        DispatchPausedPress(now);

        EndSyncWaitIfResumed(now);

        if (plan == TickAction.PollOnly)
            return;

        // One read for the whole frame, so a poll landing mid-frame cannot pair one manifest's items
        // with another's version.
        var config = remoteConfig;

        CaptureIdentityIfSettled(now);

        // Hold sweeps until this load's first config poll answers, either way, or the first sweep has no
        // item manifest and skips items. The trigger stays queued, so this costs one config round trip.
        if (remoteConfig is null && !firstConfigAnswerSeen)
            return;

        if (identity is null || uploadInFlight)
            return;

        // The server's global kill switch, honored locally to save a round trip that would only 503.
        if (!UploadGate.CanUpload(settings, config))
            return;

        var due = scheduler.Poll(now);
        if (due is null)
            return;

        StartUpload(due, config);
    }

    // --- Work ------------------------------------------------------------------------------

    /// <summary>Reads the character once the login has settled.</summary>
    /// <remarks>
    /// Commits only a usable character (see <see cref="CharacterIdentity.IsUsable"/>). An unusable
    /// character is read up to <see cref="MaxIdentityAttempts"/> times, then abandoned until a "Sync now"
    /// press or the next login.
    /// </remarks>
    private void CaptureIdentityIfSettled(DateTimeOffset now)
    {
        if (loginSettledAt is not { } settledAt || now < settledAt)
            return;

        // Not populated yet. Re-checked every frame, which is cheap: two reads and no logging.
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;

        CharacterIdentity candidate;
        try
        {
            // Hashed here, where it is read (see ContentIdHash).
            candidate = new CharacterIdentity
            {
                ContentIdHash = ContentIdHash.Compute(playerState.ContentId),
                Name = playerState.CharacterName,

                // A lazy sheet reference: it fails to resolve while the sheet loads, or for a world
                // newer than the installed game data.
                HomeWorld = playerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            // Never upload for a character we cannot identify: the server binds data to the hash, so
            // a guess would write to the wrong character.
            log.Error(
                ex, "Could not read the local character; syncing stays idle until Sync now or next login.");
            loginSettledAt = null;
            identityCaptureGaveUp = true;
            return;
        }

        // Never cache an identity the server would reject. See CharacterIdentity.IsUsable.
        if (!CharacterIdentity.IsUsable(candidate.Name, candidate.HomeWorld))
        {
            if (++identityAttempts >= MaxIdentityAttempts)
            {
                log.Warning(
                    "The local character's name or home world never became readable; " +
                    "syncing stays idle until Sync now or next login.");
                loginSettledAt = null;
                identityCaptureGaveUp = true;
                return;
            }

            loginSettledAt = now + IdentityRetryDelay;
            return;
        }

        loginSettledAt = null;
        identityAttempts = 0;
        identityCaptureGaveUp = false;
        identity = candidate;

        log.Debug("Character loaded; queuing a login sync.");
        scheduler.Request(SyncTrigger.Login, now);
    }

    /// <summary>Collects on the framework thread, then hands the payload to a background upload.</summary>
    /// <param name="due">The work the scheduler handed out; already consumed, so never reissued.</param>
    /// <param name="config">The frame's snapshot of the server config, so it cannot change mid-build.</param>
    private void StartUpload(SyncDue due, ConfigResponse? config)
    {
        CollectionSnapshot snapshot;
        SyncRequest request;
        UploadLogEntry logDraft;

        // A throw here would lose the sweep and escape into Dalamud's dispatch. CollectorRunner guards
        // each collector; this guards everything around them.
        try
        {
            snapshot = CollectorRunner.Run(
                CollectorSelection.For(collectors, due.Categories), settings, config);

            // Set here, not on the upload's outcome: the panel reports what was read, whether or not
            // the payload ever ships.
            hasCollected = true;

            // Without this, a server bug serving an oversized manifest would look like missing counts.
            foreach (var warning in truncationWarnings.LinesFor(
                snapshot.TruncatedManifests, config?.ManifestVersion))
            {
                log.Warning(warning);
            }

            // Bounded to the contract's caps (see PayloadCaps). Everything below sees the bounded
            // snapshot, whose cut categories do not claim completeness.
            var (boundedSnapshot, droppedByCap) = PayloadCaps.Bound(snapshot);
            if (droppedByCap.Count > 0)
            {
                // The dedupe below rations only the log lines, never the truncation.
                snapshot = boundedSnapshot;

                foreach (var line in capWarnings.LinesFor(droppedByCap))
                    log.Warning($"Payload cap: {line}.");
            }

            LogCollectionCost(snapshot, due.Trigger);

            // The full facts as shipped, for checking values and not just counts. They stay local in
            // /xllog, and the user already consented to uploading them. Verbose, not Debug: a quests line
            // is tens of kilobytes. With Verbose off, the level check skips serializing them on the
            // framework thread.
            if (log.MinimumLogLevel <= LogEventLevel.Verbose)
            {
                foreach (var (categoryKey, facts) in snapshot.Collections)
                    log.Verbose($"{due.Trigger} facts for {categoryKey}: {facts.ToJsonString()}");
            }

            // Kept for the settings window; the merge rules live in CollectionStatusMemory.
            RememberSkipReasons(snapshot);
            RememberPartialNotes(snapshot);
            RememberCollectedDetails(snapshot);
            RememberSourceNotes(snapshot);

            if (snapshot.Collections.Count == 0)
            {
                // An empty `collections` object is valid but asserts nothing, so sending it would only
                // spend a rate-limit slot.
                log.Debug($"Nothing to upload for {due.Trigger}; skipping.");
                return;
            }

            request = SyncPayloadBuilder.Build(
                identity!, pluginVersion, due.Trigger, snapshot, config?.ManifestVersion);

            // The completeness claims as shipped, read off the request because the builder withholds
            // some. Without this line only the website shows what the upload asserted was findable.
            if (log.MinimumLogLevel <= LogEventLevel.Verbose && request.CollectionScopes is { } scopes)
                log.Verbose($"{due.Trigger} declared complete: {string.Join(", ", scopes.Keys)}");

            // Drafted from the snapshot the payload was built from, so the log shows what went out; the
            // response fills in the rest. Inside the try because Draft serializes and hashes the facts.
            logDraft = UploadLogEntry.Draft(
                timeProvider.GetUtcNow(), due.Trigger, snapshot, config?.ManifestVersion);
        }
        catch (Exception ex)
        {
            // Reported in full once: a pass that fails to assemble usually fails on the next sweep too.
            if (assembleFailures.IsFirstSighting(ex))
                log.Error(ex, $"Could not assemble the {due.Trigger} upload; skipping it.");
            else
                log.Debug(ex, $"Could not assemble the {due.Trigger} upload again; skipping it.");
            return;
        }

        uploadInFlight = true;

        // Off the framework thread, JSON serialization included. No token: Task.Run skips the delegate
        // for an already-canceled one, so UploadAsync's finally would never clear uploadInFlight.
        var startedFor = sessionGeneration;
        _ = Task.Run(() => UploadAsync(request, due, startedFor, logDraft));
    }

    /// <summary>
    /// Folds this pass's skip reasons into the settings window's memory; see
    /// <see cref="CollectionStatusMemory"/>.
    /// </summary>
    private void RememberSkipReasons(CollectionSnapshot snapshot) =>
        lastSkipped = CollectionStatusMemory.MergeSkipReasons(lastSkipped, snapshot);

    /// <summary>
    /// Merges this pass's partial-read phrases into the settings window's memory; see
    /// <see cref="CollectionStatusMemory"/>.
    /// </summary>
    private void RememberPartialNotes(CollectionSnapshot snapshot) =>
        lastPartialNotes = CollectionStatusMemory.MergePartialNotes(lastPartialNotes, snapshot);

    /// <summary>
    /// Merges this pass's healthy-chip hover copy into the settings window's memory; see
    /// <see cref="CollectionStatusMemory"/>.
    /// </summary>
    private void RememberCollectedDetails(CollectionSnapshot snapshot) =>
        lastCollectedDetails =
            CollectionStatusMemory.MergeCollectedDetails(lastCollectedDetails, snapshot);

    /// <summary>
    /// Replaces the settings window's source notes with this pass's, when it reported any. See
    /// <see cref="lastSourceNotes"/>.
    /// </summary>
    private void RememberSourceNotes(CollectionSnapshot snapshot)
    {
        if (snapshot.SourceNotes.Count > 0)
            lastSourceNotes = snapshot.SourceNotes;
    }

    /// <summary>
    /// Reports what this collection pass cost the frame it ran in.
    /// </summary>
    /// <remarks>
    /// Warns about a repeatedly expensive pass, because overrunning the frame stutters the game; the fix
    /// is to spread the work across frames, not to collect less. <see cref="CollectionCost.IsAlarming"/>
    /// decides which passes qualify.
    /// </remarks>
    private void LogCollectionCost(CollectionSnapshot snapshot, SyncTrigger trigger)
    {
        var isFirstPass = !anyPassHasRun;
        var cost = CollectionCost.From(snapshot, isFirstPassOfSession: isFirstPass);

        if (cost.IsEmpty)
            return;

        // A pass where no collector ran warmed nothing, so it must not use up the first-pass exemption.
        anyPassHasRun = true;

        // These strings are built whatever the log level. Fine once per upload; never copy this into a
        // per-frame path.
        if (budgetWarnings.ShouldReport(cost))
        {
            log.Warning(
                $"A {trigger} collection pass took {cost.Total.TotalMilliseconds:F1}ms on the " +
                $"framework thread, over the " +
                $"{CollectionCost.FrameBudgetWarningThreshold.TotalMilliseconds:F0}ms budget: " +
                $"{cost.Breakdown}. This may stutter the game.");
            return;
        }

        // Already named. A slow machine overruns on every pass, so later overruns go to Debug.
        if (cost.IsAlarming)
        {
            log.Debug(
                $"{trigger} collection took {cost.Total.TotalMilliseconds:F1}ms, still over " +
                $"budget: {cost.Breakdown}");
            return;
        }

        // An exempt overrun is still logged: it shows what warm-up costs on a real client.
        var note = cost.OverBudget && isFirstPass
            ? " (first pass of this session; mostly one-time warm-up)"
            : string.Empty;

        log.Debug(
            $"{trigger} collection took {cost.Total.TotalMilliseconds:F1}ms{note}: {cost.Breakdown}");
    }

    /// <summary>Uploads off the framework thread, retrying once if the failure was transient.</summary>
    /// <param name="due">The work this upload carries out.</param>
    /// <param name="startedFor">The session generation this upload belongs to.</param>
    /// <param name="logDraft">The upload-log summary of this request, awaiting its outcome.</param>
    private async Task UploadAsync(
        SyncRequest request, SyncDue due, int startedFor, UploadLogEntry logDraft)
    {
        var trigger = due.Trigger;

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var response = await apiClient.PostSyncAsync(request, lifetimeToken).ConfigureAwait(false);

                // A logout while this was in the air makes it stale (see sessionGeneration), and the
                // server's monotonic writes make dropping it harmless. Checked per attempt to catch a
                // logout during the retry delay; a dropped late 429 costs at most one extra request.
                if (startedFor != sessionGeneration)
                {
                    log.Debug($"Discarding a {trigger} sync response from a previous session.");
                    return;
                }

                // The draft plus the response's diagnostics, for a pasted bug report.
                var settledEntry = logDraft with
                {
                    Status = response.Status,
                    Attempt = attempt + 1,
                    RetryAfter = response.RetryAfter,
                    HttpStatusCode = response.HttpStatusCode,
                    Detail = UploadLogText.IssuesText(response.Error),
                    ProvenSteps = response.Value?.ProvenSteps,
                };

                if (HandleResponse(response, due, startedFor))
                {
                    // Recorded even for failures, since a log that hid them would mislead. Rechecked,
                    // because HandleResponse also returns true for a stale response it discarded.
                    if (startedFor == sessionGeneration)
                        uploadLog.Record(settledEntry);

                    return;
                }

                if (!RetryPolicy.ShouldRetryNow(response.Status, attempt))
                {
                    if (startedFor == sessionGeneration)
                    {
                        uploadLog.Record(settledEntry);
                        scheduler.MarkFailed(due, timeProvider.GetUtcNow());
                    }

                    return;
                }

                // Safe to repeat; see RetryPolicy.ShouldRetryNow.
                var code = HttpCodeNote(response.HttpStatusCode);
                log.Debug($"Retrying {trigger} sync after {response.Status}{code}.");
                await Task.Delay(RetryPolicy.TransientRetryDelay, lifetimeToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Unloaded mid-upload. Not logged: the log service may already be torn down.
        }
        catch (Exception ex)
        {
            // A last-resort net: an exception escaping a discarded task surfaces later on the finalizer
            // thread, with its context gone.
            if (uploadFailures.IsFirstSighting(ex))
                log.Error(ex, "Unexpected failure during sync upload.");
            else
                log.Debug(ex, "Unexpected failure during sync upload, again.");
        }
        finally
        {
            uploadInFlight = false;
        }
    }

    /// <summary>
    /// Applies the server's answer to the scheduler. Returns true when the attempt is settled.
    /// </summary>
    /// <param name="due">The work the upload carried out.</param>
    /// <param name="startedFor">The session generation the upload belongs to.</param>
    private bool HandleResponse(ApiResponse<SyncResponse> response, SyncDue due, int startedFor)
    {
        var trigger = due.Trigger;

        // Rechecked at the moment of writing, which shrinks the logout race from a round trip to a few
        // instructions.
        if (startedFor != sessionGeneration)
        {
            log.Debug($"Discarding a {trigger} sync response from a previous session.");
            return true;
        }

        lastStatusCode = (int)response.Status;
        var now = timeProvider.GetUtcNow();

        if (response.Status == ApiStatus.Ok)
        {
            scheduler.MarkUploaded(due, now);
            lastSyncedAtBox = now;

            // A success ends the run of failures, so the next failure is reported even if its status
            // repeats.
            if (outcomeWarnings.NoteSuccess())
                log.Information($"Sync recovered on the {trigger} upload.");

            // It also shows a wait that ended early was rightly ended (see SyncResumeWait.UploadAccepted).
            syncWait.UploadAccepted();

            // Categories a per-category kill switch refused, logged so the user knows why one never
            // appears on the website.
            var skipped = response.Value?.SkippedCategories;

            // Folded to one bounded line: these names come from an overridable backend, and a newline
            // could forge log lines. Joining first is safe because ApiClient.MaxResponseBytes caps the body.
            if (skipped is {Count: > 0}
                && ServerText.SingleLine(string.Join(", ", skipped)) is { } names)
            {
                log.Information($"Server skipped: {names}");
            }

            return true;
        }

        // The server asked for a wait; honor Retry-After when it sent one. A 503 sync_disabled may end
        // early, and syncWait is armed before the scheduler's wait is set (see SyncResumeWait).
        if (RetryPolicy.BackoffUntil(response.Status, response.RetryAfter, now) is { } until)
        {
            var switchedOff = RetryPolicy.EndsWhenSyncResumes(response.Status, response.Error?.Error);
            if (switchedOff && syncWait.Arm(configPollsStarted, now, until))
            {
                scheduler.BackOffUntilSyncResumes(until, trigger);
                log.Information(
                    $"Backing off until {until:u}, or until syncing is back on, after {response.Status}.");
            }
            else
            {
                scheduler.BackOffUntil(until);
                log.Information($"Backing off until {until:u} after {response.Status}.");
            }

            // Either way it brings the next poll forward, so the card names the pause.
            if (switchedOff)
                BringConfigPollForward();

            scheduler.MarkFailed(due, now);
            return true;
        }

        // Nothing a retry can fix: a bad token, an unmatched character, a misconfigured backend.
        if (RetryPolicy.RequiresUserAction(response.Status))
        {
            // The status first, then the flag: see haltStatusCode.
            haltStatusCode = (int)response.Status;
            blockedPendingUserAction = true;
            log.Warning($"Sync halted: {response.Status}. The user must resolve this.");
            return true;
        }

        // Terminal and a plugin bug (a 400, 405 or 413): retrying the same body cannot help, so drop it.
        if (ApiStatusMap.IsTerminal(response.Status))
        {
            // The next trigger earns the same rejection, so report it once until an upload gets through.
            if (outcomeWarnings.ShouldReport(response.Status))
                log.Error($"Sync rejected: {response.Status}. Dropping this upload.");
            else
                log.Debug($"Sync rejected again: {response.Status}. Dropping this upload.");

            return true;
        }

        // Transient. The caller decides whether an attempt remains.
        if (outcomeWarnings.ShouldReport(response.Status))
            log.Warning($"Sync failed: {response.Status}{HttpCodeNote(response.HttpStatusCode)}.");
        else
            log.Debug($"Sync failed again: {response.Status}{HttpCodeNote(response.HttpStatusCode)}.");

        return false;
    }

    /// <summary>
    /// " (HTTP 530)" when a response arrived, and nothing when none did, for the log lines of a
    /// transient failure and its retry, whose statuses each stand for many codes.
    /// </summary>
    private static string HttpCodeNote(int? httpStatusCode) =>
        httpStatusCode is { } code ? $" (HTTP {code})" : string.Empty;

    /// <summary>Fetches <c>/config</c> when it is due, adopting the server's cadence and manifest.</summary>
    private void PollConfigIfDue(DateTimeOffset now)
    {
        if (nextConfigPollAt is { } dueAt && now < dueAt)
            return;

        StartConfigPoll(now);
    }

    /// <summary>
    /// Starts a <c>/config</c> poll and schedules the next. Every poll starts here: the frame tick,
    /// <see cref="RequestOnboardingConfigPoll"/> and <see cref="DispatchPausedPress"/>.
    /// </summary>
    /// <remarks>Framework thread only: it writes <see cref="nextConfigPollAt"/>.</remarks>
    private void StartConfigPoll(DateTimeOffset now)
    {
        // Every caller runs on the framework thread, so two polls asked for in the same frame cannot
        // both get through.
        if (configPollInFlight)
            return;

        nextConfigPollAt = now + ConfigPollInterval;
        configPollInFlight = true;

        // Numbered as it starts (see configPollsStarted). The framework thread is the only writer, so
        // the increment needs no Interlocked.
        configPollsStarted++;
        var pollNumber = configPollsStarted;

        // No token: an already-canceled one would make Task.Run skip the delegate, stranding
        // configPollInFlight at true.
        var startedFor = sessionGeneration;
        _ = Task.Run(() => PollConfigAsync(startedFor, pollNumber));
    }

    /// <param name="startedFor">The session generation this poll belongs to.</param>
    /// <param name="pollNumber">This poll's number, counting polls from 1 as they start.</param>
    private async Task PollConfigAsync(int startedFor, int pollNumber)
    {
        // The finally reads this to choose the full interval or the short retry, so every exit follows
        // one rule.
        var answered = false;

        // The answer's `enabled`, or null when no config came back. The finally settles a press made
        // during a pause with it.
        bool? answeredEnabled = null;

        try
        {
            var response = await apiClient.GetConfigAsync(lifetimeToken).ConfigureAwait(false);

            // A refusal only the user can fix is an answer too: the short retry is for a poll that could
            // not reach the server, and asking again sooner only repeats the question.
            answered = response.IsSuccess || RetryPolicy.RequiresUserAction(response.Status);

            if (response.IsSuccess)
            {
                // Applied even after a logout: the config describes the server and token, not the
                // character.
                var config = response.Value!;

                remoteConfig = config;

                // After the config, never before: see ConfigAnsweredPoll.
                configAnsweredPoll = pollNumber;
                answeredEnabled = config.Enabled;

                scheduler.ApplyIntervals(config.Intervals);

                // One-time pre-group consent migration, so an existing user's item scan does not go silent.
                // Queued so it never lands mid-pass. The login settle delay lets it beat the login sweep,
                // and losing that race delays one upload.
                _ = framework.RunOnFrameworkThread(() =>
                {
                    // At teardown the migration waits for the next load's first poll.
                    if (lifetimeToken.IsCancellationRequested)
                        return;

                    try
                    {
                        // Past onboarding only (see PluginSettings.SettleItemGroupConsent), and never on an
                        // empty groups array, which would burn the one-time flag before real groups arrive.
                        if (settings.OnboardingComplete
                            && config.ItemManifestGroups is { Count: > 0 } groups
                            && settings.MigrateItemGroupConsent(
                                groups,
                                ManifestConsent.AnyManifestCategoryEnabled(collectors, settings)))
                        {
                            saveSettings();
                        }
                    }
                    catch (Exception ex)
                    {
                        // A last-resort net for a discarded task; see UploadAsync.
                        log.Error(ex, "Item group consent migration failed.");
                    }
                });

                return;
            }

            // A halt is session-scoped, so a stale answer raises none. Nor does a poll during onboarding: the
            // wizard shows the token's verdict; a halt would outlive the wizard and block the first sweep.
            if (settings.OnboardingComplete
                && startedFor == sessionGeneration
                && RetryPolicy.RequiresUserAction(response.Status))
            {
                // The status first, then the flag: see haltStatusCode.
                haltStatusCode = (int)response.Status;
                blockedPendingUserAction = true;
                log.Warning($"Config poll halted: {response.Status}. The user must resolve this.");
            }

            // Any other failure keeps the previous config, which beats having none.
        }
        catch (OperationCanceledException)
        {
            // Unloaded mid-poll.
        }
        catch (Exception ex)
        {
            // Reported in full once: a throw reschedules to the short retry, so a repeatable one would
            // report every two minutes.
            if (configPollFailures.IsFirstSighting(ex))
                log.Error(ex, "Unexpected failure polling config.");
            else
                log.Debug(ex, "Unexpected failure polling config, again.");
        }
        finally
        {
            // Any answer, even a failure or cancellation, releases the first-sweep hold. The upload
            // will surface whatever is wrong with the server.
            firstConfigAnswerSeen = true;

            // Any answer ends the wizard's wait; see OnboardingConfigPending.
            onboardingConfigPending = false;

            // Settles a paused press this poll answers, whatever came back, and queues the sweep if
            // syncing is back on (see PausedPress.Settle).
            if (pausedPress.Settle(pollNumber, answeredEnabled))
                QueueManualSweep(answeredAt: timeProvider.GetUtcNow());

            configPollInFlight = false;

            if (!answered)
                RescheduleConfigPollSoon();
        }
    }

    /// <summary>
    /// Starts the poll a "Sync now" press made during a pause asked for, once it is due (see
    /// <see cref="PausedPress.PollDue"/>).
    /// </summary>
    /// <remarks>Framework thread only: it starts the poll through <see cref="StartConfigPoll"/>.</remarks>
    private void DispatchPausedPress(DateTimeOffset now)
    {
        if (!pausedPress.PollDue(configPollsStarted, configPollInFlight, now))
            return;

        pausedPress.PollStarted(now);
        StartConfigPoll(now);
    }

    /// <summary>
    /// Ends the collections upload's wait for syncing to resume once the latest <c>/config</c> says
    /// syncing is back on (see <see cref="SyncResumeWait.TryEnd"/>).
    /// </summary>
    private void EndSyncWaitIfResumed(DateTimeOffset now)
    {
        // The poll number before the config, so the config is from that poll or a later one (see
        // ConfigAnsweredPoll).
        var answeredPoll = configAnsweredPoll;
        var config = remoteConfig;

        if (!syncWait.TryEnd(answeredPoll, config is { Enabled: true }, now))
            return;

        scheduler.SyncResumed();
        log.Information("Syncing is back on; uploads resume.");
    }

    /// <summary>
    /// Queues the sweep a "Sync now" press made during a pause was for. The poll task calls it, so it
    /// marshals to the framework thread, where every scheduler request is made.
    /// </summary>
    /// <param name="answeredAt">When the answer saying syncing is back on arrived.</param>
    /// <remarks>
    /// The same answer may also end the collections wait, whose sweep can go out before this lands;
    /// <see cref="SyncScheduler.RequestUnlessSweptSince"/> then drops this request. A sweep handed out
    /// on a frame that began just before the answer lets it through, costing one extra upload.
    /// </remarks>
    private void QueueManualSweep(DateTimeOffset answeredAt)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (lifetimeToken.IsCancellationRequested)
                return;

            scheduler.RequestUnlessSweptSince(SyncTrigger.Manual, answeredAt, timeProvider.GetUtcNow());
        });
    }

    /// <summary>
    /// Brings the next <c>/config</c> poll forward to the short retry delay, on the framework thread,
    /// never pushing back one due sooner (see <see cref="SyncTickPlan.BringPollForward"/>).
    /// </summary>
    private void RescheduleConfigPollSoon()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (lifetimeToken.IsCancellationRequested)
                return;

            nextConfigPollAt = SyncTickPlan.BringPollForward(
                nextConfigPollAt, timeProvider.GetUtcNow() + ConfigPollRetryDelay);
        });
    }
}
