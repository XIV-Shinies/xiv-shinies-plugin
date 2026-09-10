using System;
using System.Collections.Generic;
// Addon lifecycle events — how a plugin is told a game window opened or refreshed.
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
// AtkUnitBase, the game's base window type, whose values back what the window displays.
using FFXIVClientStructs.FFXIV.Component.GUI;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// Learns which beasts the character holds by reading the Master's Bestiary when the player opens
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The window is handed the state of the beasts it displays, so reading it is reading the
/// character's own bestiary — every beast held, not merely the ones taken while something was
/// watching. It shows part of itself at a time, so a full picture is built from however many pages
/// the player looks at. See <see cref="BestiaryPage"/> for what a page holds and
/// <see cref="TamedBeastLedger"/> for how pages add up to a complete answer.
/// </para>
/// <para>
/// Purely passive. The window is read only when the player opens it themselves; nothing here opens
/// it, navigates it, or makes the client talk to the game's servers, and no window but this one is
/// watched. Nothing is captured at all while the collection is switched off, what is captured is
/// cleared at both edges of a login session, and the gate deciding whether any of it leaves the
/// process sits downstream of all that.
/// </para>
/// </remarks>
// `unsafe` because the bestiary window's backing values are reached through a raw pointer into the
// game's own memory — C#'s references and bounds checks do not apply, so the reader guards every
// access by hand.
public sealed unsafe class TamedBeastObserver : IDisposable
{
    /// <summary>The Master's Bestiary window's internal addon name.</summary>
    private const string BestiaryAddonName = "XBMMonsterNotebook";

    private readonly TamedBeastLedger ledger = new();

    /// <summary>Which read failures have already been reported in full.</summary>
    /// <remarks>
    /// The window refreshes while it stays open, so a layout this cannot parse would otherwise
    /// report on every refresh — full detail once, a line thereafter.
    /// </remarks>
    private readonly RepeatedFailures readFailures = new();

    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly Func<string, bool> isCategoryEnabled;
    private readonly IPluginLog log;

    // The bestiary's size, read from the game's data the first time the window is opened rather
    // than in the constructor: a character who never plays beastmaster never pays for it.
    private int? beastCount;
    private bool beastCountReadFailed;

    /// <summary>Wires the listener. Records nothing until the player opens their bestiary.</summary>
    /// <param name="isCategoryEnabled">
    /// Answers whether the user has this collection switched on, given its category key. Passed in
    /// rather than reached for, so that this class holds no opinion about where consent lives and
    /// names no collection but its own.
    /// </param>
    public TamedBeastObserver(
        IClientState clientState,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        Func<string, bool> isCategoryEnabled,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.addonLifecycle = addonLifecycle;
        this.isCategoryEnabled = isCategoryEnabled;
        this.log = log;

        // PostSetup fires once when the window opens with its data in place; PostRefresh covers it
        // being redrawn while it stays open, which is how turning a page reaches this. Both routes
        // read the same values.
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, BestiaryAddonName, OnBestiaryAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, BestiaryAddonName, OnBestiaryAddon);

        // Both session edges, for the reason KnowledgeObserver documents: a session that ended
        // without its logout event firing would otherwise leave one character's beasts in place for
        // the next character to upload as their own.
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
    }

    /// <summary>
    /// Raised when a read finds a beast this session did not already know about, carrying the
    /// category key whose upload it should prompt.
    /// </summary>
    /// <remarks>
    /// The key travels with the event so that whatever schedules the upload never has to name this
    /// collection — the same self-description that keeps category names out of the unlock router.
    /// </remarks>
    public event Action<string>? BeastsLearned;

    /// <summary>The bestiary numbers the character is known to hold, ascending.</summary>
    public IReadOnlyList<int> Snapshot() => ledger.Snapshot();

    /// <summary>
    /// Whether the whole bestiary has been accounted for, so an absent number means the character
    /// does not hold that beast.
    /// </summary>
    public bool IsComplete => ledger.IsComplete;

    /// <summary>How much of the bestiary has been seen, for the settings panel to describe.</summary>
    public (int Seen, int? Total) Progress => (ledger.SeenCount, ledger.BeastTotal);

    /// <summary>Unregisters everything the constructor wired.</summary>
    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, BestiaryAddonName, OnBestiaryAddon);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, BestiaryAddonName, OnBestiaryAddon);
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
    }

    private void OnLogin() => ledger.Clear();

    private void OnLogout(int type, int code) => ledger.Clear();

    /// <summary>Reads whatever page of the bestiary the player has on screen.</summary>
    /// <remarks>
    /// Every dereference is guarded by hand: the values array is reached through a raw pointer, and
    /// following a null one would be an access violation that no <c>catch</c> can rescue — it would
    /// take the game down rather than land in the handler below.
    /// </remarks>
    private void OnBestiaryAddon(AddonEvent type, AddonArgs args)
    {
        var isNews = false;

        try
        {
            // Nothing is captured while the collection is switched off. The bestiary is readable
            // again the moment it is switched back on and the window reopened, so refusing here
            // costs the player nothing and keeps the switch meaning what it says.
            if (!isCategoryEnabled(CategoryKeys.TamedBeasts))
            {
                ledger.Clear();
                return;
            }

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
                return;

            // The bestiary's real size, from the game's data rather than from the window. Without
            // it a filtered window could supply both halves of its own completeness check.
            var size = BeastCount();
            if (size is > 0)
                ledger.NoteDomainSize(size.Value);

            var values = new List<AddonValue>(addon->AtkValuesCount);
            for (var i = 0; i < addon->AtkValuesCount; i++)
                values.Add(Narrow(&addon->AtkValues[i]));

            var page = BestiaryPage.Read(values);

            // A read that found no records is not worth recording: the window was open but its
            // list had not landed yet, and the tally alone would move the completeness test
            // towards an answer the records have not earned.
            if (page.Seen.Count == 0)
                return;

            isNews = ledger.RecordPage(page);

            log.Debug(
                $"Bestiary page read: {page.Seen.Count} listed, {page.Captured.Count} held; " +
                $"{ledger.SeenCount} of {ledger.BeastTotal?.ToString() ?? "?"} seen so far.");
        }
        catch (Exception ex)
        {
            // An addon callback that throws would escape into Dalamud's UI dispatch, so no read
            // failure is allowed out of here.
            if (readFailures.IsFirstSighting(ex))
                log.Error(ex, "Could not read the Master's Bestiary.");
            else
                log.Debug(ex, "Could not read the Master's Bestiary again.");
        }

        // Outside the catch, so that a fault raised downstream by whatever schedules the upload is
        // reported as itself rather than as a failure to read the bestiary.
        //
        // Only news is worth an upload: turning pages over a bestiary already read would otherwise
        // schedule a sync per page with nothing to add.
        if (isNews)
            BeastsLearned?.Invoke(CategoryKeys.TamedBeasts);
    }

    /// <summary>
    /// Reduces one of the window's values to the three shapes <see cref="BestiaryPage"/> reads.
    /// </summary>
    /// <remarks>
    /// Anything else becomes an unreadable slot rather than a guess. That is what lets the reader
    /// recognize where the run of per-beast records stops, since the values after it are of kinds
    /// a record never holds.
    /// </remarks>
    private static AddonValue Narrow(AtkValue* value)
    {
        switch (value->Type)
        {
            case AtkValueType.Int:
                // Negative values are not bestiary numbers or tallies, and would wrap if widened.
                return value->Int >= 0 ? AddonValue.FromInteger((uint)value->Int) : AddonValue.Unreadable;

            case AtkValueType.UInt:
                return AddonValue.FromInteger(value->UInt);

            case AtkValueType.Bool:
                return AddonValue.FromBoolean(value->Byte != 0);

            case AtkValueType.String:
            case AtkValueType.ConstString:
            case AtkValueType.ManagedString:
                return value->String.Value == null
                    ? AddonValue.Unreadable
                    : AddonValue.FromText(value->String.ToString());

            default:
                return AddonValue.Unreadable;
        }
    }

    /// <summary>The bestiary's size, read once and kept, or null when the sheet cannot be read.</summary>
    private int? BeastCount()
    {
        if (beastCount is not null)
            return beastCount;

        beastCount = BestiaryReader.CountBeasts(dataManager);

        // Reported once, then retried in silence. The sheet is static game data, so a failure is
        // very likely permanent and repeating the warning per refresh would bury the log — but the
        // retry itself is one lookup and worth keeping, because giving up for the session would
        // withhold the completeness claim on the strength of a single bad read.
        if (beastCount is null && !beastCountReadFailed)
        {
            beastCountReadFailed = true;
            log.Warning(
                "The bestiary sheet could not be read, so this collection cannot tell a complete " +
                "bestiary from a filtered one and will not claim to have read it all.");
        }

        return beastCount;
    }
}
