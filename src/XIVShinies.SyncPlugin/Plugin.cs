#if DEBUG
// Only the development-build commands use it.
using System;
#endif
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Achievements;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
#if DEBUG
// Only /shinies dumpslots uses it.
using XIVShinies.SyncPlugin.Diagnostics;
#endif
using XIVShinies.SyncPlugin.Occult;
using XIVShinies.SyncPlugin.Sync;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The plugin entry point. Dalamud discovers the one class that implements <see cref="IDalamudPlugin"/>,
/// constructs it on load, and calls <see cref="Dispose"/> on unload.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // --- Injected Dalamud services -------------------------------------------------------
    // Dalamud fills each [PluginService] property before the constructor runs, hence `null!`.

    /// <summary>Dalamud's per-plugin handle: config persistence, UI builder hooks, the manifest.</summary>
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>Registers and routes slash commands like <c>/shinies</c>.</summary>
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;

    /// <summary>Writes to the Dalamud log (view in-game with <c>/xllog</c>).</summary>
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    /// <summary>Reads the game's static data sheets, such as quests, mounts and achievements.</summary>
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    /// <summary>
    /// Answers what the <b>local</b> player has unlocked or completed. It cannot be asked about anyone
    /// else.
    /// </summary>
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;

    /// <summary>
    /// The game's per-frame loop. Used to prove a collector is on the framework thread before it
    /// reads game memory, and to marshal work onto that thread.
    /// </summary>
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    /// <summary>Login and logout events for the local session.</summary>
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    /// <summary>
    /// The <b>local</b> character's identity: content id, name, home world. Dalamud's rules forbid
    /// collecting identifiers for any other player, and this service exposes no way to.
    /// </summary>
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    /// <summary>Loads image files into GPU textures that ImGui can draw.</summary>
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    /// <summary>
    /// The live FATE table for the current zone: world state, not player data. The occult tracker
    /// reads it inside an Occult Crescent instance.
    /// </summary>
    [PluginService] internal static IFateTable FateTable { get; private set; } = null!;

    /// <summary>
    /// Game-window lifecycle events, for passively reading the knowledge level from the occult review
    /// window and the bestiary from the Master's Bestiary window, each only when the user opens it.
    /// </summary>
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    /// <summary>
    /// The local character's condition flags. The Crucible run sharing reads only "in combat"; while it
    /// is set it reads no Crucible window or own HP, only the familiar's (see <see cref="BuddyList"/>).
    /// </summary>
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    /// <summary>
    /// The <b>local</b> player's own companion, pet and allies. The Crucible run sharing reads only the
    /// pet's data id and HP; there, the pet is the familiar the player has out.
    /// </summary>
    [PluginService] internal static IBuddyList BuddyList { get; private set; } = null!;

    /// <summary>
    /// Finds a game window by name: a Crucible window the player has open, such as the run HUD, or
    /// whether the Achievements and Party Finder windows are open.
    /// </summary>
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    // --- Plugin state --------------------------------------------------------------------

    /// <summary>The persisted settings object (see Configuration.cs).</summary>
    public Configuration Configuration { get; init; }

    // The name only has to be unique.
    private readonly WindowSystem windowSystem = new("XIVShiniesSync");
    private readonly MainWindow mainWindow;

    private readonly ApiClient apiClient;

    // Every registered fact source. They hold no unmanaged resources, so nothing to dispose.
    private readonly IReadOnlyList<ICollector> collectors;

    // Captures the knowledge level from the occult review window.
    private readonly KnowledgeObserver knowledgeObserver;

    // Learns the held beasts and their ranks from the bestiary window and the Crucible NPC's
    // conversation, when the player opens them.
    private readonly TamedBeastObserver tamedBeastObserver;

    // Takes the progress the game receives when the player opens an achievement.
    private readonly AchievementProgressObserver achievementProgressObserver;

    // Drives uploads on login, unlock and interval.
    private readonly SyncManager syncManager;

    // Streams an Occult Crescent instance's CE and FATE state to the live tracker.
    private readonly OccultManager occultManager;

    // Shares what the Crucible's windows show as the character runs its boards.
    private readonly CrucibleManager crucibleManager;

    /// <summary>
    /// Dalamud calls this once on load. <see cref="Dispose"/> must undo everything wired here:
    /// handlers, events, windows.
    /// </summary>
    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Migrations run before anything reads the config, so a new default-on setting cannot opt in
        // a user whose wizard ran long ago (see PluginSettings.ApplyUpgradeMigrations).
        if (Configuration.Version < Configuration.CurrentVersion)
        {
            // Saved even when nothing changed: the version bump records that migrations ran, so they
            // never run twice.
            Configuration.Settings.ApplyUpgradeMigrations(Configuration.Version);
            Configuration.Version = Configuration.CurrentVersion;
            Configuration.Save();
        }

        // If this throws, Dalamud never calls Dispose(), and a subscribed handler would keep firing
        // for the session. The catch tears down what was built, in Dispose()'s order, and rethrows.
        try
        {
            // The manifest carries the build's version, which becomes the User-Agent and the
            // payload's pluginVersion.
            var version = PluginInterface.Manifest.AssemblyVersion?.ToString() ?? "0.0.0";
            apiClient = new ApiClient(Configuration.Settings, version);

            // Before the collectors, since the phantom jobs collector reads it. It records only what
            // the player puts on screen (see KnowledgeObserver).
            knowledgeObserver = new KnowledgeObserver(
                AddonLifecycle, ClientState,
                key => CollectorGate.IsCapturePermitted(key, Configuration.Settings),
                Log);

            // Before the collectors, since the tamed beasts collector reads it. It gets a consent callback
            // rather than the settings and asks about its own collection, so no category is named here.
            tamedBeastObserver = new TamedBeastObserver(
                ClientState, DataManager, AddonLifecycle, Framework,
                key => CollectorGate.IsCapturePermitted(key, Configuration.Settings),
                Log);

            // Before the collectors, since the achievement progress collector reads it. It gets a consent
            // callback and asks about its own collection, so no category is named here.
            achievementProgressObserver = new AchievementProgressObserver(
                ClientState, Framework, GameGui,
                key => CollectorGate.IsCapturePermitted(key, Configuration.Settings),
                Log);

            // Nothing reads the game until something runs them.
            collectors = CollectorRegistry.Create(
                DataManager, UnlockState, Framework, knowledgeObserver, tamedBeastObserver,
                achievementProgressObserver);

            // Sets the "New" badge baseline. It needs the registered collectors, so it runs here
            // rather than with the migrations; after the first load it costs one flag read.
            if (Configuration.Settings.InitializeSeenCategories(collectors.Select(c => c.CategoryKey)))
                Configuration.Save();

            // After the baseline, which defines "never shown", and before the window can mark one
            // shown. See ManifestConsent.FixedScopeCategoryKeys for which collections qualify.
            var autoEnabled = Configuration.Settings.AutoEnableUnseenCategories(
                ManifestConsent.FixedScopeCategoryKeys(collectors));
            if (autoEnabled.Count > 0)
            {
                Configuration.Save();

                // The one path that switches a collection on without a click, so it leaves a trace.
                Log.Information(
                    $"Switched on {string.Join(", ", autoEnabled)} — new since this install was " +
                    "last shown the list, and new collections are set to start on.");
            }

            // It subscribes at once, but every path checks the upload gate first, so nothing is sent
            // before opt-in. Configuration.Save arrives as an Action, so it never holds the config shell.
            syncManager = new SyncManager(
                Framework, ClientState, PlayerState, UnlockState, Log,
                apiClient, Configuration.Settings, Configuration.Save, collectors, version);

            // A learned beast or rank uploads promptly, like an unlock; one learned before this line rides
            // the login sync. The event carries its category key, so neither side names the collection.
            tamedBeastObserver.BeastsLearned += syncManager.NotifyCategoryChanged;

            // A reading or sighting waits for the next upload rather than spending one of its own each
            // time.
            achievementProgressObserver.ReadingTaken += syncManager.NotifyCategoryHeld;
            knowledgeObserver.SightingTaken += syncManager.NotifyCategoryHeld;

            // After the SyncManager, whose identity and server config it reads. Silent until the user
            // opts in: the same consent switches gate it, plus its own toggle.
            occultManager = new OccultManager(
                Framework, ClientState, FateTable, PlayerState, Log,
                apiClient, Configuration.Settings, syncManager, version);

            // After the SyncManager, whose identity, server config and halt it reads. Its own toggle
            // starts off; until it is on, it reads only static game data (see CrucibleManager).
            crucibleManager = new CrucibleManager(
                Framework, ClientState, Condition, GameGui, AddonLifecycle, DataManager, BuddyList, Log,
                apiClient, Configuration.Settings, syncManager, version);

            // After the sync manager and collectors, which it reads. The texture provider loads the
            // masthead's mascot; the font pieces give it a heading-sized font and FontAwesome icons.
            mainWindow = new MainWindow(
                Configuration, apiClient, syncManager, collectors, TextureProvider,
                PluginInterface.UiBuilder.FontAtlas,
                PluginInterface.UiBuilder.IconFontHandle,
                PluginInterface.UiBuilder.DefaultFontSpec.SizePx,
                version);
            windowSystem.AddWindow(mainWindow);

            // HelpMessage is the text /xlhelp shows.
            CommandManager.AddHandler(PluginMeta.CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = $"Toggle the {PluginMeta.DisplayName} window.",
            });

            CommandManager.AddHandler(PluginMeta.CommandAlias, new CommandInfo(OnCommand)
            {
                ShowInHelp = false,
            });

            // Every `+=` here needs a `-=` in Dispose. OpenMainUi and OpenConfigUi are the installer's
            // open button and settings gear.
            PluginInterface.UiBuilder.Draw += windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        }
        catch
        {
            // Fields never built are still null, so `?.` skips them, and removing an event or command
            // never added is harmless (a command only logs). So this mirrors Dispose() without flags.
            PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
            CommandManager.RemoveHandler(PluginMeta.CommandName);
            CommandManager.RemoveHandler(PluginMeta.CommandAlias);

            windowSystem.RemoveAllWindows();
            mainWindow?.Dispose();

            // Either may be unbuilt, so both are null-checked.
            if (tamedBeastObserver is not null && syncManager is not null)
                tamedBeastObserver.BeastsLearned -= syncManager.NotifyCategoryChanged;

            if (achievementProgressObserver is not null && syncManager is not null)
                achievementProgressObserver.ReadingTaken -= syncManager.NotifyCategoryHeld;

            if (knowledgeObserver is not null && syncManager is not null)
                knowledgeObserver.SightingTaken -= syncManager.NotifyCategoryHeld;

            crucibleManager?.Dispose();
            occultManager?.Dispose();
            syncManager?.Dispose();
            knowledgeObserver?.Dispose();
            tamedBeastObserver?.Dispose();
            achievementProgressObserver?.Dispose();
            apiClient?.Dispose();
            throw;
        }

        Log.Information($"{PluginMeta.DisplayName} loaded.");
    }

    /// <summary>
    /// Cleanup on unload. Mirrors the constructor: unsubscribes every event and removes every window
    /// and command handler.
    /// </summary>
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        // Before the window they dispatch into, so a /shinies typed mid-teardown cannot toggle a
        // window that is already gone.
        CommandManager.RemoveHandler(PluginMeta.CommandName);
        CommandManager.RemoveHandler(PluginMeta.CommandAlias);

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        // Before the SyncManager they notify, so no upload can be scheduled through a manager being
        // torn down.
        tamedBeastObserver.BeastsLearned -= syncManager.NotifyCategoryChanged;
        achievementProgressObserver.ReadingTaken -= syncManager.NotifyCategoryHeld;
        knowledgeObserver.SightingTaken -= syncManager.NotifyCategoryHeld;

        // Before the SyncManager and ApiClient they borrow: each unsubscribes and cancels its own
        // upload in flight.
        crucibleManager.Dispose();
        occultManager.Dispose();

        // Before the ApiClient: it unsubscribes and cancels any upload in flight, so nothing still
        // reaches for the client.
        syncManager.Dispose();

        // After the SyncManager whose collection passes read them: no pass can start once its
        // handlers are detached.
        knowledgeObserver.Dispose();
        tamedBeastObserver.Dispose();
        achievementProgressObserver.Dispose();

        apiClient.Dispose();
    }

    // The /shinies handler; `args` is anything after the command. Bare /shinies toggles the window.
    private void OnCommand(string command, string args)
    {
#if DEBUG
        // Development builds only; the shipped plugin recognizes no arguments.
        //   /shinies seedlog [version]   seed the log, optionally drawing that version in the
        //                                masthead (see MainWindow.OverrideVersionForScreenshots)
        //   /shinies dumpslots           audit the unlock bitmask's Mount sheet coverage, to /xllog
        //   /shinies dumpranks           write the beast rank record to /xllog
        //   /shinies forgetcharacter     act as if reading the character gave up, to check that
        //                                "Sync now" reads it again
        var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words[0].Equals("seedlog", StringComparison.OrdinalIgnoreCase))
        {
            syncManager.SeedUploadHistoryForScreenshots(collectors, DateTimeOffset.Now);

            if (words.Length > 1)
                mainWindow.OverrideVersionForScreenshots(words[1]);

            // Says what undoes it, since both are silent: a real sync adds a row that diffs against
            // the seeded ones, and logging out or switching character clears the log.
            Log.Information(
                "Upload log seeded for screenshots. Capture before the next sync; logging out clears it.");

            // Opened, not toggled, so an already open window stays open.
            mainWindow.IsOpen = true;
            return;
        }

        if (words.Length > 0 && words[0].Equals("dumpslots", StringComparison.OrdinalIgnoreCase))
        {
            // Marshaled: a command handler runs wherever the command arrived, and a bad game-memory
            // read raises a corrupted-state exception no catch can rescue.
            _ = Framework.RunOnFrameworkThread(
                () => UnlockSlotAudit.Run(ClientState, DataManager, UnlockState, Log));
            return;
        }

        if (words.Length > 0 && words[0].Equals("dumpranks", StringComparison.OrdinalIgnoreCase))
        {
            // Marshaled, since this reads game memory.
            _ = Framework.RunOnFrameworkThread(tamedBeastObserver.AuditRankRecord);
            return;
        }

        if (words.Length > 0 && words[0].Equals("forgetcharacter", StringComparison.OrdinalIgnoreCase))
        {
            // Opened as well, since the point is to look at the cards that name the state.
            syncManager.ForgetCharacterForTesting();
            mainWindow.IsOpen = true;
            return;
        }
#endif

        mainWindow.Toggle();
    }

    // Wired to the installer's open button and settings gear.
    private void ToggleMainUi() => mainWindow.Toggle();
}
