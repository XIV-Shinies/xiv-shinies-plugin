using System;
using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Turns raw per-tick game readings into the tracker server's wire vocabulary, and remembers
/// just enough history to stamp the transitions the game itself zeroes.
/// </summary>
/// <remarks>
/// <para>
/// Pure bookkeeping in the house style of <c>SyncScheduler</c>: it never reads the game, never
/// touches the clock (every call takes <c>now</c>), and holds no Dalamud types — which is what
/// lets the timestamp rules live under unit tests. <see cref="Apply"/> and <see cref="Reset"/>
/// are only ever called from the framework thread, so the mutable state needs no lock;
/// <see cref="Current"/> may be read from any thread because it hands out an immutable
/// snapshot (see its remarks).
/// </para>
/// <para>
/// The memory matters for three things. First, <b>down-stamping</b>: when a CE flips out of
/// Battle (or a FATE leaves the table), every game-side field is already zero, so the only
/// honest timestamp is "when this plugin saw it happen" — recorded once and then held, because
/// re-stamping each tick would walk the respawn countdown forward forever. Second, <b>change
/// detection</b>: <see cref="Apply"/> reports when a wire status changed, a FATE's start
/// epoch changed, or an id left the readings — which is what the upload scheduler
/// debounces on. A preparing CE's phase deadline is not in that set: it shifts at
/// Register→Warmup without moving the status, and rides out on the next upload. Third,
/// <b>battle anchoring</b>: a running battle's start is derived once and then held, because
/// the deadline it comes from can move under it (see <see cref="MapBattle"/>).
/// </para>
/// <para>
/// Everywhere a timestamp appears it is whole seconds. The server fingerprints instances on
/// exact <c>(encounter, sinceUtc)</c> equality across independent clients, so second-exact
/// discipline here is load-bearing, not cosmetic.
/// </para>
/// </remarks>
public sealed class OccultEncounterTracker
{
    /// <summary>What the tracker remembers about one encounter id.</summary>
    /// <param name="Status">The wire status last reported for this id.</param>
    /// <param name="SinceUtc">The timestamp last reported for this id.</param>
    /// <param name="SawBattleBegin">
    /// True when this tracker watched the slot enter Battle — it held some other phase for the
    /// id on an earlier tick — rather than first meeting it already under way. Meaningful only
    /// while the id is Active, and the reason a derived start can be trusted (see
    /// <see cref="MapBattle"/>).
    /// </param>
    /// <param name="Battle">The start committed to for the running battle, once one could be derived.</param>
    private readonly record struct Entry(
        OccultEncounterStatus Status,
        DateTimeOffset? SinceUtc,
        bool SawBattleBegin = false,
        BattleStart? Battle = null);

    /// <summary>
    /// The battle start the tracker has committed to for one occurrence, and the phase deadline
    /// it was derived from.
    /// </summary>
    /// <remarks>
    /// A null <see cref="Entry.Battle"/> means no start has been committed yet, so the next tick
    /// tries again; a non-null one is final for the occurrence. <paramref name="At"/> is itself
    /// nullable because a committed start can later be proven unreliable, and the distinction
    /// between "nothing yet" and "nothing usable, ever" is what stops the next tick from
    /// re-deriving the value that was just discarded.
    /// </remarks>
    /// <param name="At">The committed start, or null once it has been proven unreliable.</param>
    /// <param name="FromDeadline">The phase deadline <paramref name="At"/> was derived from.</param>
    private readonly record struct BattleStart(DateTimeOffset? At, long FromDeadline);

    /// <summary>Last known state per CE id, following whatever ids the container reports.</summary>
    private readonly Dictionary<ushort, Entry> ceStates = [];

    /// <summary>Last known state per FATE id. Down ghosts stay here until <see cref="Reset"/>.</summary>
    private readonly Dictionary<ushort, Entry> fateStates = [];

    /// <summary>
    /// The wire-ready report from the latest <see cref="Apply"/>, published through
    /// <see cref="Current"/> (whose remarks explain the snapshot guarantee).
    /// </summary>
    private OccultEncounterState[] current = [];

    /// <summary>
    /// The current wire-ready state of every tracked encounter: all CE slots the game reports,
    /// plus initialized FATEs and the down ghosts of FATEs that have despawned.
    /// </summary>
    /// <remarks>
    /// The returned list is an immutable snapshot: its elements are init-only records and the
    /// array itself is never mutated after publication, only replaced by the next
    /// <see cref="Apply"/>. A caller that captures the reference (say, an uploader serializing
    /// on a background task) therefore reads a consistent picture even while new ticks land.
    /// </remarks>
    public IReadOnlyList<OccultEncounterState> Current => current;

    /// <summary>
    /// Ingests one tick's readings. Returns true when a wire status changed, a FATE's start
    /// epoch changed, or an id left the readings.
    /// </summary>
    public bool Apply(
        IReadOnlyList<DynamicEventReading> events, IReadOnlyList<FateReading> fates, DateTimeOffset now)
    {
        // Truncate once: every observation stamp this tick shares the same whole second.
        var observedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var changed = false;

        // Built fresh and published at the end, so the outgoing Current snapshot stays
        // readable while this tick assembles the next one.
        var report = new List<OccultEncounterState>();

        // --- CE container slots ----------------------------------------------------------
        // The container reports the same fixed set of ids every tick of an instance's life,
        // so the map's id membership simply follows what the game reports.
        var seenCes = new HashSet<ushort>();
        foreach (var reading in events)
        {
            // HashSet.Add returns false for an id already seen this tick — a duplicate slot
            // would otherwise put two contradictory rows for one encounter in the report.
            if (!seenCes.Add(reading.DynamicEventId))
                continue;

            // `TryGetValue` + the conditional needs the (Entry?) cast because C# cannot pick
            // a common type between the non-nullable struct `p` and `null` on its own.
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

        // A CE id the game stopped reporting drops out of the report. Losing an id counts as
        // a change so the server hears the new shape promptly.
        var goneCes = CollectMissing(ceStates, seenCes, skipDown: false);
        if (goneCes is not null)
        {
            changed = true;
            foreach (var id in goneCes)
                ceStates.Remove(id);
        }

        // --- FATE table rows -------------------------------------------------------------
        var seenFates = new HashSet<ushort>();
        foreach (var reading in fates)
        {
            // No usable epoch: either a pre-init row (on the table but not yet synced by the
            // server, so zero) or a value no real clock could produce. An UNTRACKED id is
            // simply not reported yet — a bogus epoch would upload a fingerprint pair that
            // identifies nothing. A TRACKED id counts as still present, keeping its known
            // state, so a transient unsynced read cannot flap an active FATE down and up.
            if (EpochOrNull(reading.StartEpoch) is not { } startUtc)
            {
                if (fateStates.ContainsKey(reading.FateId))
                    seenFates.Add(reading.FateId);
                continue;
            }

            seenFates.Add(reading.FateId);
            var previous = fateStates.TryGetValue(reading.FateId, out var p) ? p : (Entry?)null;
            var entry = new Entry(OccultEncounterStatus.Active, startUtc);

            // Unlike a CE — whose deadline shifts within one lifecycle — a FATE's start epoch
            // identifies a distinct spawn. A new epoch on an id that stayed Active means a
            // despawn and respawn landed between two polls, so the epoch participates in
            // change detection alongside the status.
            if (previous?.Status != entry.Status || previous?.SinceUtc != entry.SinceUtc)
                changed = true;

            fateStates[reading.FateId] = entry;
        }

        // A tracked FATE that vanished from the table just despawned (completed or timed
        // out — indistinguishable, and one honest "down" is all the contract asks). Stamp
        // the observation once; the ghost then persists so later uploads still carry the
        // down fact and its stamp.
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

        // Publish the finished report in one reference assignment (atomic in .NET), which is
        // what makes Current a tear-free snapshot for any reader.
        current = [.. report];

        return changed;
    }

    /// <summary>
    /// Forgets everything, as when leaving the instance. Down stamps belong to the instance
    /// they were observed in — the next instance's idle CEs must read as unknown, not
    /// inherit a countdown from somewhere else.
    /// </summary>
    public void Reset()
    {
        ceStates.Clear();
        fateStates.Clear();
        current = [];
    }

    /// <summary>Maps one CE reading to its wire entry, consulting history only for the down stamp.</summary>
    private static Entry MapCe(DynamicEventReading reading, Entry? previous, DateTimeOffset observedAt)
    {
        switch (reading.Phase)
        {
            case DynamicEventPhase.Register:
            case DynamicEventPhase.Warmup:
                // The phase deadline is the server-assigned fingerprint value for a preparing
                // CE. It shifts at Register→Warmup; that is a new (still valid) fingerprint
                // pair, not a status change.
                return new Entry(OccultEncounterStatus.Preparing, EpochOrNull(reading.PhaseDeadlineEpoch));

            case DynamicEventPhase.Battle:
                return MapBattle(reading, previous);

            case DynamicEventPhase.Inactive:
            default:
                // Inactive — and any byte outside the known enum (a future game patch could
                // add phases), because "not verifiably up" is the only safe reading of an
                // unknown state. If this plugin just WATCHED the CE end, its observation
                // time is the only timestamp in existence (the game zeroed everything) —
                // stamp it once and hold it. An already-down CE keeps its stamp; a CE never
                // observed up has nothing honest to say.
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
    /// <para>
    /// A slot's phase deadline is not fixed for the life of its battle. The Forked Tower's is a
    /// <b>completion</b> deadline that the game pushes back ten minutes on every boss the party
    /// kills, so a start re-derived each tick drifts later every time — a different value on
    /// every tick, never the one a client that watched the battle begin derived, and able to
    /// overtake the clock entirely, where any elapsed-time reading of it collapses to zero.
    /// The start is therefore derived once and then held: a start taken while the battle was
    /// under observation is not improved on by any later reading.
    /// </para>
    /// <para>
    /// Holding is only sound if the value was taken before the deadline could move, which is
    /// what <see cref="Entry.SawBattleBegin"/> records. A slot first met mid-battle may have
    /// been read from an already-postponed deadline, putting its "start" later than the real
    /// one. A deadline that later moves settles that this slot's deadline is the postponing
    /// kind — which the reading cannot distinguish from one already postponed when it was
    /// taken — so the value is dropped rather than reported as a start no other observer
    /// derives. A lower or zero reading is a torn read, and the start is held. A CE's deadline
    /// never moves within a battle, so zoning in on one already up still yields its real start.
    /// </para>
    /// <para>
    /// A start met mid-battle whose deadline never moves again — the tower's last stretch,
    /// after the final boss — stays wrong, and no reading the game offers can correct it. The
    /// server treats an epoch ahead of its own clock as unusable, which is what keeps that
    /// case from reaching a reader as a bogus elapsed time.
    /// </para>
    /// </remarks>
    private static Entry MapBattle(DynamicEventReading reading, Entry? previous)
    {
        // A battle this tracker is already following.
        if (previous is { Status: OccultEncounterStatus.Active } running)
        {
            if (running.Battle is { } committed)
            {
                // Direction is the whole test (the remarks above own the reasoning): the game
                // only ever postpones a completion deadline, never pulls one in, so anything
                // lower — a zero included — is a torn read and the start stands.
                var extended = !running.SawBattleBegin
                    && committed.At is not null
                    && reading.PhaseDeadlineEpoch > committed.FromDeadline;

                // `with` copies a record and replaces the named members — the closest analog
                // is object spread ({...committed, At: null}), except the result is a new
                // value of the same type rather than a loosely shaped object.
                var held = extended ? committed with { At = null } : committed;
                return new Entry(OccultEncounterStatus.Active, held.At, running.SawBattleBegin, held);
            }

            // Still following, but nothing has been derivable yet — the deadline lags the
            // phase by a tick or two while an instance re-syncs. Try again, carrying forward
            // whether the battle began under observation.
            return CommitBattleStart(reading, running.SawBattleBegin);
        }

        // The first tick of this battle for this id. A previous entry means the tracker held a
        // different phase for the id a tick ago and so watched the battle begin; no previous
        // entry means the slot was first met already under way — a zone-in part way through a
        // run, or an id the container had dropped and brought back.
        var sawBattleBegin = previous is not null;

        // The phase byte and the deadline sit in separate words of the container, so a slot
        // can report Battle while its deadline still holds the phase that just ended.
        // Deriving from that would put the start a whole duration early, and a start taken
        // under observation is never revisited — the error would last the battle. The
        // preceding entry's own timestamp IS that stale value, which reduces the test to a
        // comparison. Committing nothing leaves the next tick to derive from a synced read.
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
        // The game reports the battle's END deadline. Every client must derive the same start,
        // so it comes from deadline − duration — never a local clock. A nonpositive duration (a
        // torn read of the raw uint) would push the derived start PAST the deadline: a
        // plausible-looking epoch no other observer would derive, which is worse than no
        // timestamp — so it degrades to null instead.
        var start = EpochOrNull(reading.PhaseDeadlineEpoch > 0 && reading.DurationSeconds > 0
            ? reading.PhaseDeadlineEpoch - reading.DurationSeconds
            : 0);

        // Nothing usable this tick. Leaving the commitment unset is what lets the next tick
        // derive one, rather than freezing this occurrence at "no timestamp".
        if (start is null)
            return new Entry(OccultEncounterStatus.Active, null, sawBattleBegin);

        return new Entry(
            OccultEncounterStatus.Active,
            start,
            sawBattleBegin,
            new BattleStart(start, reading.PhaseDeadlineEpoch));
    }

    /// <summary>
    /// The latest moment <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts
    /// (9999-12-31T23:59:59Z). Raw game memory can hold garbage during zone transitions, and
    /// an out-of-range epoch would throw — on the framework thread — instead of converting.
    /// </summary>
    private const long MaxEpoch = 253402300799;

    /// <summary>
    /// A whole-second epoch as a timestamp, or null when the game reports 0 (unsynced) or a
    /// value no real clock could produce (a torn or garbage read).
    /// </summary>
    private static DateTimeOffset? EpochOrNull(long epoch) =>
        epoch > 0 && epoch <= MaxEpoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;

    /// <summary>
    /// The keys of <paramref name="states"/> absent from <paramref name="seen"/>, or null if
    /// none. With <paramref name="skipDown"/>, entries already Down are not reported (their
    /// absence is old news).
    /// </summary>
    /// <remarks>
    /// Buffering the keys keeps the callers' dictionary writes out of the dictionary's own
    /// enumeration, and <c>(missing ??= []).Add(id)</c> creates the list only on the first
    /// hit — the common case (nothing missing) allocates nothing.
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
