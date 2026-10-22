using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Reads the Crucible's windows as the game draws and closes them for the player, and reports each
/// reading and close. Every call arrives on the game's main thread.
/// </summary>
/// <remarks>
/// Purely passive: it opens, navigates and clicks nothing, and reads a window only when the game draws
/// it, when it closes, or when the caller asks for one already open, never while the sharing is off or
/// during a fight. Only what a window's reader names is looked at, and the board's enemy rows are
/// skipped by their type. A window's values are still whole as it closes, so it is read once more and
/// that reading goes with the close (see <see cref="CrucibleFeed.Close"/>).
/// </remarks>
// Window values are raw pointers into game memory, so every access is guarded by hand.
internal sealed unsafe class CrucibleObserver : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly Func<bool> isSharing;
    private readonly Action<string, CrucibleSnapshot> onRead;
    private readonly Action<string, CrucibleSnapshot?> onClosed;
    private readonly CrucibleNames names;
    private readonly IPluginLog log;

    /// <summary>
    /// Which read failures have been logged in full. Open windows keep redrawing, so a repeating fault
    /// would otherwise be logged on every redraw.
    /// </summary>
    private readonly RepeatedFailures readFailures = new();

    /// <summary>Wires the listeners, and reads nothing yet.</summary>
    /// <param name="isSharing">Answers whether the sharing may read right now.</param>
    /// <param name="onRead">Takes each reading, with the name of the window it came from.</param>
    /// <param name="onClosed">
    /// Takes the name of each window that closed, with the window as read at the close, or null when
    /// it could not be read then.
    /// </param>
    /// <param name="names">Resolves the results screen's drawn names to ids.</param>
    public CrucibleObserver(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        ICondition condition,
        Func<bool> isSharing,
        Action<string, CrucibleSnapshot> onRead,
        Action<string, CrucibleSnapshot?> onClosed,
        CrucibleNames names,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.condition = condition;
        this.isSharing = isSharing;
        this.onRead = onRead;
        this.onClosed = onClosed;
        this.names = names;
        this.log = log;

        // Values arrive on redraw, except the results screen's, complete at open. A window closes
        // through Close, or Finalize when torn down; hiding leaves it open and is not listened for.
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, CrucibleWindows.Redrawn, OnRedrawn);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, CrucibleWindows.CompleteAtOpen, OnRedrawn);
        addonLifecycle.RegisterListener(
            AddonEvent.PostRequestedUpdate, CrucibleWindows.CompleteAtOpen, OnRedrawn);
        addonLifecycle.RegisterListener(AddonEvent.PreClose, CrucibleWindows.Closing, OnClosing);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, CrucibleWindows.Closing, OnClosing);
    }

    /// <summary>Unregisters everything the constructor wired.</summary>
    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, CrucibleWindows.Redrawn, OnRedrawn);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, CrucibleWindows.CompleteAtOpen, OnRedrawn);
        addonLifecycle.UnregisterListener(
            AddonEvent.PostRequestedUpdate, CrucibleWindows.CompleteAtOpen, OnRedrawn);
        addonLifecycle.UnregisterListener(AddonEvent.PreClose, CrucibleWindows.Closing, OnClosing);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, CrucibleWindows.Closing, OnClosing);
    }

    /// <summary>
    /// Reads a window the player already has open, as when the character enters a board with the HUD
    /// up, or a fight ends with its loot window already showing.
    /// </summary>
    /// <param name="windowName">The window's internal name.</param>
    public void ReadIfOpen(string windowName)
    {
        try
        {
            if (!isSharing() || InCombat)
                return;

            var window = gameGui.GetAddonByName(windowName);
            if (window.IsNull || !window.IsVisible)
                return;

            Take(windowName, (AtkUnitBase*)window.Address);
        }
        catch (Exception ex)
        {
            Report(ex, windowName);
        }
    }

    /// <summary>True while the character is in combat, when nothing is read.</summary>
    private bool InCombat => condition[ConditionFlag.InCombat];

    private void OnRedrawn(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!isSharing() || InCombat)
                return;

            Take(args.AddonName, (AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            // A handler that throws would escape into Dalamud's UI dispatch, so no failure leaves.
            Report(ex, args.AddonName);
        }
    }

    private void OnClosing(AddonEvent type, AddonArgs args)
    {
        // A gate that cannot be asked counts as closed, so nothing is read or sent.
        bool sharing;
        try
        {
            sharing = isSharing();
        }
        catch (Exception ex)
        {
            Report(ex, args.AddonName);
            return;
        }

        if (!sharing)
            return;

        // A failed read, or a close during a fight, leaves this null; the close still goes in.
        CrucibleSnapshot? atClose = null;
        try
        {
            if (!InCombat)
                atClose = ReadSnapshot(args.AddonName, (AtkUnitBase*)args.Addon.Address);

            // A window can close through both events and is read at each; only the first reading can go
            // up (see CrucibleFeed.Close).
            log.Debug($"Crucible window {args.AddonName} closing ({type}).");
        }
        catch (Exception ex)
        {
            Report(ex, args.AddonName);
        }

        try
        {
            onClosed(args.AddonName, atClose);
        }
        catch (Exception ex)
        {
            Report(ex, args.AddonName);
        }
    }

    /// <summary>
    /// Reads a window and reports the reading, if there is one (see <see cref="ReadSnapshot"/>).
    /// </summary>
    private void Take(string windowName, AtkUnitBase* window)
    {
        if (ReadSnapshot(windowName, window) is { } snapshot)
            onRead(windowName, snapshot);
    }

    /// <summary>
    /// Reads a window's values through its reader; a window holding no values, or values that are not a
    /// layout its reader knows, gives null.
    /// </summary>
    private CrucibleSnapshot? ReadSnapshot(string windowName, AtkUnitBase* window)
    {
        if (window == null || window->AtkValues == null || window->AtkValuesCount == 0)
            return null;

        var values = new AtkValueList(window->AtkValues, window->AtkValuesCount);
        return CrucibleWindows.Read(windowName, values, names);
    }

    /// <summary>Logs a read failure: in full the first time, a line when it repeats.</summary>
    private void Report(Exception ex, string windowName)
    {
        if (readFailures.IsFirstSighting(ex))
            log.Error(ex, $"Could not read the Crucible window {windowName}.");
        else
            log.Debug(ex, $"Could not read the Crucible window {windowName} again.");
    }
}
