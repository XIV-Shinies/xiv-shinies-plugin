using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>GET /api/plugin/v1/config</c>: kill switches, sync cadence, and the item
/// manifest. The server enforces the kill switches too; honoring them here saves round trips.
/// </summary>
/// <remarks>
/// A member that is not <c>required</c> is one the server may omit; a missing required member fails
/// the whole <c>/config</c> and leaves the plugin on its last known config.
/// </remarks>
public sealed record ConfigResponse
{
    /// <summary>
    /// Per-category kill switches keyed by category key, such as <c>"quests"</c>. False means do not
    /// collect or send it. A collector looks up its own switch, so nothing branches on category names.
    /// </summary>
    public required Dictionary<string, bool> Categories { get; init; }

    /// <summary>
    /// Server-authored copy explaining why a switched-off category is off, keyed like
    /// <see cref="Categories"/>. Null when the server sends none.
    /// </summary>
    /// <remarks>
    /// A kill switch carries no note, since any explanation during an outage would be a guess; a
    /// category in testing carries one, so "off" does not read as broken. A category off for both
    /// reasons carries none. The plugin never interprets the text: a note's presence is the signal.
    /// </remarks>
    public Dictionary<string, string>? CategoryNotes { get; init; }

    /// <summary>
    /// The note for a category as it should be drawn, or null when there is nothing to draw: no note,
    /// or one that is empty or whitespace.
    /// </summary>
    /// <remarks>
    /// <see cref="ServerText"/> bounds and folds the text, which keeps this cheap enough for the draw
    /// path, once per row per frame. A map value sent as JSON <c>null</c> arrives as null despite the
    /// declaration; the contract forbids it, but the backend is user-overridable.
    /// </remarks>
    public string? CategoryNote(string categoryKey) =>
        CategoryNotes is not null && CategoryNotes.TryGetValue(categoryKey, out var note)
            ? ServerText.SingleLine(note)
            : null;

    /// <summary>The global kill switch. False means stop uploading entirely.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Server-chosen sync cadence.</summary>
    public required ConfigIntervals Intervals { get; init; }

    /// <summary>The only item IDs the plugin should check possession of.</summary>
    public required IReadOnlyList<uint> ItemManifest { get; init; }

    /// <summary>
    /// A content hash of the manifest, not a counter: compare it for equality only. While it is
    /// unchanged the plugin can skip re-scanning the inventory.
    /// </summary>
    public required string ManifestVersion { get; init; }

    /// <summary>
    /// The manifest split into named consent groups, or null when the server sends none. Null or empty
    /// means <see cref="ItemManifest"/> is one implicit group under the existing items consent.
    /// </summary>
    public IReadOnlyList<ItemManifestGroup>? ItemManifestGroups { get; init; }

    /// <summary>
    /// Manifest ids to omit from an upload when no scan source resolved a value, rather than report an
    /// explicit <c>count: 0</c>. Null when the server does not send the field.
    /// </summary>
    /// <remarks>
    /// These are content-bound currencies, such as Occult Crescent's pieces, whose counts the game
    /// exposes only inside that content; out of zone, a zero would overwrite the real count the server
    /// holds. The set is the server's catalog knowledge, and the plugin never hardcodes an id.
    /// </remarks>
    public IReadOnlyList<uint>? ItemOmitWhenUnseenIds { get; init; }

    /// <summary>
    /// The live occult instance tracker's switches, or null when the server has no tracker endpoint,
    /// which keeps the tracker off (see <see cref="Occult.OccultGate"/> for the full gate).
    /// </summary>
    public OccultTrackerConfig? OccultTracker { get; init; }

    /// <summary>
    /// The Crucible run sharing's switches, or null when the server has no Crucible endpoint, and
    /// nothing may be sent there.
    /// </summary>
    public CrucibleRunsConfig? CrucibleRuns { get; init; }

    /// <summary>
    /// Quest ids whose journal sequence the server wants reported, or null when it omits the field.
    /// </summary>
    /// <remarks>
    /// The server names quests with several sequential turn-ins, where the step tells it which batches
    /// were handed over; the plugin never hardcodes a quest id. The ids are Quest Excel row ids, the
    /// same id space the <c>quests</c> category uploads.
    /// </remarks>
    public IReadOnlyList<uint>? QuestSequenceManifest { get; init; }

    /// <summary>
    /// Whether the server permits this category right now.
    /// </summary>
    /// <remarks>
    /// A category the server has never heard of reads as enabled, so a new collector can ship before
    /// the server's switch: the server strips payload keys it does not recognize. Defaulting to
    /// disabled would withhold facts until both sides shipped in lockstep.
    /// </remarks>
    // `Categories is null` catches an explicit JSON null (see ApiJson) and reads as "the server named
    // nothing"; a throw here would take down the settings window, which asks once per row per frame.
    public bool IsCategoryEnabled(string categoryKey) =>
        Categories is null || !Categories.TryGetValue(categoryKey, out var enabled) || enabled;
}

/// <summary>
/// The <c>occultTracker</c> block of <c>/config</c>: the live instance tracker's switches.
/// </summary>
public sealed record OccultTrackerConfig
{
    /// <summary>
    /// The tracker's kill switch, with the global, per-user, and category switches folded in.
    /// </summary>
    /// <remarks>
    /// The client checks the global switch too (see <c>OccultGate</c>), because the backend is
    /// user-overridable.
    /// </remarks>
    public required bool Enabled { get; init; }

    /// <summary>Idle re-upload cadence while inside an instance (clamped client-side).</summary>
    /// <remarks>
    /// Not <c>required</c>, unlike <see cref="Enabled"/>: the scheduler clamps whatever arrives, so a
    /// default cadence is safe where a guessed kill switch is not.
    /// </remarks>
    public int HeartbeatSeconds { get; init; } = 60;
}

/// <summary>
/// The <c>crucibleRuns</c> block of <c>/config</c>: the Crucible run sharing's switches.
/// </summary>
public sealed record CrucibleRunsConfig
{
    /// <summary>
    /// The sharing's kill switch. The server folds the global, per-user and category switches,
    /// and whether the feature is open to this user, into this one value.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>The idle upload cadence while inside a board.</summary>
    // Not `required`: the scheduler holds any cadence within its bounds, so the default is safe.
    public int HeartbeatSeconds { get; init; } = 60;

    /// <summary>
    /// A line the server wants shown beside the setting, or null. The server sends one only when
    /// the feature is held back for this user, such as while it is in testing.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>Server-chosen sync cadence.</summary>
public sealed record ConfigIntervals
{
    /// <summary>How often to run a full-sweep upload.</summary>
    public required int FullSyncMinutes { get; init; }

    /// <summary>How long to wait after an unlock event before uploading, to batch a burst.</summary>
    public required int UnlockDebounceSeconds { get; init; }
}

/// <summary>
/// One named slice of the item manifest, carrying its own user consent.
/// </summary>
/// <remarks>
/// The plugin never hardcodes a group key and interprets one flag, <see cref="Legacy"/>; the rest is
/// the server's business. <see cref="Key"/>, <see cref="Label"/>, and <see cref="Ids"/> are
/// <c>required</c>, so one malformed group fails the whole <c>/config</c> rather than being partly
/// trusted (see <see cref="ConfigResponse"/>).
/// </remarks>
public sealed record ItemManifestGroup
{
    /// <summary>
    /// Stable consent identifier. A server-side rename is a new group that needs new consent.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>User-facing label, shown beside the group's opt-in checkbox.</summary>
    public required string Label { get; init; }

    /// <summary>The item ids this group asks about.</summary>
    public required IReadOnlyList<uint> Ids { get; init; }

    /// <summary>
    /// True when pre-group items consent already covered this group. The one-time migration opts
    /// existing users into exactly these groups.
    /// </summary>
    public bool Legacy { get; init; }
}
