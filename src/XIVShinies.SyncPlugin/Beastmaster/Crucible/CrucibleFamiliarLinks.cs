using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Which familiar each <c>Pet</c> sheet row belongs to, from the link the game's <c>XBMPet</c> sheet
/// holds for each familiar.
/// </summary>
/// <remarks>
/// The game's record of the player's pet names the familiar that is out by its <c>Pet</c> row. Every
/// other Crucible snapshot names a familiar by its <c>XBMPet</c> row, its bestiary number, and this is
/// what turns one into the other, so the website gets the id it already knows rather than one it would
/// have to translate. Built from the sheet once (see <see cref="CrucibleFamiliarLinkSheet"/>); it reads
/// nothing itself.
/// </remarks>
// A class (not a record) because what it holds is private and reached only through its methods;
// `sealed` means nothing can subclass it.
public sealed class CrucibleFamiliarLinks
{
    // `Dictionary<K, V>` is a key-to-value map, like a TypeScript `Map<K, V>`.
    private readonly Dictionary<uint, uint> petIdByPetRow;

    // A `private` constructor: a map is only ever made by From, which checks the column first.
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
    // `IEnumerable<(uint PetId, long PetRow)>` is any sequence of pairs, like an iterable of tuples;
    // a list or an array both qualify. `static` means it is called on the class itself,
    // `CrucibleFamiliarLinks.From(...)`, like a TypeScript static method.
    public static CrucibleFamiliarLinks? From(IEnumerable<(uint PetId, long PetRow)> rows)
    {
        var map = new Dictionary<uint, uint>();

        // `var (petId, petRow)` unpacks each pair into two variables, like
        // `for (const [petId, petRow] of rows)` in TypeScript.
        foreach (var (petId, petRow) in rows)
        {
            // Row 0 is the sheet's blank padding, and a link of 0, or one no row id can be (negative,
            // or past the largest `uint`), is an empty one. `is <= 0 or > uint.MaxValue` tests the
            // value against both ranges at once.
            if (petId == 0 || petRow is <= 0 or > uint.MaxValue)
                continue;

            // `TryAdd` adds the pair and returns false, adding nothing, when the key is already there.
            // `(uint)` narrows the checked value back to a row id.
            if (!map.TryAdd((uint)petRow, petId))
                return null;
        }

        // `a ? b : c` picks b when a is true, else c, as in TypeScript.
        return map.Count == 0 ? null : new CrucibleFamiliarLinks(map);
    }

    /// <summary>The familiar whose pet is the given <c>Pet</c> row, or null when none is.</summary>
    /// <param name="petRow">The <c>Pet</c> row the game's record of the player's pet names.</param>
    // `uint?` is a row id that may be null, like `number | null`. `TryGetValue` returns whether the key
    // is there and, when it is, writes its value into `petId` (`out var` declares that variable right
    // in the call).
    public uint? PetIdFor(uint petRow) =>
        petIdByPetRow.TryGetValue(petRow, out var petId) ? petId : null;

    /// <summary>
    /// The familiar that is out, from the three figures the game's record of the player's pet holds,
    /// or null when the record holds no reading of one of the Crucible's familiars.
    /// </summary>
    /// <param name="petRow">The record's data id: the pet's <c>Pet</c> row.</param>
    /// <param name="currentHp">The record's current HP.</param>
    /// <param name="maxHp">The record's maximum HP.</param>
    /// <remarks>
    /// <para>
    /// A record with no data id or no maximum HP is an empty one, as when no familiar is out. A record
    /// at 0 HP is left out too: this record never reports a down (see
    /// <see cref="CrucibleFamiliarTracker"/>), so it never sends a 0. A current HP above the maximum is
    /// no reading the game draws, and HP past the largest <c>int</c> cannot be sent as one.
    /// </para>
    /// <para>
    /// A pet no familiar links is not one of the Crucible's familiars, and is left out too.
    /// </para>
    /// </remarks>
    public CrucibleSummoned? Summoned(uint petRow, uint currentHp, uint maxHp)
    {
        if (petRow == 0 || currentHp == 0 || maxHp == 0 || currentHp > maxHp || maxHp > int.MaxValue)
            return null;

        // `is not { } petId` is true when the lookup found nothing; otherwise it names the familiar
        // `petId` for the lines below.
        if (PetIdFor(petRow) is not { } petId)
            return null;

        // `(int)` converts the record's unsigned figures, already checked to fit, to the signed numbers
        // a snapshot carries.
        return new CrucibleSummoned(petId, (int)currentHp, (int)maxHp);
    }
}
