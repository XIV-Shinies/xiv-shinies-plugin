using System;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// Turns raw per-tick game readings (CE container slots + FATE table rows) into the wire
// vocabulary the tracker server understands: a status per encounter plus a second-exact
// sinceUtc. The server fingerprints instances on exact epoch equality across independent
// clients, so the derivation rules here are load-bearing — two plugins observing the same
// instance must produce byte-identical timestamps.
public class OccultEncounterTrackerTests
{
    // An arbitrary fixed instant. Tests move time by adding to it, never by waiting.
    private static readonly DateTimeOffset T0 = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);

    // A realistic phase-deadline epoch (the game reports whole seconds).
    private const long Deadline = 1786465621;
    private const int CeDuration = 1200;

    // The Forked Tower runs an hour to a CE's twenty minutes, and the game pushes its
    // deadline ten minutes further out for every boss the party kills.
    private const int TowerDuration = 3600;
    private const int BossExtension = 600;

    private static DynamicEventReading Ce(
        ushort id, DynamicEventPhase phase, long deadline = Deadline, int duration = CeDuration) =>
        new(id, phase, deadline, duration);

    // The South Horn Forked Tower slot, at the tower's own duration.
    private static DynamicEventReading Tower(DynamicEventPhase phase, long deadline = Deadline) =>
        new(48, phase, deadline, TowerDuration);

    // Warmup ends exactly when the battle begins, so a preparing tower's deadline is the
    // battle's start — one duration before the battle's own deadline.
    private const long WarmupDeadline = Deadline - TowerDuration;

    private static FateReading Fate(ushort id, long startEpoch) => new(id, startEpoch);

    private static OccultEncounterState Single(OccultEncounterTracker tracker, ushort id) =>
        Assert.Single(tracker.Current, s => s.Id == id);

    // --- CE status mapping ---------------------------------------------------------------

    [Fact]
    public void A_registering_ce_is_preparing_with_the_registration_deadline_as_sinceUtc()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Register)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Preparing, state.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Deadline), state.SinceUtc);
    }

    [Fact]
    public void A_warming_up_ce_is_still_preparing()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Warmup)], [], T0);

        Assert.Equal(OccultEncounterStatus.Preparing, Single(tracker, 46).Status);
    }

    // The game reports a battle's END deadline, never its start. Both clients must derive the
    // same start, so it comes from the deadline minus the duration — never from a local clock.
    [Fact]
    public void A_battling_ce_is_active_with_start_derived_from_deadline_minus_duration()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Deadline - CeDuration), state.SinceUtc);
    }

    // An idle CE the plugin never saw end carries no timestamp: the container zeroes every
    // field at the Battle→Inactive flip, so a client that arrived later knows nothing.
    [Fact]
    public void An_idle_ce_never_observed_up_is_down_with_no_timestamp()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Down, state.Status);
        Assert.Null(state.SinceUtc);
    }

    // --- The battle start anchor -----------------------------------------------------------

    // A tower battle watched from its start keeps the start it was anchored at, even after the
    // deadline is extended.
    [Fact]
    public void A_battle_watched_from_its_start_holds_that_start_when_the_deadline_is_extended()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Warmup, deadline: WarmupDeadline)], [], T0);
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(1));
        tracker.Apply(
            [Tower(DynamicEventPhase.Battle, deadline: Deadline + BossExtension)],
            [], T0 + TimeSpan.FromMinutes(6));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - TowerDuration), Single(tracker, 48).SinceUtc);
    }

    // Until the deadline word catches up with the phase word, nothing is committed; the next
    // synced tick anchors the start.
    [Fact]
    public void A_battle_whose_deadline_still_holds_the_previous_phase_waits_for_the_real_one()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Warmup, deadline: WarmupDeadline)], [], T0);
        tracker.Apply([Tower(DynamicEventPhase.Battle, deadline: WarmupDeadline)], [], T0 + TimeSpan.FromSeconds(1));

        Assert.Null(Single(tracker, 48).SinceUtc);

        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(2));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - TowerDuration), Single(tracker, 48).SinceUtc);
    }

    // A start anchored under observation is held whatever the deadline later reads — the
    // deadline is not consulted at all once the battle was watched from its start.
    [Fact]
    public void A_held_start_survives_a_deadline_that_stops_reading()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Warmup, deadline: WarmupDeadline)], [], T0);
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(1));
        tracker.Apply([Tower(DynamicEventPhase.Battle, deadline: 0)], [], T0 + TimeSpan.FromSeconds(2));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - TowerDuration), Single(tracker, 48).SinceUtc);
    }

    // The same for a start derived mid-battle, where a LATER deadline IS grounds for dropping
    // it: a zero is the container saying "not yet", so the start stands.
    [Fact]
    public void A_start_derived_under_way_survives_a_deadline_that_stops_reading()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Battle, deadline: 0)], [], T0 + TimeSpan.FromSeconds(1));
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(2));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - CeDuration), Single(tracker, 46).SinceUtc);
    }

    // A slot first met mid-battle (a zone-in part way through a run) may have been read from an
    // already-postponed deadline, so its start is provisional. A deadline that later moves
    // settles that this deadline postpones, and the provisional start is dropped rather than
    // reported as one no other client shares.
    [Fact]
    public void A_battle_first_seen_under_way_drops_its_start_once_the_deadline_moves()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0);
        var changed = tracker.Apply(
            [Tower(DynamicEventPhase.Battle, deadline: Deadline + BossExtension)],
            [], T0 + TimeSpan.FromMinutes(6));

        var state = Single(tracker, 48);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Null(state.SinceUtc);

        // Dropping the start withdraws a timestamp; only a status change is reported as
        // changed, so this rides out on the next heartbeat rather than prompting an upload.
        Assert.False(changed);
    }

    // Once dropped it stays dropped: a later extension must not look like a fresh anchor.
    [Fact]
    public void A_dropped_start_is_not_re_derived_by_a_later_extension()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0);
        tracker.Apply(
            [Tower(DynamicEventPhase.Battle, deadline: Deadline + BossExtension)],
            [], T0 + TimeSpan.FromMinutes(6));
        tracker.Apply(
            [Tower(DynamicEventPhase.Battle, deadline: Deadline + (BossExtension * 2))],
            [], T0 + TimeSpan.FromMinutes(13));

        Assert.Null(Single(tracker, 48).SinceUtc);
    }

    // A CE's deadline never moves within a battle, so meeting one mid-fight still yields the
    // real start — the common case of zoning into an instance with a CE already up.
    [Fact]
    public void A_battle_first_seen_under_way_keeps_its_start_while_the_deadline_holds()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromMinutes(5));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - CeDuration), Single(tracker, 46).SinceUtc);
    }

    // The deadline can lag the phase by a tick or two while the instance re-syncs. Until a
    // start can be derived there is nothing to hold, so each tick tries again.
    [Fact]
    public void A_battle_start_is_derived_on_a_later_tick_when_the_deadline_had_not_synced()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle, deadline: 0)], [], T0);
        Assert.Null(Single(tracker, 46).SinceUtc);

        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - CeDuration), Single(tracker, 46).SinceUtc);
    }

    // Having watched the battle begin is what makes the derived start trustworthy, and that
    // fact has to outlive the unsynced ticks in between — otherwise the eventual anchor would
    // be treated as a mid-battle guess and thrown away at the first extension.
    [Fact]
    public void Watching_a_battle_begin_still_counts_after_ticks_with_no_deadline()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Warmup, deadline: WarmupDeadline)], [], T0);
        tracker.Apply([Tower(DynamicEventPhase.Battle, deadline: 0)], [], T0 + TimeSpan.FromSeconds(1));
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(2));
        tracker.Apply(
            [Tower(DynamicEventPhase.Battle, deadline: Deadline + BossExtension)],
            [], T0 + TimeSpan.FromMinutes(6));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline - TowerDuration), Single(tracker, 48).SinceUtc);
    }

    // The anchor belongs to one occurrence: the next battle on the same id derives its own.
    [Fact]
    public void A_later_battle_on_the_same_id_derives_a_fresh_start()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromMinutes(20));
        tracker.Apply(
            [Ce(46, DynamicEventPhase.Battle, deadline: Deadline + 9000)], [], T0 + TimeSpan.FromHours(2));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(Deadline + 9000 - CeDuration), Single(tracker, 46).SinceUtc);
    }

    // Watching an id leave Battle and come back is watching the second battle begin, so its
    // start is anchored as firmly as one taken at a Warmup→Battle flip and held the same way.
    [Fact]
    public void A_battle_that_re_popped_under_observation_holds_its_start()
    {
        const long SecondDeadline = Deadline + 9000;
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromMinutes(20));
        tracker.Apply([Ce(46, DynamicEventPhase.Battle, deadline: SecondDeadline)], [], T0 + TimeSpan.FromHours(2));
        tracker.Apply(
            [Ce(46, DynamicEventPhase.Battle, deadline: SecondDeadline + BossExtension)],
            [], T0 + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(6));

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(SecondDeadline - CeDuration), Single(tracker, 46).SinceUtc);
    }

    // An id the container drops takes its history with it, so the battle still running when it
    // returns is one this tracker did not watch begin — and its start is provisional again.
    [Fact]
    public void A_battle_whose_id_left_and_returned_is_treated_as_first_seen_under_way()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Tower(DynamicEventPhase.Warmup, deadline: WarmupDeadline)], [], T0);
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(1));
        tracker.Apply([], [], T0 + TimeSpan.FromSeconds(2));
        tracker.Apply([Tower(DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(3));
        tracker.Apply(
            [Tower(DynamicEventPhase.Battle, deadline: Deadline + BossExtension)],
            [], T0 + TimeSpan.FromMinutes(6));

        Assert.Null(Single(tracker, 48).SinceUtc);
    }

    // --- The observed down flip ------------------------------------------------------------

    // Battle→Inactive is the "CE just ended" moment (the respawn clock start). The observation
    // time is the only timestamp that exists — everything in the container is zero by then.
    [Fact]
    public void An_observed_battle_to_inactive_flip_stamps_the_observation_time()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(30));

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Down, state.Status);
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), state.SinceUtc);
    }

    // The stamp records when the flip was SEEN. Re-stamping every tick would walk the timestamp
    // forward forever and the derived respawn countdown along with it.
    [Fact]
    public void The_down_stamp_sticks_across_later_ticks()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(30));
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromMinutes(10));

        Assert.Equal(T0 + TimeSpan.FromSeconds(30), Single(tracker, 46).SinceUtc);
    }

    // A registration window can close without filling; that end is observable the same way.
    [Fact]
    public void A_preparing_to_inactive_flip_also_stamps_the_observation_time()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Register)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(10));

        Assert.Equal(T0 + TimeSpan.FromSeconds(10), Single(tracker, 46).SinceUtc);
    }

    [Fact]
    public void A_ce_popping_again_clears_its_down_stamp()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(30));
        tracker.Apply([Ce(46, DynamicEventPhase.Register, deadline: Deadline + 9000)], [], T0 + TimeSpan.FromHours(2));

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Preparing, state.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Deadline + 9000), state.SinceUtc);
    }

    // --- Second-exact discipline -----------------------------------------------------------

    // The server floors to whole seconds before fingerprinting; agreeing client-side keeps the
    // stored value identical to what other reporters derive.
    [Fact]
    public void Observation_stamps_are_truncated_to_whole_seconds()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply(
            [Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [],
            T0 + TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(789));

        Assert.Equal(T0 + TimeSpan.FromSeconds(30), Single(tracker, 46).SinceUtc);
    }

    // A non-idle slot whose deadline has not synced yet must not invent a timestamp.
    [Fact]
    public void A_non_idle_ce_with_a_zero_deadline_has_a_null_sinceUtc()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle, deadline: 0)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Null(state.SinceUtc);
    }

    // A torn read of the raw duration would push the derived battle start PAST the deadline —
    // a plausible-looking epoch no other observer derives, worse than no timestamp at all.
    [Fact]
    public void A_battling_ce_with_a_nonpositive_duration_has_a_null_sinceUtc()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle, duration: 0)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Null(state.SinceUtc);
    }

    // Raw game memory can hold garbage during zone transitions. An impossible epoch must
    // degrade to "no timestamp", never throw out of the framework-tick handler.
    [Fact]
    public void An_out_of_range_deadline_yields_a_null_sinceUtc_instead_of_throwing()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle, deadline: long.MaxValue)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Null(state.SinceUtc);
    }

    // A phase byte outside the known enum (a future game patch could add one) reads as
    // down — "not verifiably up" is the only safe interpretation of an unknown state.
    [Fact]
    public void An_unknown_phase_byte_reads_as_down()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, (DynamicEventPhase)7, deadline: 0)], [], T0);

        var state = Single(tracker, 46);
        Assert.Equal(OccultEncounterStatus.Down, state.Status);
        Assert.Null(state.SinceUtc);
    }

    // Two readings for one id would put contradictory rows in the report; the first wins.
    [Fact]
    public void Duplicate_ce_readings_keep_only_the_first()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply(
            [Ce(46, DynamicEventPhase.Register), Ce(46, DynamicEventPhase.Battle)], [], T0);

        Assert.Equal(OccultEncounterStatus.Preparing, Single(tracker, 46).Status);
    }

    // --- FATEs -----------------------------------------------------------------------------

    [Fact]
    public void An_initialized_fate_is_active_with_its_true_start_epoch()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1972, 1786463208)], T0);

        var state = Single(tracker, 1972);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786463208), state.SinceUtc);
    }

    // A FATE can sit on the table for a few seconds before the server syncs it (zero epoch,
    // zero position). Reporting it would upload a bogus fingerprint pair.
    [Fact]
    public void A_pre_init_fate_is_not_reported_at_all()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1963, startEpoch: 0)], T0);

        Assert.Empty(tracker.Current);
    }

    // The mirror case: a TRACKED fate whose row momentarily reads unsynced counts as still
    // present. Without this, one bad read would flap it down and back up — two spurious
    // uploads and a bogus down stamp.
    [Fact]
    public void A_transient_pre_init_reading_keeps_a_tracked_fate_active()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1972, 1786463208)], T0);

        Assert.False(tracker.Apply([], [Fate(1972, startEpoch: 0)], T0 + TimeSpan.FromSeconds(1)));

        var state = Single(tracker, 1972);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786463208), state.SinceUtc);
    }

    // A new start epoch on a still-active id means a despawn and respawn landed between two
    // polls. The epoch is a fingerprint value, so the change must upload promptly.
    [Fact]
    public void A_fate_with_a_new_epoch_while_still_active_is_a_change()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1972, 1786463208)], T0);

        Assert.True(tracker.Apply([], [Fate(1972, 1786470000)], T0 + TimeSpan.FromSeconds(1)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786470000), Single(tracker, 1972).SinceUtc);
    }

    [Fact]
    public void A_fate_leaving_the_table_goes_down_stamped_at_observation_time()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1972, 1786463208)], T0);
        tracker.Apply([], [], T0 + TimeSpan.FromSeconds(45));

        var state = Single(tracker, 1972);
        Assert.Equal(OccultEncounterStatus.Down, state.Status);
        Assert.Equal(T0 + TimeSpan.FromSeconds(45), state.SinceUtc);
    }

    [Fact]
    public void A_fate_respawning_later_is_active_again_with_its_new_epoch()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1972, 1786463208)], T0);
        tracker.Apply([], [], T0 + TimeSpan.FromSeconds(45));
        tracker.Apply([], [Fate(1972, 1786470000)], T0 + TimeSpan.FromHours(1));

        var state = Single(tracker, 1972);
        Assert.Equal(OccultEncounterStatus.Active, state.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786470000), state.SinceUtc);
    }

    // A downed FATE stays in the report: the server needs the down fact (and its stamp) even
    // if this reporter uploads minutes later. Only Reset forgets it. The ghost's continued
    // absence is old news, so it must not keep registering as a change either.
    [Fact]
    public void A_downed_fate_stays_reported_on_later_ticks()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [Fate(1972, 1786463208)], T0);
        tracker.Apply([], [], T0 + TimeSpan.FromSeconds(45));

        Assert.False(tracker.Apply([], [], T0 + TimeSpan.FromMinutes(5)));

        var state = Single(tracker, 1972);
        Assert.Equal(OccultEncounterStatus.Down, state.Status);
        Assert.Equal(T0 + TimeSpan.FromSeconds(45), state.SinceUtc);
    }

    // --- Change detection ------------------------------------------------------------------

    [Fact]
    public void The_first_reading_counts_as_a_status_change()
    {
        var tracker = new OccultEncounterTracker();
        Assert.True(tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0));
    }

    [Fact]
    public void An_unchanged_tick_is_not_a_status_change()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Register)], [Fate(1972, 1786463208)], T0);

        Assert.False(tracker.Apply(
            [Ce(46, DynamicEventPhase.Register)], [Fate(1972, 1786463208)], T0 + TimeSpan.FromSeconds(1)));
    }

    // Register→Warmup keeps the wire status at 'preparing'; the deadline shift alone must not
    // trigger an upload (the next heartbeat carries it).
    [Fact]
    public void A_phase_shift_within_preparing_is_not_a_status_change()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Register)], [], T0);

        Assert.False(tracker.Apply(
            [Ce(46, DynamicEventPhase.Warmup, deadline: Deadline + 11)], [], T0 + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void A_ce_going_from_preparing_to_active_is_a_status_change()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Warmup)], [], T0);

        Assert.True(tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0 + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_new_initialized_fate_is_a_status_change()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [], T0);

        Assert.True(tracker.Apply([], [Fate(1972, 1786463208)], T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_pre_init_fate_appearing_is_not_a_status_change()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([], [], T0);

        Assert.False(tracker.Apply([], [Fate(1963, startEpoch: 0)], T0 + TimeSpan.FromSeconds(1)));
    }

    // --- Reset (leaving the instance) ------------------------------------------------------

    [Fact]
    public void Reset_forgets_everything()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [Fate(1972, 1786463208)], T0);

        tracker.Reset();

        Assert.Empty(tracker.Current);
        // The next instance's first reading registers as a change again.
        Assert.True(tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromHours(3)));
    }

    // Down stamps belong to the instance they were observed in. After a reset (new instance),
    // an idle CE must read as unknown, not inherit the old instance's stamp.
    [Fact]
    public void Reset_clears_down_stamps_from_the_previous_instance()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(30));

        tracker.Reset();
        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromHours(3));

        Assert.Null(Single(tracker, 46).SinceUtc);
    }

    // --- Report composition ----------------------------------------------------------------

    [Fact]
    public void The_report_marks_fates_and_ces_distinctly()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(48, DynamicEventPhase.Inactive, deadline: 0)], [Fate(1972, 1786463208)], T0);

        Assert.False(Single(tracker, 48).IsFate);
        Assert.True(Single(tracker, 1972).IsFate);
    }

    [Fact]
    public void Every_ce_slot_reported_by_the_game_is_present_in_the_report()
    {
        var tracker = new OccultEncounterTracker();
        var slots = Enumerable.Range(33, 16)
            .Select(id => Ce((ushort)id, DynamicEventPhase.Inactive, deadline: 0))
            .ToList();
        tracker.Apply(slots, [], T0);

        Assert.Equal(16, tracker.Current.Count(s => !s.IsFate));
    }

    // The container's id set is fixed for an instance's life, so an id vanishing is a real
    // shape change the server should hear about promptly.
    [Fact]
    public void Losing_a_ce_id_is_a_change_and_drops_it_from_the_report()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply(
            [Ce(46, DynamicEventPhase.Inactive, deadline: 0), Ce(47, DynamicEventPhase.Inactive, deadline: 0)],
            [], T0);

        Assert.True(tracker.Apply(
            [Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(tracker.Current, s => s.Id == 47);
    }

    [Fact]
    public void A_dropped_ce_id_reappearing_reads_as_a_first_sighting()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(47, DynamicEventPhase.Battle)], [], T0);
        tracker.Apply([], [], T0 + TimeSpan.FromSeconds(1));

        // Its history went with it: no down stamp survives the gap.
        Assert.True(tracker.Apply(
            [Ce(47, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(2)));
        Assert.Null(Single(tracker, 47).SinceUtc);
    }

    // An uploader may capture Current and serialize on a background task while framework
    // ticks keep landing. The captured reference must stay a consistent picture.
    [Fact]
    public void The_report_is_a_stable_snapshot_unaffected_by_later_ticks()
    {
        var tracker = new OccultEncounterTracker();
        tracker.Apply([Ce(46, DynamicEventPhase.Battle)], [], T0);
        var captured = tracker.Current;

        tracker.Apply([Ce(46, DynamicEventPhase.Inactive, deadline: 0)], [], T0 + TimeSpan.FromSeconds(30));

        Assert.Equal(OccultEncounterStatus.Active, Assert.Single(captured, s => s.Id == 46).Status);
        Assert.Equal(OccultEncounterStatus.Down, Single(tracker, 46).Status);
    }
}
