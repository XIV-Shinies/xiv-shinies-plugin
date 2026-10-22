using System.Collections.Generic;
using System.Text.Json.Serialization;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The body of <c>POST /api/plugin/v1/occult/instance-state</c>: one compact full snapshot of the
/// occult instance the character is in (docs/api-contract.md, occult/instance-state).
/// </summary>
public sealed record OccultInstanceStateRequest
{
    /// <summary>SHA-256 of the character's ContentId, lowercase hex. The raw id never travels.</summary>
    public required string CharacterContentIdHash { get; init; }

    /// <summary>Sent for the server's first-upload binding; a 403 echoes it back.</summary>
    public required string CharacterName { get; init; }

    /// <summary>The character's home world name.</summary>
    public required string HomeWorld { get; init; }

    /// <summary>This plugin's version string.</summary>
    public required string PluginVersion { get; init; }

    /// <summary>What prompted this upload (serialized as its lowercase word).</summary>
    public required OccultTrigger Trigger { get; init; }

    /// <summary>Where the character is standing.</summary>
    public required OccultInstanceIdentity Instance { get; init; }

    /// <summary>
    /// The full encounter snapshot: every CE and tower container slot plus tracked FATEs.
    /// </summary>
    /// <remarks>
    /// The server identifies the instance by matching the timestamped entries against its active
    /// trackers, so every upload carries the full list.
    /// </remarks>
    public required IReadOnlyList<OccultEncounterUpload> Encounters { get; init; }
}

/// <summary>The <c>instance</c> object: the territory and the reporter's world.</summary>
/// <remarks>
/// An occult instance has no client-readable id, so the server identifies it by its encounter
/// fingerprint, and these are the only honest fields.
/// </remarks>
public sealed record OccultInstanceIdentity
{
    /// <summary>The game's TerritoryType row id (South Horn 1252, North Horn 1346).</summary>
    public required uint TerritoryTypeId { get; init; }

    /// <summary>
    /// The reporter's current world (<c>World</c> sheet row id), not the home world, since a
    /// data-center traveler reports from where they are. Omitted from the JSON when unreadable.
    /// </summary>
    /// <remarks>
    /// The server maps the world to a data center to scope fingerprint matching and the browse list;
    /// without it the tracker stays unscoped.
    /// </remarks>
    public uint? WorldId { get; init; }
}

/// <summary>One encounter row of the snapshot.</summary>
public sealed record OccultEncounterUpload
{
    /// <summary>
    /// The <c>DynamicEvent</c> sheet row id for a CE or tower row, or null for a FATE row. Null is
    /// omitted from the JSON, so exactly one of the two id keys appears.
    /// </summary>
    public uint? DynamicEventId { get; init; }

    /// <summary>The <c>Fate</c> sheet row id for a FATE row, or null for a CE row.</summary>
    public uint? FateId { get; init; }

    /// <summary>The three-word status, serialized as "preparing", "active" or "down".</summary>
    public required OccultEncounterStatus Status { get; init; }

    /// <summary>
    /// When this status began, as second-exact UTC text such as <c>2026-08-11T16:02:15Z</c>, or null
    /// when nothing is known.
    /// </summary>
    /// <remarks>
    /// <c>OccultUploadBuilder</c> formats it, because the contract wants a trailing <c>Z</c> where the
    /// serializer would write <c>+00:00</c>. A null is written, not omitted: the contract wants the key
    /// present ("null entries carry state but never identity"), unlike the id keys above.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? SinceUtc { get; init; }
}
