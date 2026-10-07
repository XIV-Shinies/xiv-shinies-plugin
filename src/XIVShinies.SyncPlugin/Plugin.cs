#if DEBUG
// DateTimeOffset, StringComparison and StringSplitOptions, for the development-build commands.
// Guarded with them: a Release compile of this file needs none.
using System;
#endif
using System.Collections.Generic;
// Path.Combine, for locating the mascot image next to the plugin DLL.
using System.IO;
// Select, to project the collectors down to the category keys the seen-baseline needs.
using System.Linq;
// Dalamud's command system (registering the /shinies slash command).
using Dalamud.Game.Command;
// The windowing system that draws and manages our ImGui windows.
using Dalamud.Interface.Windowing;
// Provides the [PluginService] attribute used for dependency injection below.
using Dalamud.IoC;
// Core plugin interfaces, including IDalamudPlugin and IDalamudPluginInterface.
using Dalamud.Plugin;
// The injectable Dalamud "services" live here (ICommandManager, IPluginLog, etc.).
using Dalamud.Plugin.Services;
// Our HTTPS client and its DTOs. A child namespace is not visible automatically — only enclosing
// namespaces are searched — so it needs an explicit using.
using XIVShinies.SyncPlugin.Api;
// The bestiary capture behind the tamed beasts collection.
using XIVShinies.SyncPlugin.Beastmaster;
// The Crucible run sharing (window observer, scheduler, uploader).
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
// The registered fact sources.
using XIVShinies.SyncPlugin.Collectors;
#if DEBUG
// UnlockSlotAudit, the development-build check behind /shinies dumpslots. Guarded with it: a
// Release compile of this file uses nothing from this namespace.
using XIVShinies.SyncPlugin.Diagnostics;
#endif
// The live occult instance tracker (reader, scheduler, uploader).
using XIVShinies.SyncPlugin.Occult;
// The upload orchestrator and its supporting policy classes.
using XIVShinies.SyncPlugin.Sync;
// Our own window classes.
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The plugin entry point. Dalamud discovers the one class that implements
/// <see cref="IDalamudPlugin"/>, constructs it on load, and calls <see cref="Dispose"/> on
/// unload. Think of the constructor as the plugin's "mount" and Dispose as its "unmount".
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // --- Injected Dalamud services -------------------------------------------------------
    // Dalamud uses dependency injection: rather than us importing/creating these services, the
    // framework *sets* them for us. Any static property tagged with [PluginService] gets filled
    // in by Dalamud before our constructor runs. It's conceptually like React context/props
    // being provided from above, except the values arrive via reflection into static slots.
    //
    // `internal` = visible anywhere in this project but not to other assemblies. `static` = one
    // shared slot for the whole plugin (there's only ever one Plugin instance). `= null!` is a
    // promise to the compiler: "this is non-null in practice (Dalamud fills it) — trust me, don't
    // warn." The `!` is the null-forgiving operator, the C# cousin of TS's `!` non-null assertion.

    /// <summary>Dalamud's per-plugin handle: config persistence, UI builder hooks, manifest, etc.</summary>
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>Registers and routes slash commands like <c>/shinies</c>.</summary>
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;

    /// <summary>Writes to the Dalamud log (view in-game with <c>/xllog</c>).</summary>
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    /// <summary>Reads the game's static data sheets (quests, mounts, achievements, …).</summary>
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    /// <summary>
    /// Answers what the <b>local</b> player has unlocked or completed. It exposes only the local
    /// player's state — there is no way to ask it about anyone else.
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
    /// The <b>local</b> character's identity — content id, name, home world. Dalamud's rules forbid
    /// collecting identifiers for any other player, and this service exposes no way to.
    /// </summary>
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    /// <summary>Loads image files into GPU textures that ImGui can draw.</summary>
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    /// <summary>
    /// The live FATE table for the current zone — world state, not player data. Read by the
    /// occult tracker while inside an Occult Crescent instance.
    /// </summary>
    [PluginService] internal static IFateTable FateTable { get; private set; } = null!;

    /// <summary>
    /// Game-window lifecycle events. Used to passively read the knowledge level from the occult
    /// review window, and the character's own bestiary from the Master's Bestiary window — each
    /// only when the user opens it themselves.
    /// </summary>
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    /// <summary>
    /// The local character's condition flags. The Crucible run sharing reads only "in combat", and
    /// reads nothing while it is set; the glamour collection checks whether a summoning bell is in
    /// use, so it is not read while a retainer's windows are open.
    /// </summary>
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    /// <summary>
    /// Finds a game window by name. The Crucible run sharing uses it to read a Crucible window the
    /// player already has open, such as the run HUD on entering a board; the glamour collection uses
    /// it to check whether a storage window (the Glamour Dresser and its outfit-glamour window, the
    /// Armoire, the saddlebag) is open, so it can wait for it to close (see
    /// <see cref="GlamourCollector"/>).
    /// </summary>
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    // --- Plugin state --------------------------------------------------------------------

    /// <summary>The persisted settings object (see Configuration.cs).</summary>
    // `{ get; init; }` is an auto-property that can be set only during construction, then becomes
    // read-only — like a `readonly` field you can still assign in the constructor.
    public Configuration Configuration { get; init; }

    // `readonly` fields can be assigned only here or in the constructor, then never reassigned.
    // The WindowSystem owns/draws our windows; the string is just a unique namespace for it.
    private readonly WindowSystem windowSystem = new("XIVShiniesSync");
    private readonly MainWindow mainWindow;

    // The HTTPS client for the XIV Shinies API. Constructed here but never called on its own —
    // nothing is uploaded until the user explicitly opts in. It owns an HttpClient, so it must be
    // disposed below.
    private readonly ApiClient apiClient;

    // Every registered fact source. Constructed once; nothing runs them yet. They hold no
    // unmanaged resources, so there is nothing to dispose.
    private readonly IReadOnlyList<ICollector> collectors;

    // Passively captures the knowledge level from the occult review window. Subscribes to
    // addon-lifecycle events and to login/logout, so it must be disposed.
    private readonly KnowledgeObserver knowledgeObserver;

    // Passively learns which beasts the character holds, by reading the bestiary window when the
    // player opens it. Subscribes to that window's addon events and to login/logout, so it must be
    // disposed.
    private readonly TamedBeastObserver tamedBeastObserver;

    // Listens for login/unlock/interval and drives the uploads. Subscribes to game events, so it
    // must be disposed — and disposed BEFORE the ApiClient it borrows.
    private readonly SyncManager syncManager;

    // Watches for the character entering an Occult Crescent instance and streams its CE/FATE
    // state to the live tracker. Subscribes to game events, so it must be disposed — before the
    // SyncManager and ApiClient it borrows.
    private readonly OccultManager occultManager;

    // Follows the character through the Crucible's boards and shares what its windows show. Subscribes
    // to game events and window events, so it must be disposed, before the SyncManager and ApiClient
    // it borrows.
    private readonly CrucibleManager crucibleManager;

    /// <summary>
    /// Constructor — Dalamud calls this once on load. Wire everything up here, and be sure to
    /// tear down in Dispose whatever you set up here (handlers, events, windows).
    /// </summary>
    public Plugin()
    {
        // Load previously-saved settings, or start fresh. `as Configuration` is a safe cast that
        // yields null on type mismatch; `?? new Configuration()` is the null-coalescing operator
        // (identical to JS `??`) supplying a default. Net effect: "use the saved config if there
        // is one, otherwise a new default config".
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // A config written by an older plugin version runs through its migrations before
        // anything reads it — this is what keeps a new default-on setting from silently opting
        // in a user whose wizard ran long ago (see PluginSettings.ApplyUpgradeMigrations).
        if (Configuration.Version < Configuration.CurrentVersion)
        {
            // The save happens whether or not any setting changed: persisting the version bump
            // is what records that migrations ran, so they can never run twice.
            Configuration.Settings.ApplyUpgradeMigrations(Configuration.Version);
            Configuration.Version = Configuration.CurrentVersion;
            Configuration.Save();
        }

        // The whole wiring sequence runs under one guard because a constructor that throws is
        // a plugin Dalamud never received: Dispose() will never be called, so any event handler
        // already subscribed would stay bound to a half-constructed plugin for the rest of the
        // game session, firing every frame. The catch tears down whatever was built — in the
        // same order Dispose() uses — and rethrows so the load still fails visibly.
        try
        {
            // Build the API client from the persisted settings. The manifest carries the version
            // the build stamped in, which becomes the User-Agent and the payload's pluginVersion
            // field.
            var version = PluginInterface.Manifest.AssemblyVersion?.ToString() ?? "0.0.0";
            apiClient = new ApiClient(Configuration.Settings, version);

            // The knowledge-level capture, built before the collectors because the phantom jobs
            // collector reads from it. It only records what the player puts on screen
            // themselves — see KnowledgeObserver's remarks for the passive-capture design.
            knowledgeObserver = new KnowledgeObserver(AddonLifecycle, ClientState, Log);

            // The bestiary capture, built before the collectors for the same reason: the tamed
            // beasts collector reads from it. The consent question is handed over as a callback
            // rather than the settings object, so the observer never has to know how consent is
            // decided — only whether it stands. It asks about its own collection, so no category is
            // named here.
            tamedBeastObserver = new TamedBeastObserver(
                ClientState, DataManager, AddonLifecycle,
                key => CollectorGate.IsCapturePermitted(key, Configuration.Settings),
                Log);

            // Build the fact sources. Nothing reads the game until something explicitly runs them.
            // The window and condition services are only asked questions during a pass; nothing
            // subscribes to them, so they add nothing to tear down.
            collectors = CollectorRegistry.Create(
                DataManager, UnlockState, Framework, knowledgeObserver, tamedBeastObserver,
                GameGui, Condition);

            // Establishes which collections count as already-seen, so the settings screen can badge
            // a genuinely new one. It runs here rather than with the migrations above because it
            // needs the registered collectors, which do not exist until this line — and it is
            // self-guarding, so running it on every load costs one flag read after the first.
            if (Configuration.Settings.InitializeSeenCategories(collectors.Select(c => c.CategoryKey)))
                Configuration.Save();

            // Honors the user's standing "turn on new collections automatically" answer. It runs
            // after the baseline above, because that is what decides which collections count as
            // never-shown — and before the window exists, so a collection is switched on before
            // anything can draw it and mark it shown.
            //
            // Only the collections a tick settles on its own — see
            // ManifestConsent.FixedScopeCategoryKeys for why one whose groups the user answers
            // separately must not be switched on for them.
            var autoEnabled = Configuration.Settings.AutoEnableUnseenCategories(
                ManifestConsent.FixedScopeCategoryKeys(collectors));
            if (autoEnabled.Count > 0)
            {
                Configuration.Save();

                // Logged because this is the one path that switches a collection on without a
                // click, so the reason it happened should be findable afterwards.
                Log.Information(
                    $"Switched on {string.Join(", ", autoEnabled)} — new since this install was " +
                    "last shown the list, and new collections are set to start on.");
            }

            // Start listening. The manager subscribes to login and unlock events immediately, but
            // every path out of them checks the upload gate first, so a user who has not opted in
            // sends nothing and the plugin never contacts the server.
            //
            // `Configuration.Save` is passed as a method-group callback (see
            // CollectorRegistry.Create for how method groups work): the manager gets "persist the
            // settings" as a plain Action, so it never needs a reference to the Dalamud config
            // shell itself.
            syncManager = new SyncManager(
                Framework, ClientState, PlayerState, UnlockState, Log,
                apiClient, Configuration.Settings, Configuration.Save, collectors, version);

            // A beast learned from the bestiary should upload promptly rather than waiting for the
            // next interval, the way an unlock does. The observer cannot be told about the manager
            // before the manager exists, so the two are joined here; the event carries the
            // observer's own category key, so neither side names the collection.
            tamedBeastObserver.BeastsLearned += syncManager.NotifyCategoryChanged;

            // The live occult tracker. Built after the SyncManager because it reads the identity
            // and server config that manager owns; gated by the same consent switches plus its own
            // "Share live Occult instance state" toggle, so it too is silent until the user opts in.
            occultManager = new OccultManager(
                Framework, ClientState, FateTable, PlayerState, Log,
                apiClient, Configuration.Settings, syncManager, version);

            // The Crucible run sharing. Built after the SyncManager because it reads the identity,
            // server config and halt that manager owns; gated by the same consent switches plus its
            // own toggle, which starts off, so it reads none of the player's play and sends nothing
            // until the user opts in. Before that it reads only static game data (see
            // CrucibleManager).
            crucibleManager = new CrucibleManager(
                Framework, ClientState, Condition, GameGui, AddonLifecycle, DataManager, Log,
                apiClient, Configuration.Settings, syncManager, version);

            // The mascot drawn in the settings header — the same hand-made image the installer
            // shows, shipped next to the DLL. GetFromFile returns a shared texture that loads
            // lazily and is owned by Dalamud, so there is nothing to dispose on our side; if the
            // file is missing the wrap comes back empty and the header simply draws without an
            // image.
            var mascotTexture = TextureProvider.GetFromFile(Path.Combine(
                PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "images", "icon.png"));

            // Create our window and hand it to the WindowSystem so it gets drawn each frame. It
            // reads the sync manager's status and the collectors' self-descriptions, so it is
            // built after both. The font pieces let it build a heading-sized font and draw
            // FontAwesome icons.
            mainWindow = new MainWindow(
                Configuration, apiClient, syncManager, collectors, mascotTexture,
                PluginInterface.UiBuilder.FontAtlas,
                PluginInterface.UiBuilder.IconFontHandle,
                PluginInterface.UiBuilder.DefaultFontSpec.SizePx,
                version);
            windowSystem.AddWindow(mainWindow);

            // Register the /shinies command. CommandInfo takes the handler method (OnCommand);
            // the object-initializer sets the help text shown in /xlhelp.
            CommandManager.AddHandler(PluginMeta.CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = $"Toggle the {PluginMeta.DisplayName} window.",
            });

            // Register a longer alias that runs the same handler. ShowInHelp = false keeps
            // /xlhelp to a single entry instead of listing the command twice.
            CommandManager.AddHandler(PluginMeta.CommandAlias, new CommandInfo(OnCommand)
            {
                ShowInHelp = false,
            });

            // Subscribe to UI events. `+=` adds a handler to a C# "event" (a built-in
            // publisher/subscriber list); there's no exact React analog, but it's like
            // addEventListener. Every `+=` here MUST be matched by a `-=` in Dispose, or we'd
            // leak the handler after the plugin unloads.
            // - Draw: fires every frame; we forward it to the WindowSystem to render our windows.
            // - OpenMainUi: the "open" button next to the plugin in the installer.
            // - OpenConfigUi: the "settings" gear next to the plugin in the installer.
            PluginInterface.UiBuilder.Draw += windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        }
        catch
        {
            // Unassigned readonly fields are still null here, so `?.` skips whatever never got
            // built. Unsubscribing an event that was never added does nothing, and removing a
            // command that was never added only logs that it was not found, which is what lets this
            // mirror Dispose() without tracking progress flags.
            PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
            CommandManager.RemoveHandler(PluginMeta.CommandName);
            CommandManager.RemoveHandler(PluginMeta.CommandAlias);

            windowSystem.RemoveAllWindows();
            mainWindow?.Dispose();

            // The one pairing `?.` cannot express: a null-conditional may not stand on the left of
            // `-=`, and the handler on the right would still be evaluated against a null manager.
            // Both halves are therefore checked by hand, in the position Dispose() unsubscribes at.
            if (tamedBeastObserver is not null && syncManager is not null)
                tamedBeastObserver.BeastsLearned -= syncManager.NotifyCategoryChanged;

            crucibleManager?.Dispose();
            occultManager?.Dispose();
            syncManager?.Dispose();
            knowledgeObserver?.Dispose();
            tamedBeastObserver?.Dispose();
            apiClient?.Dispose();
            throw;
        }

        Log.Information($"{PluginMeta.DisplayName} loaded.");
    }

    /// <summary>
    /// Cleanup on unload (Dalamud calls this). Mirror of the constructor: unsubscribe every
    /// event, remove every window and command handler. This is the plugin's "unmount" — the
    /// same discipline as returning a cleanup function from useEffect so nothing lingers.
    /// </summary>
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        // Before the window they dispatch into: a /shinies typed mid-teardown must not toggle
        // a window that is already gone.
        CommandManager.RemoveHandler(PluginMeta.CommandName);
        CommandManager.RemoveHandler(PluginMeta.CommandAlias);

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        // Before the SyncManager it notifies: once this is detached, nothing can schedule a
        // further upload through a manager that is about to go away.
        tamedBeastObserver.BeastsLearned -= syncManager.NotifyCategoryChanged;

        // Before the SyncManager and ApiClient they borrow, deliberately: each unsubscribes everything
        // it subscribed to and cancels any upload of its own in flight first.
        crucibleManager.Dispose();
        occultManager.Dispose();

        // Before the ApiClient, deliberately: this unsubscribes the game events and cancels any
        // upload in flight, so nothing is still reaching for the client when it goes away.
        syncManager.Dispose();

        // After the SyncManager whose collection passes read them: no pass can start once the
        // manager's handlers are detached.
        knowledgeObserver.Dispose();
        tamedBeastObserver.Dispose();

        // Releases the underlying HttpClient and its connection pool.
        apiClient.Dispose();
    }

    // The command handler. Its signature (string command, string args) is what CommandInfo
    // expects: `command` is what was typed (/shinies), `args` is anything after it. Bare
    // /shinies toggles the window open/closed.
    private void OnCommand(string command, string args)
    {
#if DEBUG
        // A development build recognizes a small set of arguments, none of which exists in a
        // Release compile — the shipped plugin recognizes no arguments at all.
        //
        //   /shinies seedlog              — seed the log
        //   /shinies seedlog <version>    — seed it and draw that version in the masthead
        //                                   (see MainWindow.OverrideVersionForScreenshots)
        //   /shinies dumpslots            — audit the unlock bitmask's coverage of the Mount
        //                                   sheet; the answer goes to /xllog, not the screen
        var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words[0].Equals("seedlog", StringComparison.OrdinalIgnoreCase))
        {
            syncManager.SeedUploadHistoryForScreenshots(collectors, DateTimeOffset.Now);

            if (words.Length > 1)
                mainWindow.OverrideVersionForScreenshots(words[1]);

            // Says what undoes it, because both are silent: a real sync lands a genuine row at the
            // front whose counts diff against the fabricated ones and light up "(changed)" across
            // the board, and a logout or character switch clears the log outright.
            Log.Information(
                "Upload log seeded for screenshots. Capture before the next sync; logging out clears it.");

            // Opened rather than toggled: the point of the command is to look at the log, and a
            // toggle would close the window when it is already open.
            mainWindow.IsOpen = true;
            return;
        }

        if (words.Length > 0 && words[0].Equals("dumpslots", StringComparison.OrdinalIgnoreCase))
        {
            // Marshaled rather than called straight. Dalamud does not schedule command handlers —
            // it invokes them where the command arrived, which is the game's main thread for chat
            // and the console's draw for the console — so the read is on the right thread by
            // circumstance, not by contract, and a bad read of game memory raises a
            // corrupted-state exception no catch can rescue. When already on that thread,
            // RunOnFrameworkThread runs the call in place.
            _ = Framework.RunOnFrameworkThread(
                () => UnlockSlotAudit.Run(ClientState, DataManager, UnlockState, Log));
            return;
        }
#endif

        mainWindow.Toggle();
    }

    // Small helper wired to the installer's open/config buttons above. `Toggle()` comes from the
    // Window base class (show if hidden, hide if shown).
    private void ToggleMainUi() => mainWindow.Toggle();
}
