using System.Collections.Generic;
using System.Text.Json.Serialization;
// The occult wire enums (trigger words and the three-word status vocabulary).
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The body of <c>POST /api/plugin/v1/occult/instance-state</c> — one compact full snapshot of
/// the occult instance the character is standing in (docs/api-contract.md § occult/instance-state).
/// </summary>
public sealed record OccultInstanceStateRequest
{
    /// <summary>SHA-256 of the character's ContentId, lowercase hex. The raw id never travels.</summary>
    public required string CharacterContentIdHash { get; init; }

    /// <summary>Used only for first-upload binding and to render a friendly 403.</summary>
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
    /// The full encounter snapshot: every CE/tower container slot plus tracked FATEs. The server
    /// resolves WHICH instance this is by matching the timestamped entries against its active
    /// trackers (the fingerprint), so a fresh full list rides on every upload.
    /// </summary>
    public required IReadOnlyList<OccultEncounterUpload> Encounters { get; init; }
}

/// <summary>
/// The <c>instance</c> object: the territory alone. Occult instances have no client-readable
/// id — identity is the encounter fingerprint — so the territory is the only honest field.
/// </summary>
public sealed record OccultInstanceIdentity
{
    /// <summary>The game's TerritoryType row id (South Horn 1252, North Horn 1346).</summary>
    public required uint TerritoryTypeId { get; init; }
}

/// <summary>One encounter row of the snapshot.</summary>
public sealed record OccultEncounterUpload
{
    /// <summary>
    /// The <c>DynamicEvent</c> sheet row id for a CE or tower row, or null for a FATE row —
    /// null is omitted from the JSON, so exactly one of the two id keys appears.
    /// </summary>
    public uint? DynamicEventId { get; init; }

    /// <summary>The <c>Fate</c> sheet row id for a FATE row, or null for a CE row.</summary>
    public uint? FateId { get; init; }

    /// <summary>The three-word status (serialized as "preparing" | "active" | "down").</summary>
    public required OccultEncounterStatus Status { get; init; }

    /// <summary>
    /// When this status began: a second-exact UTC string like <c>2026-08-11T16:02:15Z</c>, or
    /// null when nothing is known. Pre-formatted by <c>OccultUploadBuilder</c> because the
    /// contract wants a trailing <c>Z</c>, where the serializer's own DateTimeOffset format
    /// would write a <c>+00:00</c> offset.
    /// </summary>
    /// <remarks>
    /// The attribute overrides the shared serializer policy of omitting null properties: the
    /// contract wants this key PRESENT with a JSON null ("null entries carry state but never
    /// identity"), unlike the id fields above where omission is the point.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? SinceUtc { get; init; }
}
