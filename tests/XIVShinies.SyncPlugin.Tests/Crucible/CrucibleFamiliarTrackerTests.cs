using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The familiars snapshot: the familiar that is out is read throughout a run, fights included, and what
// a fight did to each familiar goes up once the fight is over. The tracker is handed each moment, so
// every test sets the time explicitly. Each `[Fact]` is one test, like `it(...)` in Jest; `inCombat:`
// names the argument it fills, so each call says what its true or false means.
public class CrucibleFamiliarTrackerTests
{
    // `static readonly` is a value built once and never reassigned; a `DateTimeOffset` is a moment
    // with its offset from UTC.
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    /// <summary>A moment some seconds after <see cref="T0"/>.</summary>
    // `=>` makes the expression after it the whole method body, like an arrow function.
    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    /// <summary>A reading of the familiar that is out, as the tracker is handed it.</summary>
    // `new(petId, current, max)` builds a CrucibleSummoned; the type comes from the method's return type.
    private static CrucibleSummoned Out(uint petId, int current, int max) => new(petId, current, max);

    /// <summary>The snapshot as its own kind, failing the test when nothing was sent.</summary>
    // `Assert.IsType<T>` fails unless the value is a T, and hands it back typed as one.
    private static CrucibleFamiliarsObservation Sent(CrucibleObservation? observation) =>
        Assert.IsType<CrucibleFamiliarsObservation>(observation);

    // Nothing is handed out while the fight lasts, whatever the familiars do.
    [Fact]
    public void Nothing_is_sent_during_a_fight()
    {
        var tracker = new CrucibleFamiliarTracker();

        Assert.Null(tracker.Observe(Out(13, 560, 560), inCombat: true, At(0)));
        Assert.Null(tracker.Observe(Out(13, 400, 560), inCombat: true, At(3)));
        Assert.Null(tracker.Observe(null, inCombat: true, At(6)));
        Assert.Null(tracker.Observe(Out(34, 589, 589), inCombat: true, At(9)));
    }

    // A familiar still out when the fight ends goes up with its HP at the end, marked summoned. The
    // `u` in `13u` makes the number a `uint`, the type of a familiar's id.
    [Fact]
    public void A_familiar_out_at_the_end_of_a_fight_goes_up_with_its_final_hp()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 560, 560), inCombat: true, At(0));
        tracker.Observe(Out(13, 400, 560), inCombat: true, At(3));

        var sent = Sent(tracker.Observe(Out(13, 358, 560), inCombat: false, At(6)));

        Assert.True(sent.AfterFight);
        Assert.Equal("2026-10-21T18:00:06Z", sent.ObservedAtUtc);

        // `Assert.Single` fails unless the list holds exactly one entry, and hands that entry back.
        var familiar = Assert.Single(sent.Familiars);
        Assert.Equal(13u, familiar.PetId);
        Assert.Equal(358, familiar.Hp.Current);
        Assert.Equal(560, familiar.Hp.Max);
        Assert.True(familiar.Summoned);
        Assert.Equal("2026-10-21T18:00:06Z", familiar.ReadAtUtc);
    }

    // One that left mid-fight keeps the HP and moment of its last reading while it was out, and goes
    // up as no longer summoned, in the order the familiars came out.
    [Fact]
    public void A_familiar_that_left_during_the_fight_keeps_its_hp_from_when_it_left()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(1, 608, 608), inCombat: true, At(0));
        tracker.Observe(Out(1, 386, 608), inCombat: true, At(50));
        tracker.Observe(null, inCombat: true, At(51));
        tracker.Observe(Out(13, 560, 560), inCombat: true, At(52));

        var sent = Sent(tracker.Observe(Out(13, 560, 560), inCombat: false, At(80)));

        Assert.Equal(2, sent.Familiars.Count);
        Assert.Equal(1u, sent.Familiars[0].PetId);
        Assert.Equal(386, sent.Familiars[0].Hp.Current);
        Assert.False(sent.Familiars[0].Summoned);
        Assert.Equal("2026-10-21T18:00:50Z", sent.Familiars[0].ReadAtUtc);
        Assert.Equal(13u, sent.Familiars[1].PetId);
        Assert.True(sent.Familiars[1].Summoned);
    }

    // A familiar called back in after leaving is one entry, carrying its latest reading.
    [Fact]
    public void A_familiar_called_back_in_is_listed_once()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(1, 500, 608), inCombat: true, At(0));
        tracker.Observe(Out(13, 560, 560), inCombat: true, At(10));
        tracker.Observe(Out(1, 300, 608), inCombat: true, At(20));

        var sent = Sent(tracker.Observe(Out(1, 280, 608), inCombat: false, At(30)));

        Assert.Equal(2, sent.Familiars.Count);
        Assert.Equal(1u, sent.Familiars[0].PetId);
        Assert.Equal(280, sent.Familiars[0].Hp.Current);
        Assert.True(sent.Familiars[0].Summoned);
        Assert.Equal(13u, sent.Familiars[1].PetId);
        Assert.False(sent.Familiars[1].Summoned);
    }

    // The familiar out as the fight starts belongs to it; one that was out only before it does not.
    [Fact]
    public void Only_familiars_out_during_the_fight_are_listed()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(1, 608, 608), inCombat: false, At(0));
        tracker.Observe(Out(13, 560, 560), inCombat: false, At(1));
        tracker.Observe(Out(13, 560, 560), inCombat: true, At(2));

        var sent = Sent(tracker.Observe(Out(13, 500, 560), inCombat: false, At(30)));

        Assert.Equal(13u, Assert.Single(sent.Familiars).PetId);
    }

    // A fight with no familiar out has nothing to say about familiars.
    [Fact]
    public void A_fight_with_no_familiar_out_sends_nothing()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(null, inCombat: true, At(0));

        Assert.Null(tracker.Observe(null, inCombat: false, At(30)));
    }

    // A fight that ends with no familiar out still sends the ones that were out during it, none of
    // them summoned.
    [Fact]
    public void A_fight_that_ends_with_no_familiar_out_sends_the_ones_that_were()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(1, 400, 608), inCombat: true, At(0));

        var sent = Sent(tracker.Observe(null, inCombat: false, At(30)));

        var familiar = Assert.Single(sent.Familiars);
        Assert.Equal(1u, familiar.PetId);
        Assert.Equal(400, familiar.Hp.Current);
        Assert.False(familiar.Summoned);
        Assert.Equal("2026-10-21T18:00:00Z", familiar.ReadAtUtc);
    }

    // Each fight starts its own list: a familiar from the fight before is not carried into the next.
    [Fact]
    public void Each_fight_lists_only_its_own_familiars()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(1, 608, 608), inCombat: true, At(0));
        tracker.Observe(null, inCombat: false, At(30));
        tracker.Observe(Out(13, 560, 560), inCombat: true, At(60));

        var sent = Sent(tracker.Observe(Out(13, 560, 560), inCombat: false, At(90)));

        Assert.Equal(13u, Assert.Single(sent.Familiars).PetId);
    }

    // A change in HP outside a fight sends the familiar that is out, with its new HP.
    [Fact]
    public void An_hp_change_outside_a_fight_sends_the_familiar_that_is_out()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 300, 560), inCombat: false, At(0));

        var sent = Sent(tracker.Observe(Out(13, 560, 560), inCombat: false, At(3)));

        Assert.False(sent.AfterFight);
        var familiar = Assert.Single(sent.Familiars);
        Assert.Equal(13u, familiar.PetId);
        Assert.Equal(560, familiar.Hp.Current);
        Assert.True(familiar.Summoned);
        Assert.Equal("2026-10-21T18:00:03Z", familiar.ReadAtUtc);
    }

    // A change in maximum HP alone goes up too.
    [Fact]
    public void A_change_in_maximum_hp_outside_a_fight_is_sent()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 560, 560), inCombat: false, At(0));

        var sent = Sent(tracker.Observe(Out(13, 560, 728), inCombat: false, At(3)));

        Assert.Equal(728, Assert.Single(sent.Familiars).Hp.Max);
    }

    // Calling a familiar out tells the website nothing the team window has not, and an unchanged
    // reading tells it nothing at all.
    [Fact]
    public void Calling_a_familiar_out_or_reading_it_unchanged_sends_nothing()
    {
        var tracker = new CrucibleFamiliarTracker();

        Assert.Null(tracker.Observe(Out(13, 300, 560), inCombat: false, At(0)));
        Assert.Null(tracker.Observe(Out(13, 300, 560), inCombat: false, At(1)));
        Assert.Null(tracker.Observe(Out(1, 608, 608), inCombat: false, At(2)));
        Assert.Null(tracker.Observe(null, inCombat: false, At(3)));
        Assert.Null(tracker.Observe(Out(1, 400, 608), inCombat: false, At(4)));
    }

    // The HP a fight ended on can land on the record a moment after the fight is over, and that
    // correction goes up too.
    [Fact]
    public void A_change_just_after_a_fight_goes_up_as_a_change_outside_it()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 400, 560), inCombat: true, At(0));
        Sent(tracker.Observe(Out(13, 400, 560), inCombat: false, At(10)));

        var sent = Sent(tracker.Observe(Out(13, 380, 560), inCombat: false, At(11)));

        Assert.False(sent.AfterFight);
        Assert.Equal(380, Assert.Single(sent.Familiars).Hp.Current);
    }

    // A reset forgets a fight in progress, so a visit that ended mid-fight sends nothing for it.
    [Fact]
    public void A_reset_forgets_the_fight_in_progress()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 400, 560), inCombat: true, At(0));

        tracker.Reset();

        Assert.Null(tracker.Observe(Out(13, 400, 560), inCombat: false, At(10)));
    }

    // A reset forgets the previous reading too, so the first reading after it is a familiar called
    // out, not a change in HP.
    [Fact]
    public void A_reset_forgets_the_previous_reading()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 300, 560), inCombat: false, At(0));

        tracker.Reset();

        Assert.Null(tracker.Observe(Out(13, 560, 560), inCombat: false, At(3)));
    }
}
