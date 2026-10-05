using System;
// Addon lifecycle events: how a plugin is told a game window opened, refreshed or closed.
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
// The game's condition flags, of which only "in combat" is read here.
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
// AtkUnitBase, the game's base window type, whose values back what the window displays.
using FFXIVClientStructs.FFXIV.Component.GUI;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Listens for the Crucible's windows being drawn and closed for the player, reads each one drawn, and
/// reports the readings and the closes.
/// </summary>
/// <remarks>
/// <para>
/// Purely passive. A window is read only when the game draws it for the player, or when the caller asks
/// for one the player already has open; nothing here opens, navigates or clicks one. Nothing is read
/// while the sharing is off, and nothing during a fight. A close is reported without reading the
/// window: its closing snapshot is made from the last reading taken (see <see cref="CrucibleFeed"/>).
/// Of each window, only what its reader names is ever looked at; the board window's enemy rows are
/// skipped by their type.
/// </para>
/// <para>
/// Every call arrives on the game's main thread.
/// </para>
/// </remarks>
// `internal` makes it visible only inside this plugin, and `sealed` means nothing can subclass it.
// `IDisposable` is the interface for an object with a `Dispose` method that releases what it holds,
// like the cleanup function a React effect returns. `unsafe` because a window's values are reached
// through a raw pointer into the game's own memory, where C#'s references and bounds checks do not
// apply, so every access is guarded by hand.
internal sealed unsafe class CrucibleObserver : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;

    // `Func<bool>` is a function value that takes nothing and returns a bool, like `() => boolean`;
    // `Action<A, B>` is one that takes two values and returns nothing, like `(a: A, b: B) => void`.
    private readonly Func<bool> isSharing;
    private readonly Action<string, CrucibleSnapshot> onRead;
    private readonly Action<string> onClosed;
    private readonly IPluginLog log;

    /// <summary>Which read failures have already been reported in full.</summary>
    /// <remarks>
    /// The windows redraw while they stay open, so a fault that repeats would otherwise be reported on
    /// every redraw: full detail once, a line thereafter.
    /// </remarks>
    private readonly RepeatedFailures readFailures = new();

    /// <summary>Wires the listeners. Reads nothing until the sharing is on and a window is drawn.</summary>
    /// <param name="isSharing">Answers whether the sharing may read right now.</param>
    /// <param name="onRead">Takes each reading, with the name of the window it came from.</param>
    /// <param name="onClosed">Takes the name of each window that closed.</param>
    // The other parameters are the Dalamud services this reads through, and the log.
    public CrucibleObserver(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        ICondition condition,
        Func<bool> isSharing,
        Action<string, CrucibleSnapshot> onRead,
        Action<string> onClosed,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.condition = condition;
        this.isSharing = isSharing;
        this.onRead = onRead;
        this.onClosed = onClosed;
        this.log = log;

        // A window's values arrive when it is redrawn, apart from the results screen's, which are
        // complete when it opens. A window closes through Close or, when it is torn down, Finalize; a
        // window only hidden is still open, so hiding is not listened for. Every registration has a
        // matching one in Dispose.
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

            // `GetAddonByName` hands back a wrapper around the window's address, null when the window
            // does not exist.
            var window = gameGui.GetAddonByName(windowName);
            if (window.IsNull || !window.IsVisible)
                return;

            // `(AtkUnitBase*)` reads the address as a pointer to the game's window type.
            Take(windowName, (AtkUnitBase*)window.Address);
        }
        catch (Exception ex)
        {
            Report(ex, windowName);
        }
    }

    /// <summary>True while the character is in combat, when nothing is read.</summary>
    private bool InCombat => condition[ConditionFlag.InCombat];

    // `AddonEvent type` is unused, but the lifecycle service calls every handler with it.
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
        try
        {
            if (isSharing())
                onClosed(args.AddonName);
        }
        catch (Exception ex)
        {
            Report(ex, args.AddonName);
        }
    }

    /// <summary>
    /// Reads a window's values through its reader and reports the reading; a window holding no values,
    /// or values that are not a layout its reader knows, reports nothing.
    /// </summary>
    private void Take(string windowName, AtkUnitBase* window)
    {
        // `->` reaches a member through a pointer, as `.` does through a reference.
        if (window == null || window->AtkValues == null || window->AtkValuesCount == 0)
            return;

        var values = new AtkValueList(window->AtkValues, window->AtkValuesCount);
        // `x is { } snapshot` matches when x is not null and names it `snapshot`.
        if (CrucibleWindows.Read(windowName, values) is { } snapshot)
            onRead(windowName, snapshot);
    }

    /// <summary>Logs a read failure: in full the first time, a line when it repeats.</summary>
    private void Report(Exception ex, string windowName)
    {
        // `$"..."` is an interpolated string, like a template literal: `{windowName}` is filled in.
        if (readFailures.IsFirstSighting(ex))
            log.Error(ex, $"Could not read the Crucible window {windowName}.");
        else
            log.Debug(ex, $"Could not read the Crucible window {windowName} again.");
    }
}
