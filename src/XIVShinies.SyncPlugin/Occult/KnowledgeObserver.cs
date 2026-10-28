using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Captures the character's true knowledge level when the player opens the Occult Crescent review
/// window ("Review your knowledge level and currencies", from the surveyor in Phantom Village).
/// </summary>
/// <remarks>
/// Only this window and the Lodestone show the true level; the HUD and every mapped game struct carry
/// the zone-synced one, so the window's first Atk value is the one client-side source. Purely passive:
/// the plugin never opens the window or talks to an NPC. The sighting belongs to the character who
/// opened the window, so login clears it, logout keeps it for the sync manager's logout send, and it
/// is never persisted, because knowledge is a live stat (see <see cref="KnowledgeObservation"/>); a
/// reload waits for the next review.
/// </remarks>
public sealed unsafe class KnowledgeObserver : IDisposable
{
    private const string AddonName = "MKDContentsInfo";

    /// <summary>
    /// Read failures already reported in full. The window refreshes while open, so an unparseable
    /// layout would otherwise report on every refresh.
    /// </summary>
    private readonly RepeatedFailures readFailures = new();

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState clientState;
    private readonly Func<string, bool> isCategoryEnabled;
    private readonly IPluginLog log;
    private readonly TimeProvider timeProvider;

    private KnowledgeObservation? current;

    /// <summary>Wires the listener. Captures nothing until the player opens the window.</summary>
    /// <param name="isCategoryEnabled">
    /// Whether the user has the given category switched on. Passed in so this class holds no opinion
    /// on where consent lives.
    /// </param>
    public KnowledgeObserver(
        IAddonLifecycle addonLifecycle,
        IClientState clientState,
        Func<string, bool> isCategoryEnabled,
        IPluginLog log,
        TimeProvider? timeProvider = null)
    {
        this.addonLifecycle = addonLifecycle;
        this.clientState = clientState;
        this.isCategoryEnabled = isCategoryEnabled;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // PostSetup fires when the window opens with its data in place; PostRefresh when it updates
        // while open.
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddon);

        // Both edges move SessionGeneration; only login clears the sighting (see the class remarks).
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
    }

    /// <summary>
    /// Raised when a sighting is captured, with the category key whose facts it holds for the next
    /// upload.
    /// </summary>
    /// <remarks>The key travels with the event, so the scheduler never names this collection.</remarks>
    public event Action<string>? SightingTaken;

    /// <summary>
    /// The latest sighting since the last login, or null when none is held.
    /// </summary>
    /// <remarks>
    /// The progression collector reads it during a collection pass on the framework thread, where the
    /// addon callbacks write it, so it needs no lock.
    /// </remarks>
    public KnowledgeObservation? Current => current;

    /// <summary>
    /// Moves at every login and logout. A class that stores this beside session-scoped state knows the
    /// state is stale once the values differ, without wiring client-state events of its own.
    /// </summary>
    public int SessionGeneration => sessionGeneration;

    private int sessionGeneration;

    /// <summary>Unregisters everything the constructor wired.</summary>
    public void Dispose()
    {
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddon);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddon);
    }

    private void OnLogin()
    {
        current = null;
        sessionGeneration++;
    }

    private void OnLogout(int type, int code) => sessionGeneration++;

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        var taken = false;

        try
        {
            // Switched off: nothing is read or queued, and a sighting held from before is dropped.
            if (!isCategoryEnabled(CategoryKeys.OccultProgression))
            {
                current = null;
                return;
            }

            // Check the array pointer, not just the count: a null dereference is an access
            // violation, which no catch can stop, and it would crash the game.
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 1)
                return;

            // The level is the first value, staged as Int or UInt. Any other type means the data
            // has not landed yet, so nothing is captured.
            var value = addon->AtkValues[0];
            var level = value.Type switch
            {
                AtkValueType.Int => (long)value.Int,
                AtkValueType.UInt => value.UInt,
                _ => -1L,
            };

            // The wire schema bounds the level to 0 through 255; anything outside is a misread.
            if (level is < 0 or > 255)
                return;

            current = new KnowledgeObservation
            {
                Level = (byte)level,
                ObservedAt = timeProvider.GetUtcNow(),
            };
            log.Debug($"Knowledge level {level} observed from the review window.");
            taken = true;
        }
        catch (Exception ex)
        {
            // A throw would escape into Dalamud's UI dispatch. Only the first sighting gets the full
            // report (see readFailures).
            if (readFailures.IsFirstSighting(ex))
                log.Error(ex, "Could not read the knowledge review window.");
            else
                log.Debug(ex, "Could not read the knowledge review window again.");
        }

        // Outside the catch, so a fault in upload scheduling reports as itself.
        if (taken)
            SightingTaken?.Invoke(CategoryKeys.OccultProgression);
    }
}
