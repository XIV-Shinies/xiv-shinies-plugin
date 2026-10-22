using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Which familiar each <c>Pet</c> sheet row belongs to, from the link the game's <c>XBMPet</c> sheet
/// holds for each familiar.
/// </summary>
/// <remarks>
/// The game's record of the player's pet names the familiar that is out by its <c>Pet</c> row, while
/// every other Crucible snapshot names a familiar by its <c>XBMPet</c> row, its bestiary number. This
/// turns one into the other, so the website gets the id it already knows. Built once from the sheet
/// (see <see cref="CrucibleFamiliarLinkSheet"/>); it reads nothing itself.
/// </remarks>
public sealed class CrucibleFamiliarLinks
{
    private readonly Dictionary<uint, uint> petIdByPetRow;

    // Private, so a map is only ever made by From, which checks the column first.
    private CrucibleFamiliarLinks(Dictionary<uint, uint> petIdByPetRow)
    {
        this.petIdByPetRow = petIdByPetRow;
    }

    /// <summary>
    /// Builds the map from each familiar's row and the <c>Pet</c> row it links, or returns null when
    /// the column read cannot be the link.
    /// </summary>
    /// <param name="rows">
    /// Each <c>XBMPet</c> row id with the value of its <c>Pet</c> link column, widened to a
    /// <c>long</c> so a column of any whole-number type can be handed in as it is.
    /// </param>
    /// <returns>
    /// Null when no familiar links a <c>Pet</c> row, or when two link the same one. Each familiar has
    /// its own pet, so either means the column read is not the link, and nothing is mapped rather than
    /// sending ids taken from the wrong column.
    /// </returns>
    public static CrucibleFamiliarLinks? From(IEnumerable<(uint PetId, long PetRow)> rows)
    {
        var map = new Dictionary<uint, uint>();

        foreach (var (petId, petRow) in rows)
        {
            // Row 0 is the sheet's blank padding. A link of 0, or one no row id can be (negative, or
            // past the largest `uint`), is an empty one.
            if (petId == 0 || petRow is <= 0 or > uint.MaxValue)
                continue;

            if (!map.TryAdd((uint)petRow, petId))
                return null;
        }

        return map.Count == 0 ? null : new CrucibleFamiliarLinks(map);
    }

    /// <summary>The familiar whose pet is the given <c>Pet</c> row, or null when none is.</summary>
    public uint? PetIdFor(uint petRow) =>
        petIdByPetRow.TryGetValue(petRow, out var petId) ? petId : null;

    /// <summary>
    /// The familiar that is out, from the three figures the game's record of the player's pet holds,
    /// or null when the record holds no reading of one of the Crucible's familiars.
    /// </summary>
    /// <param name="petRow">The record's data id: the pet's <c>Pet</c> row.</param>
    /// <remarks>
    /// A record with no data id or no maximum HP is an empty one, as when no familiar is out. One at
    /// 0 HP is left out, since this record never reports a down (see
    /// <see cref="CrucibleFamiliarTracker"/>), and so is a current HP above the maximum, which the game
    /// never draws, or a maximum past the largest <c>int</c>, which cannot be sent. A pet no familiar
    /// links is not one of the Crucible's familiars, and is left out too.
    /// </remarks>
    public CrucibleSummoned? Summoned(uint petRow, uint currentHp, uint maxHp)
    {
        if (petRow == 0 || currentHp == 0 || maxHp == 0 || currentHp > maxHp || maxHp > int.MaxValue)
            return null;

        if (PetIdFor(petRow) is not { } petId)
            return null;

        // Both figures were checked above to fit an `int`.
        return new CrucibleSummoned(petId, (int)currentHp, (int)maxHp);
    }
}
