using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// A reading of the familiar that is out, from the game's record of the player's own pet, named by its
/// bestiary row.
/// </summary>
/// <param name="PetId">The familiar's <c>XBMPet</c> row id (see <see cref="CrucibleFamiliarLinks"/>).</param>
/// <param name="MaxHp">Its maximum HP, feeds included.</param>
// Two readings with the same familiar and HP are equal, which is how a change in HP is spotted.
public readonly record struct CrucibleSummoned(uint PetId, int CurrentHp, int MaxHp);

/// <summary>One familiar's last reading while it was out.</summary>
/// <param name="Summoned">Whether it is still out.</param>
public sealed record CrucibleFieldedFamiliar(
    uint PetId, int CurrentHp, int MaxHp, bool Summoned, DateTimeOffset ReadAt);

/// <summary>
/// Decides when a snapshot of familiar HP is handed out: after a fight, for every familiar out during
/// it, and outside a fight, when the familiar that is out changes HP.
/// </summary>
/// <remarks>
/// The game's record of the player's pet holds only the familiar that is out, and a fight can call out
/// several and end with none out, so each one's last reading is kept; nobody enters anything on the
/// website mid-fight, so nothing goes up until the fight is over. The record drops a familiar that goes
/// down just as it drops one called back or swapped out, holding the HP of the game's last three-second
/// update rather than 0, so one no longer out is never taken to be down: a down is the team window's
/// 0 HP. It reads neither the game nor the clock, and only the game's main thread calls it.
/// </remarks>
public sealed class CrucibleFamiliarTracker
{
    /// <summary>The familiars out during the fight in progress, in the order they first came out.</summary>
    private readonly List<CrucibleFieldedFamiliar> fight = [];

    /// <summary>The previous reading, or null when no familiar was out then.</summary>
    private CrucibleSummoned? previous;

    /// <summary>True from the first reading in combat until the first reading after it.</summary>
    private bool fighting;

    /// <summary>
    /// Takes in one reading of the familiar that is out, and returns the snapshot it calls for, if any.
    /// </summary>
    /// <param name="summoned">The familiar that is out, or null when none is.</param>
    /// <returns>
    /// A familiars snapshot on the first reading after a fight in which a familiar was out, or on a
    /// change in the HP of the familiar that is out outside a fight; otherwise null.
    /// </returns>
    /// <remarks>
    /// An HP change outside a fight comes from an item used while the familiar is out, or from the
    /// record's update landing just after a fight. Calling one out hands out nothing, since the team
    /// window already showed its HP.
    /// </remarks>
    public CrucibleObservation? Observe(CrucibleSummoned? summoned, bool inCombat, DateTimeOffset now)
    {
        var before = previous;
        previous = summoned;

        // A fight starts its own list, with the familiar out as it begins.
        if (inCombat && !fighting)
        {
            fighting = true;
            fight.Clear();
        }

        if (fighting)
        {
            Record(summoned, now);
            if (inCombat)
                return null;

            // The first reading after the fight: what it did to each familiar goes up.
            fighting = false;
            var ended = fight.Count == 0 ? null : CrucibleUploadBuilder.Familiars(true, fight, now);
            fight.Clear();
            return ended;
        }

        // Outside a fight, only the familiar that stayed out and changed HP has anything new to say.
        if (summoned is { } current && before is { } last && last.PetId == current.PetId && last != current)
        {
            CrucibleFieldedFamiliar[] changed =
                [new(current.PetId, current.CurrentHp, current.MaxHp, Summoned: true, now)];
            return CrucibleUploadBuilder.Familiars(false, changed, now);
        }

        return null;
    }

    /// <summary>Forgets the fight in progress and the previous reading.</summary>
    public void Reset()
    {
        fight.Clear();
        previous = null;
        fighting = false;
    }

    /// <summary>
    /// Brings the fight's list up to date: the familiar out takes this reading, and every other one is
    /// marked as no longer out.
    /// </summary>
    private void Record(CrucibleSummoned? summoned, DateTimeOffset now)
    {
        var outId = summoned?.PetId;
        for (var index = 0; index < fight.Count; index++)
        {
            if (fight[index].Summoned && fight[index].PetId != outId)
                fight[index] = fight[index] with { Summoned = false };
        }

        if (summoned is not { } current)
            return;

        var reading = new CrucibleFieldedFamiliar(
            current.PetId, current.CurrentHp, current.MaxHp, Summoned: true, now);

        var existing = fight.FindIndex(familiar => familiar.PetId == current.PetId);
        if (existing < 0)
            fight.Add(reading);
        else
            fight[existing] = reading;
    }
}
