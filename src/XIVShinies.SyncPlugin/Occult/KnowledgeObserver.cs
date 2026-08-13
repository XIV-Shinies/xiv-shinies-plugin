using System;
// Addon lifecycle events — how a plugin is told a game window opened or refreshed.
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
// AtkUnitBase, the game's base window type, whose AtkValues back what the window displays.
using FFXIVClientStructs.FFXIV.Component.GUI;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Passively captures the character's TRUE knowledge level whenever the player opens the
/// Occult Crescent review window ("Review your knowledge level and currencies", offered by
/// the surveyor in Phantom Village).
/// </summary>
/// <remarks>
/// <para>
/// The true level is displayed in exactly two places: this window and the Lodestone — the
/// in-instance HUD shows only the zone-synced level, and the only knowledge value any mapped
/// game struct carries is that zone-synced one. The window's backing data
/// (<c>MKDContentsInfo</c>'s first Atk value) is the one
/// client-side source, so this class listens for the window opening and reads that single
/// value. Purely passive: the plugin never opens the window or talks to any NPC — the value
/// is captured only when the player chooses to look at it themselves.
/// </para>
/// <para>
/// The sighting is cached in memory with its observation time and cleared at both session
/// edges (login and logout) — it belongs to the character who opened the window.
/// Deliberately not persisted: knowledge is a live stat whose value decays in usefulness
/// (see <see cref="KnowledgeObservation"/>). A reload just waits for the next review.
/// </para>
/// <para>
/// Addon callbacks arrive on the framework thread, and <see cref="Current"/> is read there
/// too (by the progression collector during a collection pass), so no lock is needed; the
/// reference write is atomic regardless.
/// </para>
/// </remarks>
// `unsafe` because the window's backing values are reached through a raw pointer into the
// game's own memory — C#'s references and bounds checks do not apply, so the callback guards
// every access by hand.
public sealed unsafe class KnowledgeObserver : IDisposable
{
    /// <summary>The review window's internal addon name.</summary>
    private const string AddonName = "MKDContentsInfo";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly TimeProvider timeProvider;

    private KnowledgeObservation? current;

    /// <summary>Wires the listener. Captures nothing until the player opens the window.</summary>
    public KnowledgeObserver(
        IAddonLifecycle addonLifecycle,
        IClientState clientState,
        IPluginLog log,
        TimeProvider? timeProvider = null)
    {
        this.addonLifecycle = addonLifecycle;
        this.clientState = clientState;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // PostSetup fires once when the window opens with its data in place; PostRefresh covers
        // the window updating while it stays open. Both routes read the same single value.
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddon);

        // Logout alone would leave a stale value if a session ever ended without the event
        // firing (an abrupt disconnect) and the next login were a different character, so both
        // session edges clear the sighting.
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
    }

    /// <summary>
    /// The latest sighting for the logged-in character, or null when the window has not been
    /// opened since login.
    /// </summary>
    public KnowledgeObservation? Current => current;

    /// <summary>
    /// A fence that moves at every session edge (login and logout). State another class scopes
    /// to the same login session as the sighting stores this value alongside; a stored copy that
    /// no longer matches was recorded in a session that has ended. Comparing generations answers
    /// that without the other class wiring client-state events of its own.
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

    // Both session edges clear the sighting and move the session fence; the constructor
    // explains why both edges are wired.
    private void OnLogin()
    {
        current = null;
        sessionGeneration++;
    }

    private void OnLogout(int type, int code)
    {
        current = null;
        sessionGeneration++;
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        try
        {
            // The args carry a safe pointer wrapper; its Address is the raw AtkUnitBase*. The
            // array pointer is checked alongside the count: dereferencing a null array would be
            // an access violation, which no managed catch can stop — it would take the game
            // process down rather than land in the handler below.
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 1)
                return;

            // The knowledge level is the window's first backing value. It arrives as an Int (or
            // UInt, depending on how the game staged it); anything else means the window's data
            // has not landed yet — capture nothing rather than a wrong value.
            var value = addon->AtkValues[0];
            var level = value.Type switch
            {
                AtkValueType.Int => (long)value.Int,
                AtkValueType.UInt => value.UInt,
                _ => -1L,
            };

            // The wire schema bounds the level at 0–255; a value outside that is a misread, not
            // a fact about the character.
            if (level is < 0 or > 255)
                return;

            current = new KnowledgeObservation
            {
                Level = (byte)level,
                ObservedAt = timeProvider.GetUtcNow(),
            };
            log.Debug($"Knowledge level {level} observed from the review window.");
        }
        catch (Exception ex)
        {
            // An addon callback that throws would escape into Dalamud's UI dispatch.
            log.Error(ex, "Could not read the knowledge review window.");
        }
    }
}
