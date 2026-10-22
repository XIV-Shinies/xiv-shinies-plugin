using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Occult;

/// <summary>
/// Assembles the <c>POST /occult/instance-state</c> request from the tracker's snapshot. It reads
/// neither the game nor the clock, and sends nothing.
/// </summary>
public static class OccultUploadBuilder
{
    /// <summary>Builds one upload request.</summary>
    /// <param name="characterContentIdHash">SHA-256 of the character's ContentId, lowercase hex.</param>
    /// <param name="characterName">The character's name, the binding identity as on /sync.</param>
    /// <param name="homeWorld">The character's home world name.</param>
    /// <param name="pluginVersion">This plugin's version string.</param>
    /// <param name="territoryTypeId">The occult territory the snapshot describes.</param>
    /// <param name="trigger">What prompted this upload.</param>
    /// <param name="encounters">The tracker's snapshot (see <c>OccultEncounterTracker.Current</c>).</param>
    /// <param name="currentWorldId">The reporter's current <c>World</c> row id, or null when
    /// unreadable; the server then leaves the tracker unscoped by data center.</param>
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
                // One id key per row: the serializer omits the null one, which tells the server
                // which sheet the id belongs to.
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
    /// Formats a timestamp for the contract's fingerprint comparison (see <see cref="WireTime"/>), or
    /// null when there is none. The tracker already gives whole seconds, so nothing is lost.
    /// </summary>
    private static string? FormatSinceUtc(DateTimeOffset? since) =>
        since is { } moment ? WireTime.Format(moment) : null;
}
