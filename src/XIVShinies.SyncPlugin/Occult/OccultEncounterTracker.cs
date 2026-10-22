using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Turns per-tick game readings into the tracker server's wire statuses, remembering enough history
/// to stamp the transitions the game zeroes.
/// </summary>
/// <remarks>
/// It reads neither the game nor the clock. <see cref="Apply"/> and <see cref="Reset"/> run on the
/// framework thread only, so the state needs no lock, and <see cref="Current"/> is an immutable
/// snapshot any thread may read. Every timestamp is whole seconds, because the server fingerprints
/// instances on exact <c>(encounter, sinceUtc)</c> equality across clients.
/// </remarks>
public sealed class OccultEncounterTracker
{
    /// <summary>What the tracker remembers about one encounter id.</summary>
    /// <param name="Status">The wire status last reported for this id.</param>
    /// <param name="SinceUtc">The timestamp last reported for this id.</param>
    /// <param name="SawBattleBegin">
    /// True when the id held another phase on an earlier tick, so this tracker watched the battle
    /// begin rather than meeting it under way. Meaningful only while Active (see
    /// <see cref="MapBattle"/>).
    /// </param>
    /// <param name="Battle">The start committed to for the running battle, once one could be derived.</param>
    private readonly record struct Entry(
        OccultEncounterStatus Status,
        DateTimeOffset? SinceUtc,
        bool SawBattleBegin = false,
        BattleStart? Battle = null);

    /// <summary>
    /// The battle start committed to for one occurrence, and the phase deadline it came from.
    /// </summary>
    /// <remarks>
    /// A null <see cref="Entry.Battle"/> means nothing is committed yet, so the next tick tries again. A
    /// non-null one is final for the occurrence, even with a null <paramref name="At"/>, which is what
    /// stops the next tick from re-deriving a value just discarded.
    /// </remarks>
    /// <param name="At">The committed start, or null once it has been proven unreliable.</param>
    /// <param name="FromDeadline">The phase deadline <paramref name="At"/> was derived from.</param>
    private readonly record struct BattleStart(DateTimeOffset? At, long FromDeadline);

    /// <summary>Last known state per CE id.</summary>
    private readonly Dictionary<ushort, Entry> ceStates = [];

    /// <summary>Last known state per FATE id. Down ghosts stay here until <see cref="Reset"/>.</summary>
    private readonly Dictionary<ushort, Entry> fateStates = [];

    private OccultEncounterState[] current = [];

    /// <summary>
    /// The wire-ready state of every tracked encounter: every CE slot the game reports, plus synced
    /// FATEs and the down ghosts of despawned ones.
    /// </summary>
    /// <remarks>
    /// An immutable snapshot: the array is never changed after publication, only replaced by the next
    /// <see cref="Apply"/>, so a caller holding it on a background task reads a consistent picture.
    /// </remarks>
    public IReadOnlyList<OccultEncounterState> Current => current;

    /// <summary>
    /// Ingests one tick's readings. Returns true when a wire status changed, a FATE's start
    /// epoch changed, or an id left the readings.
    /// </summary>
    public bool Apply(
        IReadOnlyList<DynamicEventReading> events, IReadOnlyList<FateReading> fates, DateTimeOffset now)
    {
        // Every stamp this tick shares one whole second.
        var observedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var changed = false;

        var report = new List<OccultEncounterState>();

        // CE container slots. The container reports the same ids every tick of an instance's life,
        // so the map's ids simply follow the game's.
        var seenCes = new HashSet<ushort>();
        foreach (var reading in events)
        {
            // A duplicate slot would put two contradictory rows for one encounter in the report.
            if (!seenCes.Add(reading.DynamicEventId))
                continue;

            var previous = ceStates.TryGetValue(reading.DynamicEventId, out var p) ? p : (Entry?)null;
            var entry = MapCe(reading, previous, observedAt);

            if (previous?.Status != entry.Status)
                changed = true;

            ceStates[reading.DynamicEventId] = entry;
            report.Add(new OccultEncounterState
            {
                IsFate = false, Id = reading.DynamicEventId, Status = entry.Status, SinceUtc = entry.SinceUtc,
            });
        }

        // A CE id the game stopped reporting leaves the report, and counts as a change so the server
        // hears the new shape promptly.
        var goneCes = CollectMissing(ceStates, seenCes, skipDown: false);
        if (goneCes is not null)
        {
            changed = true;
            foreach (var id in goneCes)
                ceStates.Remove(id);
        }

        // FATE table rows.
        var seenFates = new HashSet<ushort>();
        foreach (var reading in fates)
        {
            // An unsynced or garbage epoch. An untracked id is not reported yet (see FateReading); a
            // tracked one keeps its state, so a transient bad read cannot flap an active FATE.
            if (EpochOrNull(reading.StartEpoch) is not { } startUtc)
            {
                if (fateStates.ContainsKey(reading.FateId))
                    seenFates.Add(reading.FateId);
                continue;
            }

            seenFates.Add(reading.FateId);
            var previous = fateStates.TryGetValue(reading.FateId, out var p) ? p : (Entry?)null;
            var entry = new Entry(OccultEncounterStatus.Active, startUtc);

            // A FATE's start epoch identifies one spawn, so a new epoch on a still-Active id is a
            // despawn and respawn between polls.
            if (previous?.Status != entry.Status || previous?.SinceUtc != entry.SinceUtc)
                changed = true;

            fateStates[reading.FateId] = entry;
        }

        // A tracked FATE gone from the table despawned, completed or failed alike. It is stamped
        // once, and the ghost stays so later uploads still carry the down fact and its stamp.
        var despawned = CollectMissing(fateStates, seenFates, skipDown: true);
        if (despawned is not null)
        {
            changed = true;
            foreach (var id in despawned)
                fateStates[id] = new Entry(OccultEncounterStatus.Down, observedAt);
        }

        foreach (var (id, entry) in fateStates)
        {
            report.Add(new OccultEncounterState
            {
                IsFate = true, Id = id, Status = entry.Status, SinceUtc = entry.SinceUtc,
            });
        }

        // One reference assignment publishes the report, so no reader sees it half built.
        current = [.. report];

        return changed;
    }

    /// <summary>
    /// Forgets everything, as on leaving the instance, so the next instance's idle CEs read as unknown
    /// instead of inheriting this one's countdowns.
    /// </summary>
    public void Reset()
    {
        ceStates.Clear();
        fateStates.Clear();
        current = [];
    }

    /// <summary>Maps one CE reading to its wire entry, consulting history only for the down stamp.</summary>
    /// <remarks>
    /// The game zeroes every field when a CE ends, so the moment this plugin saw it end is the only
    /// stamp. It is set once and held, since re-stamping each tick would push the respawn countdown
    /// forward forever; a CE never seen up gets none.
    /// </remarks>
    private static Entry MapCe(DynamicEventReading reading, Entry? previous, DateTimeOffset observedAt)
    {
        switch (reading.Phase)
        {
            case DynamicEventPhase.Register:
            case DynamicEventPhase.Warmup:
                // The phase deadline is the fingerprint value. Its shift at Warmup is a new valid
                // pair, not a status change, so it rides out on the next upload.
                return new Entry(OccultEncounterStatus.Preparing, EpochOrNull(reading.PhaseDeadlineEpoch));

            case DynamicEventPhase.Battle:
                return MapBattle(reading, previous);

            case DynamicEventPhase.Inactive:
            default:
                // An unknown byte, perhaps a phase a patch added, is not verifiably up, so it reads
                // as down.
                if (previous is { } prev)
                {
                    return prev.Status == OccultEncounterStatus.Down
                        ? prev
                        : new Entry(OccultEncounterStatus.Down, observedAt);
                }

                return new Entry(OccultEncounterStatus.Down, null);
        }
    }

    /// <summary>Maps a Battle-phase reading, committing to a single start for the occurrence.</summary>
    /// <remarks>
    /// The Forked Tower's deadline is a completion deadline the game pushes back ten minutes per boss
    /// killed. So the start is derived once and held: re-derived, it would drift later with every kill
    /// and could overtake the clock. A start first met mid-battle may come from an already-postponed
    /// deadline, so it is dropped if that deadline later moves, rather than reported as a start no other
    /// client derives. A CE's deadline never moves, so zoning in on a running CE still yields its real
    /// start. A mid-battle start whose deadline never moves again, as in the tower's last stretch, stays
    /// wrong. The server treats an epoch ahead of its own clock as unusable, which keeps such a start
    /// from showing as a bogus elapsed time.
    /// </remarks>
    private static Entry MapBattle(DynamicEventReading reading, Entry? previous)
    {
        // A battle this tracker is already following.
        if (previous is { Status: OccultEncounterStatus.Active } running)
        {
            if (running.Battle is { } committed)
            {
                // The game only postpones a completion deadline, so a lower reading, zero included,
                // is a torn read and the start stands.
                var extended = !running.SawBattleBegin
                    && committed.At is not null
                    && reading.PhaseDeadlineEpoch > committed.FromDeadline;

                var held = extended ? committed with { At = null } : committed;
                return new Entry(OccultEncounterStatus.Active, held.At, running.SawBattleBegin, held);
            }

            // Nothing derivable yet: the deadline lags the phase by a tick or two while an instance
            // re-syncs.
            return CommitBattleStart(reading, running.SawBattleBegin);
        }

        // The first Battle tick for this id. With no previous entry the slot was met under way: a
        // zone-in mid-run, or an id the container dropped and brought back.
        var sawBattleBegin = previous is not null;

        // The phase byte and deadline sit in separate words, so Battle can arrive while the deadline is
        // still the preparing entry's stamp. A start derived from it would stay a whole duration early
        // all battle, so nothing is committed and the next tick tries a synced read.
        if (previous is { Status: OccultEncounterStatus.Preparing } preparing
            && preparing.SinceUtc is not null
            && preparing.SinceUtc == EpochOrNull(reading.PhaseDeadlineEpoch))
        {
            return new Entry(OccultEncounterStatus.Active, null, sawBattleBegin);
        }

        return CommitBattleStart(reading, sawBattleBegin);
    }

    /// <summary>
    /// Derives the battle start from one reading and commits it, or leaves the entry
    /// uncommitted when the reading cannot produce one.
    /// </summary>
    private static Entry CommitBattleStart(DynamicEventReading reading, bool sawBattleBegin)
    {
        // Deadline minus duration, never a local clock, so every client derives the same start. A
        // torn nonpositive duration would give a plausible epoch nobody else derives, worse than none.
        var start = EpochOrNull(reading.PhaseDeadlineEpoch > 0 && reading.DurationSeconds > 0
            ? reading.PhaseDeadlineEpoch - reading.DurationSeconds
            : 0);

        // Leaving the commitment unset lets the next tick try, instead of freezing at no timestamp.
        if (start is null)
            return new Entry(OccultEncounterStatus.Active, null, sawBattleBegin);

        return new Entry(
            OccultEncounterStatus.Active,
            start,
            sawBattleBegin,
            new BattleStart(start, reading.PhaseDeadlineEpoch));
    }

    /// <summary>
    /// The largest epoch <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts, 9999-12-31T23:59:59Z.
    /// Garbage read during a zone transition must not throw on the framework thread.
    /// </summary>
    private const long MaxEpoch = 253402300799;

    /// <summary>
    /// A whole-second epoch as a timestamp, or null for 0, which means unsynced, or for a value no real
    /// clock could produce.
    /// </summary>
    private static DateTimeOffset? EpochOrNull(long epoch) =>
        epoch > 0 && epoch <= MaxEpoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;

    /// <summary>
    /// The keys of <paramref name="states"/> absent from <paramref name="seen"/>, or null if none.
    /// With <paramref name="skipDown"/>, entries already Down are left out.
    /// </summary>
    /// <remarks>
    /// Returning the keys lets callers write the dictionary after its enumeration ends. The list is
    /// created on the first hit, so the common case allocates nothing.
    /// </remarks>
    private static List<ushort>? CollectMissing(
        Dictionary<ushort, Entry> states, HashSet<ushort> seen, bool skipDown)
    {
        List<ushort>? missing = null;
        foreach (var (id, entry) in states)
        {
            if (skipDown && entry.Status == OccultEncounterStatus.Down)
                continue;

            if (!seen.Contains(id))
                (missing ??= []).Add(id);
        }

        return missing;
    }
}
