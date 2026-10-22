using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Turns window readings into the snapshots and request of
/// <c>POST /api/plugin/v1/crucible/observations</c>. It reads neither the game nor the clock.
/// </summary>
/// <remarks>
/// Each snapshot method returns the base <see cref="CrucibleObservation"/>, the type the request's
/// list holds, which also makes the serializer write the snapshot's <c>kind</c> key.
/// </remarks>
public static class CrucibleUploadBuilder
{
    /// <summary>A board window snapshot.</summary>
    /// <param name="closed">Whether this is the last snapshot before the window closed.</param>
    public static CrucibleObservation Board(
        CrucibleBoardReading reading, DateTimeOffset observedAt, bool closed)
    {
        var pieces = new List<CruciblePieceUpload>(reading.Pieces.Count);
        foreach (var piece in reading.Pieces)
        {
            // The wire carries the window's own progress number, not the enum's name.
            pieces.Add(new CruciblePieceUpload
            {
                NodeIndex = piece.NodeIndex,
                Progress = (int)piece.Progress,
            });
        }

        return new CrucibleBoardObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            Closed = closed,
            View = (int)reading.View,
            CurrentNodeIndex = reading.CurrentNodeIndex,
            Pieces = pieces,
        };
    }

    /// <summary>A team window snapshot.</summary>
    /// <param name="closed">Whether this is the last snapshot before the window closed.</param>
    public static CrucibleObservation Team(
        CrucibleTeamReading reading, DateTimeOffset observedAt, bool closed)
    {
        var familiars = new List<CrucibleFamiliarUpload>(reading.Familiars.Count);
        foreach (var familiar in reading.Familiars)
        {
            familiars.Add(new CrucibleFamiliarUpload
            {
                PetId = familiar.PetId,
                Place = familiar.Place,
                Hp = new CrucibleHpUpload { Current = familiar.CurrentHp, Max = familiar.MaxHp },
                Rank = familiar.Rank,
                RankSynced = familiar.RankSynced,
                FeedItemIds = familiar.FeedItemIds,
                Resting = familiar.Resting,
            });
        }

        return new CrucibleTeamObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            Closed = closed,
            Mode = (int)reading.Mode,
            ItemId = reading.ItemId,
            Familiars = familiars,
        };
    }

    /// <summary>A run HUD snapshot.</summary>
    public static CrucibleObservation Bag(CrucibleBagReading reading, DateTimeOffset observedAt) =>
        new CrucibleBagObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            Tokens = reading.Tokens,
            ItemIds = reading.ItemIds,
            GearIds = reading.GearIds,
        };

    /// <summary>A treasure, loot or shop snapshot.</summary>
    /// <param name="closed">Whether this is the last snapshot before the window closed.</param>
    public static CrucibleObservation Offer(
        CrucibleOfferReading reading, DateTimeOffset observedAt, bool closed)
    {
        var offers = new List<CrucibleOfferUpload>(reading.Offers.Count);
        foreach (var offer in reading.Offers)
        {
            // The reader leaves the three shop fields null outside a shop, and a null is left out
            // of the JSON, so a treasure or loot offer goes out as its item id alone.
            offers.Add(new CrucibleOfferUpload
            {
                ItemId = offer.ItemId,
                Price = offer.Price,
                Discounted = offer.Discounted,
                Bought = offer.Bought,
            });
        }

        return new CrucibleOfferObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            Closed = closed,
            Source = reading.Source,
            Tokens = reading.Tokens,
            TokensEarned = reading.TokensEarned,
            Offers = offers,
        };
    }

    /// <summary>
    /// A results screen snapshot, with its drawn text resolved to ids: the mode and rank by the
    /// caller, each bonus name through <paramref name="bonusId"/>.
    /// </summary>
    /// <param name="degree">The board's Degree the drawn mode resolved to, or null.</param>
    /// <param name="rankIndex">The rank the drawn rank resolved to, or null.</param>
    /// <param name="bonusId">
    /// Looks up a bonus's <c>XBMScoreBonus</c> row id by its drawn name; null for a name it does not
    /// know.
    /// </param>
    /// <remarks>
    /// A name that does not resolve is sent as null, never as a guess; its points are sent either way.
    /// </remarks>
    public static CrucibleObservation Results(
        CrucibleResultsReading reading,
        DateTimeOffset observedAt,
        int? degree,
        int? rankIndex,
        Func<string, uint?> bonusId)
    {
        var bonuses = new List<CrucibleBonusUpload>(reading.Bonuses.Count);
        foreach (var bonus in reading.Bonuses)
            bonuses.Add(new CrucibleBonusUpload { BonusId = bonusId(bonus.Name), Points = bonus.Points });

        var familiars = new List<CrucibleResultFamiliarUpload>(reading.Familiars.Count);
        foreach (var familiar in reading.Familiars)
        {
            familiars.Add(new CrucibleResultFamiliarUpload
            {
                PetId = familiar.PetId,
                RankBefore = familiar.RankBefore,
                RankAfter = familiar.RankAfter,
                ExpBefore = familiar.ExpBefore,
                ExpAfter = familiar.ExpAfter,
            });
        }

        return new CrucibleResultsObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            Degree = degree,
            RankIndex = rankIndex,
            Score = new CrucibleScoreUpload
            {
                Base = reading.BaseScore,
                PerformancePct = reading.PerformancePercent,
                PerformancePoints = reading.PerformancePoints,
                Enemies = reading.Enemies.Count,
                EnemyPoints = reading.Enemies.Points,
                Elites = reading.Elites.Count,
                ElitePoints = reading.Elites.Points,
                Bosses = reading.Bosses.Count,
                BossPoints = reading.Bosses.Points,
                RemainingHp = reading.RemainingHp,
                BonusPoints = reading.BonusTotal,
                Total = reading.TotalScore,
            },
            Bonuses = bonuses,
            Familiars = familiars,
        };
    }

    /// <summary>A snapshot of the character's own HP.</summary>
    public static CrucibleObservation Self(int currentHp, int maxHp, DateTimeOffset observedAt) =>
        new CrucibleSelfObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            Hp = new CrucibleHpUpload { Current = currentHp, Max = maxHp },
        };

    /// <summary>A snapshot of the familiars' HP (see <see cref="CrucibleFamiliarTracker"/>).</summary>
    /// <param name="familiars">Each familiar's last reading, in the order they first came out.</param>
    public static CrucibleObservation Familiars(
        bool afterFight, IReadOnlyList<CrucibleFieldedFamiliar> familiars, DateTimeOffset observedAt)
    {
        // A new list, so the snapshot keeps its readings even when the caller's list is cleared.
        var uploads = new List<CrucibleFieldedFamiliarUpload>(familiars.Count);
        foreach (var familiar in familiars)
        {
            // A reading is never later than its snapshot, which a clock set back during a fight could
            // otherwise make it.
            var readAt = familiar.ReadAt < observedAt ? familiar.ReadAt : observedAt;

            uploads.Add(new CrucibleFieldedFamiliarUpload
            {
                PetId = familiar.PetId,
                Hp = new CrucibleHpUpload { Current = familiar.CurrentHp, Max = familiar.MaxHp },
                Summoned = familiar.Summoned,
                ReadAtUtc = WireTime.Format(readAt),
            });
        }

        return new CrucibleFamiliarsObservation
        {
            ObservedAtUtc = WireTime.Format(observedAt),
            AfterFight = afterFight,
            Familiars = uploads,
        };
    }

    /// <summary>One upload request.</summary>
    /// <param name="characterContentIdHash">SHA-256 of the character's ContentId, lowercase hex.</param>
    /// <param name="characterName">The character's name, part of the binding identity as on /sync.</param>
    /// <param name="homeWorld">The character's home world name.</param>
    /// <param name="territoryTypeId">
    /// The territory the upload concerns (see <see cref="CrucibleObservationsRequest.TerritoryTypeId"/>).
    /// </param>
    /// <param name="observations">The snapshots to send, in the order they were observed.</param>
    public static CrucibleObservationsRequest Request(
        string characterContentIdHash,
        string characterName,
        string homeWorld,
        string pluginVersion,
        CrucibleTrigger trigger,
        uint territoryTypeId,
        IReadOnlyList<CrucibleObservation> observations) =>
        new()
        {
            CharacterContentIdHash = characterContentIdHash,
            CharacterName = characterName,
            HomeWorld = homeWorld,
            PluginVersion = pluginVersion,
            Trigger = trigger,
            TerritoryTypeId = territoryTypeId,

            // A copy, so the request cannot change after it is built: the contract requires a retry
            // to carry the same content.
            Observations = [.. observations],
        };
}
