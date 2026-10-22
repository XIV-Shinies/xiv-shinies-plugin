using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// Learns which beasts the character holds, and each one's rank, from the Master's Bestiary when
/// the player opens it and from the record set the Crucible's NPC loads.
/// </summary>
/// <remarks>
/// The window holds the character's whole bestiary, not just beasts taken while the plugin watched,
/// shown a page at a time (see <see cref="BestiaryPage"/> and <see cref="TamedBeastLedger"/>), but
/// no rank beyond the selected beast's, so <see cref="OnFrameworkUpdate"/> reads every rank from the
/// NPC's record set. Purely passive: it reads the window only when the player opens it, and the record
/// set stays empty until the player talks to the NPC. It never opens a window, starts a conversation or
/// talks to the game's servers, and watches no other window. Nothing is captured while the collection
/// is off, captures clear at both session edges, and the upload gate sits downstream of all of it.
/// </remarks>
public sealed unsafe class TamedBeastObserver : IDisposable
{
    private const string BestiaryAddonName = "XBMMonsterNotebook";

    /// <summary>The event id of the Crucible of the Unbroken NPC's conversation.</summary>
    private const uint CrucibleTalkEventId = 0xB03D0;

    /// <summary>How far into that conversation's event handler the beast record set begins.</summary>
    /// <remarks>
    /// No published definition names this place, so a game patch can move it; see
    /// <see cref="BeastRankRecord"/> for the checks that refuse a reading taken from elsewhere, and
    /// the moves they cannot catch.
    /// </remarks>
    private const int RankRecordOffset = 0x468;

    /// <summary>How often the handler is looked for while the character is at the entrance.</summary>
    private static readonly TimeSpan RankReadInterval = TimeSpan.FromSeconds(1);

    private readonly TamedBeastLedger ledger = new();

    // Record set read faults, each reported in full once.
    private readonly RepeatedFailures rankFailures = new();

    /// <summary>
    /// Window read faults, each reported in full once. The window refreshes while open, so an
    /// unparseable layout would otherwise report on every refresh.
    /// </summary>
    private readonly RepeatedFailures readFailures = new();

    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly Func<string, bool> isCategoryEnabled;
    private readonly IPluginLog log;

    private readonly TimeProvider timeProvider;

    // Starts at the earliest moment, so the first frame at the entrance looks at once.
    private DateTimeOffset nextRankReadAt;

    // Whether a refused reading has been reported since the last accepted one; the same refusal would
    // otherwise repeat every second the player stands at the entrance.
    private bool rankRefusalReported;

    // The bestiary's size, read from the game's data on first need rather than in the constructor, so
    // a character who never plays beastmaster never pays for it.
    private int? beastCount;
    private bool beastCountReadFailed;

    // The figures of the last read written to the log, so a redraw with the same figures is not written
    // again. Null until a read is logged, and again whenever the window opens or the ledger is cleared.
    private BestiaryReadSummary? lastLoggedRead;

    /// <summary>
    /// Wires the listeners. Records nothing until the player opens their bestiary or talks to the
    /// Crucible's NPC.
    /// </summary>
    /// <param name="isCategoryEnabled">
    /// Whether the user has the given category switched on. Passed in so this class holds no opinion
    /// on where consent lives and names no collection but its own.
    /// </param>
    /// <param name="timeProvider">
    /// The clock the record set's reads run on; the system clock when omitted.
    /// </param>
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
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // PostSetup fires when the window opens, before its list loads; PostRefresh fires on each
        // redraw while it stays open, which is how the loaded list and each turned page arrive.
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, BestiaryAddonName, OnBestiaryAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, BestiaryAddonName, OnBestiaryAddon);

        // Both session edges: a session that ended without its logout event would otherwise leave one
        // character's beasts in place for the next character to upload as their own.
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;

        // Subscribed last: a constructor that fails part-way returns nothing to dispose, so a frame
        // handler subscribed earlier would run for the rest of the session.
        framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Raised when a read finds a beast this session did not know, or a record set reading that
    /// differs from the last, with the category key whose upload it should prompt.
    /// </summary>
    /// <remarks>
    /// The key travels with the event, so the upload scheduler never names this collection.
    /// </remarks>
    public event Action<string>? BeastsLearned;

    /// <summary>
    /// The bestiary numbers the character is known to hold, ascending, from the window and the
    /// record set together.
    /// </summary>
    public IReadOnlyList<int> Pacts() => ledger.Pacts();

    /// <summary>
    /// Each beast's rank from the current record set reading, by bestiary number: empty until one is
    /// accepted, and again after a refused one, a session edge, or finding the collection switched off.
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
    /// After a clear the next read starts the ledger afresh, so it is logged even when its figures
    /// match a read from before.
    /// </remarks>
    private void ForgetCaptured()
    {
        ledger.Clear();
        lastLoggedRead = null;
    }

    /// <summary>Reads whatever page of the bestiary the player has on screen.</summary>
    /// <remarks>
    /// Every dereference is guarded by hand: following a null pointer is an access violation no
    /// <c>catch</c> can rescue, and it would crash the game.
    /// </remarks>
    private void OnBestiaryAddon(AddonEvent type, AddonArgs args)
    {
        var isNews = false;

        try
        {
            // Nothing is captured while the collection is off. Reopening the window after switching
            // it on reads everything again, so refusing here costs the player nothing.
            if (!isCategoryEnabled(CategoryKeys.TamedBeasts))
            {
                ForgetCaptured();
                return;
            }

            // The opening arrives before the list loads. Clearing the last logged read here makes the
            // first read after it always logged, which shows a reopening in the log.
            if (type == AddonEvent.PostSetup)
                lastLoggedRead = null;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
                return;

            // The bestiary's size from the game's data (see TamedBeastLedger.IsComplete).
            var size = BeastCount();
            if (size is > 0)
                ledger.NoteDomainSize(size.Value);

            var values = new List<AddonValue>(addon->AtkValuesCount);
            for (var i = 0; i < addon->AtkValuesCount; i++)
                values.Add(AtkValueList.Narrow(&addon->AtkValues[i]));

            var page = BestiaryPage.Read(values);

            // No records means the list has not landed yet, and the tally alone would move the
            // completeness test toward an answer the records have not earned.
            if (page.Seen.Count == 0)
                return;

            isNews = ledger.RecordPage(page);

            // Every figure the completeness test weighs, so a withheld claim shows which agreement
            // failed. The page's tally sits beside the ledger's because a filter lowers only the page's.
            var summary = new BestiaryReadSummary(
                page.Seen.Count, page.Captured.Count, page.CapturedTotal, page.BeastTotal,
                ledger.Report(), size);

            // Logged only on a change: the window redraws many times a second, and the message is
            // built even when the log level discards it.
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
            // An addon callback that throws would escape into Dalamud's UI dispatch.
            if (readFailures.IsFirstSighting(ex))
                log.Error(ex, "Could not read the Master's Bestiary.");
            else
                log.Debug(ex, "Could not read the Master's Bestiary again.");
        }

        // Outside the catch, so a fault in upload scheduling reports as itself. Only news prompts an
        // upload; otherwise paging through a bestiary already read would schedule a sync per page.
        if (isNews)
            BeastsLearned?.Invoke(CategoryKeys.TamedBeasts);
    }

    /// <summary>
    /// Looks for the beast record set while the character is at the Crucible of the Unbroken's
    /// entrance.
    /// </summary>
    /// <remarks>
    /// The NPC's conversation handler exists only in that zone and goes when the character leaves, so
    /// a record set read here always belongs to the character standing there. Elsewhere this returns
    /// at once; there, while the collection is on, it walks the handler registry once a second on the
    /// framework thread, the only thread the game's state is safe to read from.
    /// </remarks>
    private void OnFrameworkUpdate(IFramework _)
    {
        if (!clientState.IsLoggedIn || (uint)clientState.TerritoryType != CrucibleTerritories.Entrance)
            return;

        var now = timeProvider.GetUtcNow();

        // A clock that jumped backward would otherwise hold the reads off until it caught up.
        if (nextRankReadAt > now + RankReadInterval)
            nextRankReadAt = now;

        if (now < nextRankReadAt)
            return;
        nextRankReadAt = now + RankReadInterval;

        var isNews = false;

        try
        {
            // Nothing is captured while the collection is off, as in the window's handler.
            if (!isCategoryEnabled(CategoryKeys.TamedBeasts))
            {
                ForgetCaptured();
                return;
            }

            isNews = ReadRanks();
        }
        catch (Exception ex)
        {
            // A frame callback that throws would escape into Dalamud's frame dispatch.
            if (rankFailures.IsFirstSighting(ex))
                log.Error(ex, "Could not read the beasts' ranks.");
            else
                log.Debug(ex, "Could not read the beasts' ranks again.");
        }

        // Outside the catch, so a fault in upload scheduling reports as itself.
        if (isNews)
            BeastsLearned?.Invoke(CategoryKeys.TamedBeasts);
    }

    /// <summary>Reads the record set from the NPC's conversation handler, if it has been filled.</summary>
    /// <returns>True when an accepted reading changed a rank or a leveled beast.</returns>
    private bool ReadRanks()
    {
        if (!TryCopyRankRecord(out var bytes, out var size))
            return false;

        var assessment = BeastRankRecord.Assess(bytes, size, ledger.Snapshot());

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
                // A refusal after this one is news again and is reported.
                rankRefusalReported = false;

                // An Accepted verdict guarantees the reading is present.
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
    private bool TryCopyRankRecord(out byte[] bytes, out int size)
    {
        bytes = [];
        size = 0;

        var events = EventFramework.Instance();
        if (events == null)
            return false;

        // The registry maps each running event's id (Item1) to a wrapped handler pointer (Item2.Value).
        // It is walked on every read, not cached, so a handler the game has removed is never read.
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

        // The record set's length, and where its EXP figures and HP records start, follow from the
        // bestiary's size; without it nothing can be checked.
        var count = BeastCount();
        if (count is not > 0)
            return false;

        // A guarded read, so an address that has gone invalid fails the read instead of crashing the
        // game.
        if (!Dalamud.SafeMemory.ReadBytes(
                handler + RankRecordOffset, BeastRankRecord.ByteLength(count.Value), out var copied))
        {
            return false;
        }

        bytes = copied;
        size = count.Value;
        return true;
    }

#if DEBUG
    /// <summary>
    /// Writes the record set to the log as it reads now, whatever its checks decide, for comparing
    /// with the game when ranks stop arriving after a patch. Debug builds only.
    /// </summary>
    /// <remarks>
    /// CLAUDE.md's "Investigating the beast rank record after a game patch" says how to use it. Runs
    /// on the framework thread; the caller marshals there.
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

        var assessment = BeastRankRecord.Assess(bytes, size, ledger.Snapshot());
        var marker = BeastRankRecord.IsFilled(bytes) ? "filled" : "not filled";
        log.Information($"Beast rank record, {size} beasts: {marker}, {assessment.Verdict}.");

        // Rank and EXP compare with the bestiary's detail panel ("67/100"). Before any feed, a Crucible
        // run's team window on board N shows the beast's Nth HP figure.
        for (var index = 0; index < size; index++)
        {
            var rank = bytes[BeastRankRecord.MarkerBytes + index];
            var exp = bytes[BeastRankRecord.MarkerBytes + size + index];
            var hp = BeastRankRecord.HpRecord(bytes, size, index);
            var shape = BeastRankRecord.IsWellFormedHpRecord(hp) ? "well-formed" : "misshapen";
            log.Information(
                $"  No. {index + 1}: rank {rank}, EXP {exp}, HP record " +
                $"{BeastRankRecord.HpRecordWord(hp, 0)} {BeastRankRecord.HpRecordWord(hp, 1)} " +
                $"{BeastRankRecord.HpRecordWord(hp, 2)} {BeastRankRecord.HpRecordWord(hp, 3)} " +
                $"{BeastRankRecord.HpRecordWord(hp, 4)} ({shape})");
        }
    }
#endif

    /// <summary>The bestiary's size, read once and kept, or null when the sheet cannot be read.</summary>
    private int? BeastCount()
    {
        if (beastCount is not null)
            return beastCount;

        beastCount = BestiaryReader.CountBeasts(dataManager);

        // Warned once, then retried in silence: a static sheet's failure is likely permanent, but one
        // lookup is cheap, and giving up would withhold the completeness claim for the whole session.
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
