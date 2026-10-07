using System.Collections.Generic;
using System.Text.Json.Serialization;
// The Crucible wire enums: CrucibleTrigger and CrucibleOfferSource.
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The body of <c>POST /api/plugin/v1/crucible/observations</c>: snapshots of what the Crucible's
/// windows showed, each at one moment (docs/api-contract.md, crucible/observations).
/// </summary>
/// <remarks>
/// The server's schema is strict: a key it does not name fails the whole upload, and so does a
/// key it names that is missing. A key the contract shows as <c>null</c> is therefore written as
/// an explicit <c>null</c> (the <c>JsonIgnoreCondition.Never</c> properties below), where the
/// shared serializer would otherwise leave a null out.
/// </remarks>
// `sealed record` is an immutable data type nothing can subclass. Each `{ get; init; }`
// property can be set only while the object is being built, like a field of a frozen object
// literal, and `required` makes leaving it unset a compile error.
public sealed record CrucibleObservationsRequest
{
    /// <summary>SHA-256 of the character's ContentId, lowercase hex. The raw id never travels.</summary>
    public required string CharacterContentIdHash { get; init; }

    /// <summary>Sent for the server's character binding; a 403 echoes it back.</summary>
    public required string CharacterName { get; init; }

    /// <summary>The character's home world name.</summary>
    public required string HomeWorld { get; init; }

    /// <summary>This plugin's version string.</summary>
    public required string PluginVersion { get; init; }

    /// <summary>What prompted this upload (serialized as its lowercase word).</summary>
    public required CrucibleTrigger Trigger { get; init; }

    /// <summary>
    /// The TerritoryType row id the upload concerns: the board, the entrance for its two
    /// windows, or on a leave the board just left.
    /// </summary>
    // `uint` is a whole number that cannot be negative, which is how the game stores ids.
    public required uint TerritoryTypeId { get; init; }

    /// <summary>
    /// The snapshots, at most one per kind, in the order they were observed. Empty on a heartbeat
    /// or a leave, and still sent as an empty list.
    /// </summary>
    // `IReadOnlyList<T>` is a list its holder cannot change, like `readonly T[]` in TypeScript.
    public required IReadOnlyList<CrucibleObservation> Observations { get; init; }
}

/// <summary>
/// One snapshot: what a window showed, or the character's own HP. Each kind below is a record
/// that inherits this one.
/// </summary>
/// <remarks>
/// <c>JsonPolymorphic</c> and the six <c>JsonDerivedType</c> attributes make the serializer write
/// a <c>kind</c> key naming the record's type, as the first key of each snapshot, whenever it
/// serializes a value declared as this base type (the request's list, for one). It is the same
/// idea as a discriminated union in TypeScript, where a literal <c>kind</c> field says which shape
/// an object has.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CrucibleBoardObservation), "board")]
[JsonDerivedType(typeof(CrucibleTeamObservation), "team")]
[JsonDerivedType(typeof(CrucibleBagObservation), "bag")]
[JsonDerivedType(typeof(CrucibleOfferObservation), "offer")]
[JsonDerivedType(typeof(CrucibleResultsObservation), "results")]
[JsonDerivedType(typeof(CrucibleSelfObservation), "self")]
// `abstract` means no snapshot is ever just this base: every one is one of the six kinds.
public abstract record CrucibleObservation
{
    /// <summary>When this was observed, as second-exact UTC text (see <see cref="WireTime"/>).</summary>
    public required string ObservedAtUtc { get; init; }
}

/// <summary>The board window: every piece of the board with its progress.</summary>
// `: CrucibleObservation` makes this record inherit the base's members (like `extends`), so it
// has an `ObservedAtUtc` too.
public sealed record CrucibleBoardObservation : CrucibleObservation
{
    /// <summary>True on the last snapshot before the window closed.</summary>
    public required bool Closed { get; init; }

    /// <summary>The window's view: 0 before entering, 1 the whole board, 2 scoped to one piece.</summary>
    public required int View { get; init; }

    /// <summary>The piece being played, in the scoped view; null in the other views.</summary>
    // `uint?` is an id that may be null (`number | null`). With `required` the builder must still
    // set it, even if only to null, and `JsonIgnoreCondition.Never` writes that null into the JSON.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required uint? CurrentNodeIndex { get; init; }

    /// <summary>Every piece the window listed, in its order.</summary>
    public required IReadOnlyList<CruciblePieceUpload> Pieces { get; init; }
}

/// <summary>One piece of the board.</summary>
public sealed record CruciblePieceUpload
{
    /// <summary>The piece's node index, numbered from 1.</summary>
    public required uint NodeIndex { get; init; }

    /// <summary>0 not reached, 1 cleared, 2 the branch not taken.</summary>
    public required int Progress { get; init; }
}

/// <summary>The team window: the player's own familiars.</summary>
public sealed record CrucibleTeamObservation : CrucibleObservation
{
    /// <summary>True on the last snapshot before the window closed.</summary>
    public required bool Closed { get; init; }

    /// <summary>
    /// The window's mode: 0 roster pick, 1 browse, 2 lineup, 3 feed, 4 campsite, 5 Blessed Horn.
    /// </summary>
    public required int Mode { get; init; }

    /// <summary>The feed or horn being given, in modes 3 and 5; null in the others.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required uint? ItemId { get; init; }

    /// <summary>The familiars the window listed, in its order.</summary>
    public required IReadOnlyList<CrucibleFamiliarUpload> Familiars { get; init; }
}

/// <summary>One of the player's familiars in the team window.</summary>
public sealed record CrucibleFamiliarUpload
{
    /// <summary>The familiar's <c>XBMPet</c> row id.</summary>
    public required uint PetId { get; init; }

    /// <summary>Its lineup place, 0 to 2, or 3 when it is not in the lineup.</summary>
    public required int Place { get; init; }

    /// <summary>Its current and maximum HP.</summary>
    public required CrucibleHpUpload Hp { get; init; }

    /// <summary>Its rank as the window drew it.</summary>
    public required int Rank { get; init; }

    /// <summary>Whether the board has capped its rank.</summary>
    public required bool RankSynced { get; init; }

    /// <summary>The <c>XBMItem</c> ids of the feeds it has eaten, in order.</summary>
    public required IReadOnlyList<uint> FeedItemIds { get; init; }

    /// <summary>Whether it is picked to rest at a campsite.</summary>
    public required bool Resting { get; init; }
}

/// <summary>A current and maximum HP pair, for a familiar or for the character.</summary>
public sealed record CrucibleHpUpload
{
    /// <summary>The current HP.</summary>
    public required int Current { get; init; }

    /// <summary>The maximum HP.</summary>
    public required int Max { get; init; }
}

/// <summary>
/// The run HUD: tokens and what the player carries. It stays open, so it has no closed flag.
/// </summary>
public sealed record CrucibleBagObservation : CrucibleObservation
{
    /// <summary>The token balance.</summary>
    public required int Tokens { get; init; }

    /// <summary>
    /// The <c>XBMItem</c> id in each filled item slot; an item in two slots is listed twice.
    /// </summary>
    public required IReadOnlyList<uint> ItemIds { get; init; }

    /// <summary>The <c>XBMItem</c> id in each filled gear slot.</summary>
    public required IReadOnlyList<uint> GearIds { get; init; }
}

/// <summary>A treasure coffer, a fight's loot, or a shop's shelf.</summary>
public sealed record CrucibleOfferObservation : CrucibleObservation
{
    /// <summary>True on the last snapshot before the window closed.</summary>
    public required bool Closed { get; init; }

    /// <summary>Which window it was (serialized as <c>treasure</c>, <c>loot</c> or <c>shop</c>).</summary>
    public required CrucibleOfferSource Source { get; init; }

    /// <summary>The token balance the window drew.</summary>
    public required int Tokens { get; init; }

    /// <summary>The tokens the fight paid, on a loot window; null on the others.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required int? TokensEarned { get; init; }

    /// <summary>The shown offers, in the window's order.</summary>
    public required IReadOnlyList<CrucibleOfferUpload> Offers { get; init; }
}

/// <summary>One offered item.</summary>
/// <remarks>
/// A treasure or loot offer carries its item id alone. The three shop fields are not
/// <c>required</c> and hold null there; the shared serializer leaves a null out, so those keys
/// are absent rather than null.
/// </remarks>
public sealed record CrucibleOfferUpload
{
    /// <summary>The item's <c>XBMItem</c> id.</summary>
    public required uint ItemId { get; init; }

    /// <summary>A shop offer's price as drawn, discount applied.</summary>
    public int? Price { get; init; }

    /// <summary>Whether a shop offer is discounted.</summary>
    public bool? Discounted { get; init; }

    /// <summary>Whether a shop offer has been bought.</summary>
    public bool? Bought { get; init; }
}

/// <summary>The results screen at the end of a run.</summary>
public sealed record CrucibleResultsObservation : CrucibleObservation
{
    /// <summary>The board's Degree, 0 to 3, or null when the drawn mode did not resolve.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required int? Degree { get; init; }

    /// <summary>
    /// The run's rank, 0 (Apprentice) to 8 (Legendary), or null when the drawn rank did not resolve.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required int? RankIndex { get; init; }

    /// <summary>The score as the screen drew it.</summary>
    public required CrucibleScoreUpload Score { get; init; }

    /// <summary>The bonuses earned, in the screen's order.</summary>
    public required IReadOnlyList<CrucibleBonusUpload> Bonuses { get; init; }

    /// <summary>The familiars brought, in the screen's order.</summary>
    public required IReadOnlyList<CrucibleResultFamiliarUpload> Familiars { get; init; }
}

/// <summary>The score's numbers.</summary>
public sealed record CrucibleScoreUpload
{
    /// <summary>The score before bonuses.</summary>
    public required int Base { get; init; }

    /// <summary>The performance share, as a percentage.</summary>
    public required int PerformancePct { get; init; }

    /// <summary>The points the performance share paid.</summary>
    public required int PerformancePoints { get; init; }

    /// <summary>How many enemies the run beat.</summary>
    public required int Enemies { get; init; }

    /// <summary>The points the enemies paid.</summary>
    public required int EnemyPoints { get; init; }

    /// <summary>How many elites the run beat.</summary>
    public required int Elites { get; init; }

    /// <summary>The points the elites paid.</summary>
    public required int ElitePoints { get; init; }

    /// <summary>How many bosses the run beat.</summary>
    public required int Bosses { get; init; }

    /// <summary>The points the bosses paid.</summary>
    public required int BossPoints { get; init; }

    /// <summary>The HP the run ended on.</summary>
    public required int RemainingHp { get; init; }

    /// <summary>The points all bonuses paid together.</summary>
    public required int BonusPoints { get; init; }

    /// <summary>The final score.</summary>
    public required int Total { get; init; }
}

/// <summary>One bonus the run earned.</summary>
public sealed record CrucibleBonusUpload
{
    /// <summary>The bonus's <c>XBMScoreBonus</c> row id, or null when its name did not resolve.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required uint? BonusId { get; init; }

    /// <summary>The points it paid.</summary>
    public required int Points { get; init; }
}

/// <summary>What one familiar the run brought gained.</summary>
public sealed record CrucibleResultFamiliarUpload
{
    /// <summary>The familiar's <c>XBMPet</c> row id.</summary>
    public required uint PetId { get; init; }

    /// <summary>Its rank before the run.</summary>
    public required int RankBefore { get; init; }

    /// <summary>Its rank after the run.</summary>
    public required int RankAfter { get; init; }

    /// <summary>Its EXP toward the next rank before the run.</summary>
    public required int ExpBefore { get; init; }

    /// <summary>Its EXP toward the next rank after the run.</summary>
    public required int ExpAfter { get; init; }
}

/// <summary>The character's own HP.</summary>
public sealed record CrucibleSelfObservation : CrucibleObservation
{
    /// <summary>The character's current and maximum HP.</summary>
    public required CrucibleHpUpload Hp { get; init; }
}
