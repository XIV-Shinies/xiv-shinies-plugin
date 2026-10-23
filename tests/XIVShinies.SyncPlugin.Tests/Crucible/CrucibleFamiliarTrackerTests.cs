using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The familiars snapshot: the familiar that is out is read throughout a run, fights included, and what
// a fight did to each familiar goes up once the fight is over.
public class CrucibleFamiliarTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static CrucibleSummoned Out(uint petId, int current, int max) => new(petId, current, max);

    /// <summary>The familiars snapshot, failing the test when nothing was sent.</summary>
    private static CrucibleFamiliarsObservation Sent(CrucibleObservation? observation) =>
        Assert.IsType<CrucibleFamiliarsObservation>(observation);

    [Fact]
    public void Nothing_is_sent_during_a_fight()
    {
        var tracker = new CrucibleFamiliarTracker();

        Assert.Null(tracker.Observe(Out(13, 560, 560), inCombat: true, At(0)));
        Assert.Null(tracker.Observe(Out(13, 400, 560), inCombat: true, At(3)));
        Assert.Null(tracker.Observe(null, inCombat: true, At(6)));
        Assert.Null(tracker.Observe(Out(34, 589, 589), inCombat: true, At(9)));
    }

    // A familiar still out goes up marked summoned.
    [Fact]
    public void A_familiar_out_at_the_end_of_a_fight_goes_up_with_its_final_hp()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 560, 560), inCombat: true, At(0));
        tracker.Observe(Out(13, 400, 560), inCombat: true, At(3));

        var sent = Sent(tracker.Observe(Out(13, 358, 560), inCombat: false, At(6)));

        Assert.True(sent.AfterFight);
        Assert.Equal("2026-10-21T18:00:06Z", sent.ObservedAtUtc);

        var familiar = Assert.Single(sent.Familiars);
        Assert.Equal(13u, familiar.PetId);
        Assert.Equal(358, familiar.Hp.Current);
        Assert.Equal(560, familiar.Hp.Max);
        Assert.True(familiar.Summoned);
        Assert.Equal("2026-10-21T18:00:06Z", familiar.ReadAtUtc);
    }

    // It keeps the moment of its last reading while out, too, and goes up not summoned. The list
    // follows the order the familiars came out.
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

    // Its one entry carries its latest reading.
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

    // The familiar out as the fight starts belongs to it; one out only before it does not.
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

    [Fact]
    public void A_fight_with_no_familiar_out_sends_nothing()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(null, inCombat: true, At(0));

        Assert.Null(tracker.Observe(null, inCombat: false, At(30)));
    }

    // None of them goes up summoned.
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

    // A familiar from the fight before is not carried into the next.
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

    // The maximum alone counts as a change.
    [Fact]
    public void A_change_in_maximum_hp_outside_a_fight_is_sent()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 560, 560), inCombat: false, At(0));

        var sent = Sent(tracker.Observe(Out(13, 560, 728), inCombat: false, At(3)));

        Assert.Equal(728, Assert.Single(sent.Familiars).Hp.Max);
    }

    // Calling a familiar out tells the website nothing the team window has not.
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

    // The HP a fight ended on can land on the record a moment after the fight is over.
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

    // A visit that ended mid-fight therefore sends nothing for that fight.
    [Fact]
    public void A_reset_forgets_the_fight_in_progress()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 400, 560), inCombat: true, At(0));

        tracker.Reset();

        Assert.Null(tracker.Observe(Out(13, 400, 560), inCombat: false, At(10)));
    }

    // The first reading after a reset is a familiar called out, not a change in HP.
    [Fact]
    public void A_reset_forgets_the_previous_reading()
    {
        var tracker = new CrucibleFamiliarTracker();
        tracker.Observe(Out(13, 300, 560), inCombat: false, At(0));

        tracker.Reset();

        Assert.Null(tracker.Observe(Out(13, 560, 560), inCombat: false, At(3)));
    }
}
