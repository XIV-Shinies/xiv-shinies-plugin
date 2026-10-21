using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// A reading of the familiar that is out, from the game's record of the player's own pet, named by its
/// bestiary row.
/// </summary>
/// <param name="PetId">The familiar's <c>XBMPet</c> row id (see <see cref="CrucibleFamiliarLinks"/>).</param>
/// <param name="CurrentHp">Its current HP.</param>
/// <param name="MaxHp">Its maximum HP, feeds included.</param>
// A `readonly record struct` is a small immutable value compared field by field, so two readings
// with the same familiar and HP are equal, which is how a change in HP is spotted.
public readonly record struct CrucibleSummoned(uint PetId, int CurrentHp, int MaxHp);

/// <summary>One familiar's last reading while it was out.</summary>
/// <param name="PetId">The familiar's <c>XBMPet</c> row id.</param>
/// <param name="CurrentHp">Its current HP at that reading.</param>
/// <param name="MaxHp">Its maximum HP at that reading.</param>
/// <param name="Summoned">Whether it is still out.</param>
/// <param name="ReadAt">When that reading was taken.</param>
// A positional `record` is an immutable data type declared by its parameter list alone: each
// parameter becomes a read-only property. `sealed` means nothing can subclass it.
public sealed record CrucibleFieldedFamiliar(
    uint PetId, int CurrentHp, int MaxHp, bool Summoned, DateTimeOffset ReadAt);

/// <summary>
/// Follows the familiar that is out through a run and decides when a snapshot of its HP is handed
/// out: once a fight is over, for every familiar that was out during it, and outside a fight, when
/// the familiar that is out changes HP.
/// </summary>
/// <remarks>
/// <para>
/// The game keeps one record of the player's pet, holding only the familiar that is out. More than
/// one can be called out during a fight, and it can end with none out, so each familiar's last
/// reading is kept: one that leaves the field keeps its HP as it left, and one still out at the end
/// has its HP then. Nobody enters anything on the website mid-fight, so the tracker hands out nothing
/// until it is over.
/// </para>
/// <para>
/// The record drops a familiar that goes down the same way it drops one called back or swapped out,
/// holding the HP of the game's last three-second update rather than 0, so a familiar no longer out
/// is never taken to be down: a down is the team window's 0 HP.
/// </para>
/// </remarks>
// A class (not a record) because it holds changing state; `sealed` means nothing can subclass it. It
// reads neither the game nor the clock (the caller hands in each reading and its moment), and only the
// game's main thread calls it.
public sealed class CrucibleFamiliarTracker
{
    /// <summary>The familiars out during the fight in progress, in the order they first came out.</summary>
    // `List<T>` is a growable array, like a TypeScript `T[]`; `[]` starts it empty.
    private readonly List<CrucibleFieldedFamiliar> fight = [];

    /// <summary>The previous reading, or null when no familiar was out then.</summary>
    // The `?` lets a value type such as this struct also hold null, like `CrucibleSummoned | null`.
    private CrucibleSummoned? previous;

    /// <summary>True from the first reading in combat until the first reading after it.</summary>
    private bool fighting;

    /// <summary>
    /// Takes in one reading of the familiar that is out, and returns the snapshot it calls for, if any.
    /// </summary>
    /// <param name="summoned">The familiar that is out, or null when none is.</param>
    /// <param name="inCombat">Whether the game says the player is in combat.</param>
    /// <param name="now">The moment of the reading.</param>
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
        // `is { } x` names the value when there is one; `!=` compares the two readings field by field.
        if (summoned is { } current && before is { } last && last.PetId == current.PetId && last != current)
        {
            // An array of one, written as a collection expression; `new(...)` builds the array's
            // element type, and `Summoned: true` names the argument it fills.
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
        // `?.` reads through a possibly-null value, like optional chaining: null when none is out.
        var outId = summoned?.PetId;
        for (var index = 0; index < fight.Count; index++)
        {
            // `with { ... }` copies the record with the named property changed, like
            // `{ ...reading, summoned: false }`; the copy replaces the entry.
            if (fight[index].Summoned && fight[index].PetId != outId)
                fight[index] = fight[index] with { Summoned = false };
        }

        if (summoned is not { } current)
            return;

        var reading = new CrucibleFieldedFamiliar(
            current.PetId, current.CurrentHp, current.MaxHp, Summoned: true, now);

        // `FindIndex` returns the position of the first entry the test matches, or -1, like
        // JavaScript's `findIndex`.
        var existing = fight.FindIndex(familiar => familiar.PetId == current.PetId);
        if (existing < 0)
            fight.Add(reading);
        else
            fight[existing] = reading;
    }
}
