using System;
using System.Collections.Generic;
using System.Globalization;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Assembles the <c>POST /occult/instance-state</c> request from the tracker's wire-ready
/// snapshot. Pure — no game, no clock, no network — so the wire shape stays unit-testable.
/// </summary>
public static class OccultUploadBuilder
{
    /// <summary>Builds one upload request.</summary>
    /// <param name="characterContentIdHash">SHA-256 of the character's ContentId, lowercase hex.</param>
    /// <param name="characterName">The character's name (binding identity, as on /sync).</param>
    /// <param name="homeWorld">The character's home world name.</param>
    /// <param name="pluginVersion">This plugin's version string.</param>
    /// <param name="territoryTypeId">The occult territory the snapshot describes.</param>
    /// <param name="trigger">What prompted this upload.</param>
    /// <param name="encounters">The tracker's current snapshot (see <c>OccultEncounterTracker.Current</c>).</param>
    /// <param name="currentWorldId">The reporter's current world (<c>World</c> row id), or null
    /// when unreadable — the server then leaves the tracker un-scoped by data center.</param>
    public static OccultInstanceStateRequest Build(
        string characterContentIdHash,
        string characterName,
        string homeWorld,
        string pluginVersion,
        uint territoryTypeId,
        OccultTrigger trigger,
        IReadOnlyList<OccultEncounterState> encounters,
        uint? currentWorldId)
    {
        var rows = new List<OccultEncounterUpload>(encounters.Count);
        foreach (var encounter in encounters)
        {
            rows.Add(new OccultEncounterUpload
            {
                // Exactly one of the two id keys per row: null here means the serializer omits
                // the key, which is how a row declares which sheet its id belongs to.
                DynamicEventId = encounter.IsFate ? null : encounter.Id,
                FateId = encounter.IsFate ? encounter.Id : null,
                Status = encounter.Status,
                SinceUtc = FormatSinceUtc(encounter.SinceUtc),
            });
        }

        return new OccultInstanceStateRequest
        {
            CharacterContentIdHash = characterContentIdHash,
            CharacterName = characterName,
            HomeWorld = homeWorld,
            PluginVersion = pluginVersion,
            Trigger = trigger,
            Instance = new OccultInstanceIdentity
            {
                TerritoryTypeId = territoryTypeId,
                WorldId = currentWorldId,
            },
            Encounters = rows,
        };
    }

    /// <summary>
    /// Formats a timestamp the way the contract's fingerprint comparison expects: second-exact
    /// UTC with a trailing <c>Z</c> (e.g. <c>2026-08-11T16:02:15Z</c>). The tracker already
    /// guarantees whole seconds, so no precision is discarded here.
    /// </summary>
    private static string? FormatSinceUtc(DateTimeOffset? since) =>
        since?.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
