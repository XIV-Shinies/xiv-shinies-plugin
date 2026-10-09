using System;
using System.Collections.Generic;
// Addon lifecycle events — how a plugin is told a game window opened or refreshed.
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
// EventFramework, the game's registry of running NPC conversations and other scripted events.
using FFXIVClientStructs.FFXIV.Client.Game.Event;
// AtkUnitBase, the game's base window type, whose values back what the window displays.
using FFXIVClientStructs.FFXIV.Component.GUI;
// `using X = ...` gives a type a local name, like `import { Framework as GameFramework }`: the game's
// own Framework, which reports the running game's version, would otherwise clash with Dalamud's
// IFramework in reading.
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// Learns which beasts the character holds, and each one's rank, from the Master's Bestiary when
/// the player opens it and from the record set the Crucible's NPC loads.
/// </summary>
/// <remarks>
/// <para>
/// The window is handed the state of the beasts it displays, so reading it is reading the
/// character's own bestiary — every beast held, not merely the ones taken while something was
/// watching. It shows part of itself at a time, so a full picture is built from however many pages
/// the player looks at. See <see cref="BestiaryPage"/> for what a page holds and
/// <see cref="TamedBeastLedger"/> for how pages add up to a complete answer. Its beast list carries
/// no ranks and its detail panel shows only the selected beast's, so
/// <see cref="OnFrameworkUpdate"/> reads every rank from the NPC's record set.
/// </para>
/// <para>
/// Purely passive. The window is read only when the player opens it themselves, and the record set
/// holds nothing until the player has talked to the NPC; nothing here opens a window, starts a
/// conversation, or makes the client talk to the game's servers, and no window but this one is
/// watched. Nothing is captured at all while the collection is switched off, what is captured is
/// cleared at both edges of a login session, and the gate deciding whether any of it leaves the
/// process sits downstream of all that.
/// </para>
/// </remarks>
// `unsafe` because the bestiary window's values and the NPC's event handler are reached through raw
// pointers into the game's own memory — C#'s references and bounds checks do not apply, so the
// reader guards every access by hand.
public sealed unsafe class TamedBeastObserver : IDisposable
{
    /// <summary>The Master's Bestiary window's internal addon name.</summary>
    private const string BestiaryAddonName = "XBMMonsterNotebook";

    /// <summary>The event id of the Crucible of the Unbroken NPC's conversation.</summary>
    // `0x` writes a number in hexadecimal, as in JavaScript.
    private const uint CrucibleTalkEventId = 0xB03D0;

    /// <summary>How far into that conversation's event handler the beast record set begins.</summary>
    /// <remarks>
    /// <see cref="BeastRankRecord.VerifiedGameVersions"/> lists the game versions it was checked on.
    /// </remarks>
    private const int RankRecordOffset = 0x468;

    /// <summary>How often the handler is looked for while the character is at the entrance.</summary>
    // `static readonly` rather than `const`: a TimeSpan is built when the program runs, and `const`
    // only holds values fixed when the code compiles.
    private static readonly TimeSpan RankReadInterval = TimeSpan.FromSeconds(1);

    private readonly TamedBeastLedger ledger = new();

    // Faults met while reading the record set, reported in full once each, as readFailures does for
    // the window.
    private readonly RepeatedFailures rankFailures = new();

    /// <summary>Which read failures have already been reported in full.</summary>
    /// <remarks>
    /// The window refreshes while it stays open, so a layout this cannot parse would otherwise
    /// report on every refresh — full detail once, a line thereafter.
    /// </remarks>
    private readonly RepeatedFailures readFailures = new();

    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly Func<string, bool> isCategoryEnabled;
    private readonly IPluginLog log;

    // See the constructor's timeProvider parameter.
    private readonly TimeProvider timeProvider;

    // When the handler is next looked for. The default is the earliest possible moment, so the first
    // frame at the entrance looks at once.
    private DateTimeOffset nextRankReadAt;

    // Whether a refused reading of the record set has been reported since the last accepted one: the
    // same refusal would otherwise repeat every second the player stands at the entrance.
    private bool rankRefusalReported;

    // The running game's version, read once, and whether the note about an unchecked version has been
    // written. Null until read.
    private string? gameVersion;
    private bool unverifiedLayoutReported;

    // The bestiary's size, read from the game's data the first time the window opens or the NPC's
    // handler is found, rather than in the constructor: a character who never plays beastmaster
    // never pays for it.
    private int? beastCount;
    private bool beastCountReadFailed;

    // The figures of the last read written to the log, so a redraw that reads the same figures is
    // not written again; null until a read is logged, and again whenever the window opens or the
    // ledger is cleared.
    private BestiaryReadSummary? lastLoggedRead;

    /// <summary>
    /// Wires the listeners. Records nothing until the player opens their bestiary or talks to the
    /// Crucible's NPC.
    /// </summary>
    /// <param name="isCategoryEnabled">
    /// Answers whether the user has this collection switched on, given its category key. Passed in
    /// rather than reached for, so that this class holds no opinion about where consent lives and
    /// names no collection but its own.
    /// </param>
    /// <param name="timeProvider">
    /// The clock the record set's reads run on; the system clock when omitted.
    /// </param>
    // `TimeProvider? timeProvider = null` is an optional parameter, like `timeProvider?: TimeProvider`.
    public TamedBeastObserver(
        IClientState clientState,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        Func<string, bool> isCategoryEnabled,
        IPluginLog log,
        TimeProvider? timeProvider = null)
    {
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.isCategoryEnabled = isCategoryEnabled;
        this.log = log;

        // `??` uses the right-hand value when the left is null, as in JavaScript.
        this.timeProvider = timeProvider ?? TimeProvider.System;

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

        // Called once per game frame, on the framework thread (see OnFrameworkUpdate). Subscribed
        // last: a constructor that fails part-way never returns an object to dispose, so a frame
        // handler subscribed before the failing line would run for the rest of the session.
        framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Raised when a read finds a beast this session did not already know about, or a reading of the
    /// record set that differs from the last one, carrying the category key whose upload it should
    /// prompt.
    /// </summary>
    /// <remarks>
    /// The key travels with the event so that whatever schedules the upload never has to name this
    /// collection — the same self-description that keeps category names out of the unlock router.
    /// </remarks>
    public event Action<string>? BeastsLearned;

    /// <summary>
    /// The bestiary numbers the character is known to hold, ascending, from the window and the
    /// record set together.
    /// </summary>
    public IReadOnlyList<int> Pacts() => ledger.Pacts();

    /// <summary>
    /// Each beast's rank from the current reading of the record set, by bestiary number: empty until
    /// one is accepted, and again after a refused one, a session edge, or a check that finds the
    /// collection switched off.
    /// </summary>
    public IReadOnlyDictionary<int, int> Ranks() => ledger.Ranks();

    /// <summary>How many beasts the bestiary window has shown as held this session.</summary>
    public int HeldCount => ledger.Count;

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
        framework.Update -= OnFrameworkUpdate;
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
                values.Add(AtkValueList.Narrow(&addon->AtkValues[i]));

            var page = BestiaryPage.Read(values);

            // A read that found no records is not worth recording: the window was open but its
            // list had not landed yet, and the tally alone would move the completeness test
            // toward an answer the records have not earned.
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
    /// Looks for the beast record set while the character is at the Crucible of the Unbroken's
    /// entrance.
    /// </summary>
    /// <remarks>
    /// The NPC's conversation handler exists only in that zone, and leaving it removes the handler,
    /// so a record set read here always belongs to the character standing there. Anywhere else this
    /// returns at once; there, while the collection is switched on, it walks the game's handler
    /// registry once a second, on the framework thread, which the game's own state is only safe to read from.
    /// </remarks>
    // The parameter is the framework itself, which this handler has no use for; `_` names it as
    // deliberately unused.
    private void OnFrameworkUpdate(IFramework _)
    {
        // `(uint)` converts the territory id to the type the constant is declared with.
        if (!clientState.IsLoggedIn || (uint)clientState.TerritoryType != CrucibleTerritories.Entrance)
            return;

        var now = timeProvider.GetUtcNow();

        // A clock that jumped backwards would otherwise hold the reads off until it caught up.
        if (nextRankReadAt > now + RankReadInterval)
            nextRankReadAt = now;

        if (now < nextRankReadAt)
            return;
        nextRankReadAt = now + RankReadInterval;

        var isNews = false;

        try
        {
            // Nothing is captured while the collection is switched off, as the window's handler
            // also guarantees.
            if (!isCategoryEnabled(CategoryKeys.TamedBeasts))
            {
                ForgetCaptured();
                return;
            }

            isNews = ReadRanks();
        }
        catch (Exception ex)
        {
            // A frame callback that throws would escape into Dalamud's frame dispatch, so no read
            // failure is allowed out of here.
            if (rankFailures.IsFirstSighting(ex))
                log.Error(ex, "Could not read the beasts' ranks.");
            else
                log.Debug(ex, "Could not read the beasts' ranks again.");
        }

        // Outside the catch for the same reason as in OnBestiaryAddon.
        if (isNews)
            BeastsLearned?.Invoke(CategoryKeys.TamedBeasts);
    }

    /// <summary>Reads the record set from the NPC's conversation handler, if it has been filled.</summary>
    /// <returns>True when an accepted reading changed a rank or a leveled beast.</returns>
    private bool ReadRanks()
    {
        if (!TryCopyRankRecord(out var bytes, out var size))
            return false;

        var layoutVerified = BeastRankRecord.IsLayoutVerified(GameVersion(), size);
        var assessment = BeastRankRecord.Assess(bytes, size, ledger.Snapshot(), layoutVerified);

        // `switch` picks one branch by value, like a JavaScript switch statement.
        switch (assessment.Verdict)
        {
            case RankRecordVerdict.NotFilled:
                return false;

            case RankRecordVerdict.Refused:
                // See TamedBeastLedger.ForgetRanks for why an earlier reading goes too.
                ledger.ForgetRanks();
                if (!rankRefusalReported)
                {
                    rankRefusalReported = true;
                    log.Warning(
                        "The beasts' ranks were found but did not pass their checks, so none is sent " +
                        "until a reading passes; this is written once until then. Talking to the " +
                        "Crucible's NPC again refreshes them; if ranks still do not arrive after " +
                        "that, a game update may have moved them.");
                }

                return false;

            default:
                if (!layoutVerified && !unverifiedLayoutReported)
                {
                    unverifiedLayoutReported = true;
                    log.Information(
                        $"The beasts' rank record has not been checked on game version {GameVersion()}, " +
                        "so ranks are sent only for beasts the Master's Bestiary has shown as held.");
                }

                // A refusal after this one is news again and is reported.
                rankRefusalReported = false;

                // `!` tells the compiler the reading is present, which an Accepted verdict guarantees.
                var reading = assessment.Reading!;
                var changed = ledger.RecordRanks(reading);
                if (changed)
                {
                    log.Debug(
                        $"Beast ranks read: {reading.Ranks.Count} ranked, {reading.Leveled.Count} leveled.");
                }

                return changed;
        }
    }

    /// <summary>
    /// Copies the record set's bytes out of the NPC's conversation handler, when the handler exists.
    /// </summary>
    /// <param name="bytes">The record set's bytes, starting at the marker.</param>
    /// <param name="size">The bestiary's size, which sets how many bytes were copied.</param>
    /// <returns>False when there is no handler, no bestiary size, or the memory could not be read.</returns>
    // `out` parameters are extra return values the method must assign before it returns.
    private bool TryCopyRankRecord(out byte[] bytes, out int size)
    {
        bytes = [];
        size = 0;

        var events = EventFramework.Instance();
        if (events == null)
            return false;

        // The registry is a map from each running event's id to its handler. Each entry is a pair:
        // `Item1` the id, and `Item2` a wrapper around a pointer to the handler, whose `Value` is the
        // pointer. Only the ids are compared, and only the one matching handler is read. Walking the
        // map each time, rather than keeping a handler found earlier, means a handler the game has
        // since removed is never read.
        //
        // `nint` is an integer the size of a memory address, so the record set's offset can be added
        // to the handler's address as plain arithmetic.
        nint handler = 0;
        foreach (var pair in events->EventHandlerModule.EventHandlerMap)
        {
            if (pair.Item1 == CrucibleTalkEventId)
            {
                handler = (nint)pair.Item2.Value;
                break;
            }
        }

        if (handler == 0)
            return false;

        // The record set's length, and where its EXP figures start, both follow from the bestiary's
        // size; without it nothing can be checked. `is not > 0` is true for null as well as for 0 or
        // less, since a null value matches no number pattern.
        var count = BeastCount();
        if (count is not > 0)
            return false;

        // SafeMemory copies the bytes out through a guarded read, so an address that is no longer
        // valid fails the read rather than crashing the game.
        if (!Dalamud.SafeMemory.ReadBytes(
                handler + RankRecordOffset, BeastRankRecord.ByteLength(count.Value), out var copied))
        {
            return false;
        }

        bytes = copied;
        size = count.Value;
        return true;
    }

    /// <summary>The running game's version, read once from the game, or null when unreadable.</summary>
    private string? GameVersion()
    {
        if (gameVersion is null)
        {
            var game = GameFramework.Instance();
            if (game != null)
                gameVersion = game->GameVersionString;
        }

        return gameVersion;
    }

#if DEBUG
    /// <summary>
    /// Writes the record set to the log as it reads now, whatever the game version, so a maintainer
    /// can compare it with the NPC's bestiary after a game patch. Development builds only.
    /// </summary>
    /// <remarks>
    /// CLAUDE.md's "Re-checking the beast rank record after a game patch" says how to use it. Runs on
    /// the framework thread; the caller marshals there.
    /// </remarks>
    public void AuditRankRecord()
    {
        if (!TryCopyRankRecord(out var bytes, out var size))
        {
            log.Information(
                "Beast rank record: no NPC conversation handler is registered here, or it could not be " +
                "read. Talk to the NPC at the Crucible of the Unbroken's entrance first.");
            return;
        }

        var version = GameVersion() ?? "unknown";
        var verified = BeastRankRecord.IsLayoutVerified(version, size);
        var assessment = BeastRankRecord.Assess(bytes, size, ledger.Snapshot(), layoutVerified: true);
        var marker = BeastRankRecord.IsFilled(bytes) ? "filled" : "not filled";
        log.Information(
            $"Beast rank record on game version {version} ({(verified ? "checked" : "not checked")}), " +
            $"{size} beasts: {marker}, {assessment.Verdict}.");

        // One line per beast: the bytes as stored, so they can be compared with the bestiary's
        // detail panel, which shows the rank and the EXP as "67/100".
        for (var index = 0; index < size; index++)
        {
            var rank = bytes[BeastRankRecord.MarkerBytes + index];
            var exp = bytes[BeastRankRecord.MarkerBytes + size + index];
            log.Information($"  No. {index + 1}: rank {rank}, EXP {exp}");
        }
    }
#endif

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
