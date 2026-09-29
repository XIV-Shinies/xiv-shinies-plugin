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

    // The figures of the last read written to the log, so a redraw that reads the same figures is
    // not written again; null until a read is logged, and again whenever the window opens or the
    // ledger is cleared.
    private BestiaryReadSummary? lastLoggedRead;

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

        // PostSetup fires once when the window opens, before its list has loaded; PostRefresh fires
        // as the window is redrawn while it stays open, which is how the loaded list and each turned
        // page reach this. Both route to the same handler.
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

    private void OnLogin() => ForgetCaptured();

    private void OnLogout(int type, int code) => ForgetCaptured();

    /// <summary>Clears what has been captured, and the note of what was last logged with it.</summary>
    /// <remarks>
    /// The two go together: after a clear the next read starts the ledger afresh, so it is written to
    /// the log even when its figures match a read from before.
    /// </remarks>
    private void ForgetCaptured()
    {
        ledger.Clear();
        lastLoggedRead = null;
    }

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
                ForgetCaptured();
                return;
            }

            // The window opening arrives before its list has loaded, so it is noted here rather
            // than logged: clearing the last logged read means the first read with records after an
            // opening is always written, even when its figures match the read before the window
            // closed. That is what shows a player's reopening in the log.
            if (type == AddonEvent.PostSetup)
                lastLoggedRead = null;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
                return;

            // The bestiary's real size, from the game's data rather than from the window — the one
            // figure the completeness test does not take from the window itself. See
            // TamedBeastLedger.IsComplete for what it agrees with.
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

            // The ledger's four figures plus the sheet's size, which the ledger never hands back.
            // Together they are everything the completeness test weighs, so a withheld claim says
            // which agreement failed rather than only that one did. The page's own tally sits
            // beside the ledger's remembered one because the two part company as soon as the player
            // filters the list.
            var summary = new BestiaryReadSummary(
                page.Seen.Count, page.Captured.Count, page.CapturedTotal, page.BeastTotal,
                ledger.Report(), size);

            // Written only when a figure has changed since the last read logged: the window redraws
            // many times a second while it stays open, and a message is built even when the log
            // level would discard it.
            //
            // `lastLoggedRead` is a nullable struct (`BestiaryReadSummary?`), and a real value never
            // equals null, so the first read after an opening or a clear is written too.
            if (summary != lastLoggedRead)
            {
                lastLoggedRead = summary;

                // Built from the summary alone, so the line changes exactly when the summary does.
                var tally = summary.Ledger;
                log.Debug(
                    $"Bestiary page read: {summary.Listed} listed, {summary.Held} held, " +
                    $"page tally {summary.PageCapturedTotal?.ToString() ?? "?"}/" +
                    $"{summary.PageBeastTotal?.ToString() ?? "?"}; " +
                    $"ledger {tally.Held} held, {tally.Seen} seen, tally " +
                    $"{tally.CapturedTotal?.ToString() ?? "?"}/{tally.BeastTotal?.ToString() ?? "?"}; " +
                    $"sheet {summary.SheetSize?.ToString() ?? "?"}; complete {tally.IsComplete}.");
            }
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
                "The bestiary sheet could not be read, so nothing can check the bestiary window's " +
                "own total against the game's data and this collection will not claim to have " +
                "read it all.");
        }

        return beastCount;
    }
}
