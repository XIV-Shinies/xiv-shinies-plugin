using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>GET /api/plugin/v1/config</c> — remote kill switches, sync cadence, and the
/// item manifest. The client must honor the kill switches even though the server enforces them
/// too; obeying them locally saves pointless round trips.
/// </summary>
public sealed record ConfigResponse
{
    /// <summary>
    /// Per-category kill switches keyed by category (<c>"quests"</c>, …). False means "do not
    /// collect or send this". A dictionary rather than named properties, so a collector can look
    /// up its own switch by its <c>CategoryKey</c> without anyone branching on category names.
    /// </summary>
    public required Dictionary<string, bool> Categories { get; init; }

    /// <summary>
    /// Server-authored copy explaining why a switched-off category is off, keyed the same way as
    /// <see cref="Categories"/>. Null when the server sends none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A category can be off for two unlike reasons, and the difference matters to the person
    /// reading the row. A kill switch turns it off for everyone, usually while something is
    /// wrong — and carries <b>no</b> note, because during an outage any specific explanation is a
    /// guess the plugin would be making on the server's behalf. A category still being tested
    /// carries one, because "it is off" alone invites the reader to conclude something is broken.
    /// A category off for both reasons carries no note: the louder signal wins.
    /// </para>
    /// <para>
    /// The plugin never interprets the text — no keywords, no parsing, no deciding when it
    /// applies. The server chooses whether to send a note at all, and the presence of one is the
    /// entire signal. That keeps the two sides free to disagree about wording without a release.
    /// </para>
    /// </remarks>
    // NOT `required`, for the same older-server reason as ItemManifestGroups below.
    public Dictionary<string, string>? CategoryNotes { get; init; }

    /// <summary>
    /// The note for a category as it should be drawn, or null when there is nothing to draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for a category the server did not explain, and equally for one it "explained" with
    /// nothing — a missing value, an empty string, or only whitespace. <see cref="ServerText"/>
    /// does the bounding and folding, which is also what makes this safe to call from the draw
    /// path: it is reached once per row per frame.
    /// </para>
    /// <para>
    /// A <b>null</b> value inside the map is reachable despite the non-nullable declaration —
    /// the deserializer does not enforce nullable reference annotations, so a map entry written
    /// as JSON <c>null</c> arrives as one. The contract says the server never sends that; the
    /// backend being user-overridable is what makes the contract insufficient on its own.
    /// </para>
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
    /// A content hash of the manifest, not a counter — compare it for equality only. When it is
    /// unchanged the plugin can skip re-scanning the inventory.
    /// </summary>
    public required string ManifestVersion { get; init; }

    /// <summary>
    /// The manifest split into named consent groups, or null when the server does not send them.
    /// Null means: fall back to <see cref="ItemManifest"/> as one implicit group covered by the
    /// existing items consent — an older server is a supported peer.
    /// </summary>
    // NOT `required`: a required property makes deserialization of older configs throw,
    // and an older server is a supported peer, not an error.
    public IReadOnlyList<ItemManifestGroup>? ItemManifestGroups { get; init; }

    /// <summary>
    /// Manifest ids to OMIT from an upload when no scan source resolved a value, instead of
    /// reporting the explicit <c>count: 0</c>. Null when the server does not send the field.
    /// </summary>
    /// <remarks>
    /// The server lists its content-bound currencies here (Occult Crescent's pieces, for
    /// example): the game only exposes their counts while the character is inside that content,
    /// so out-of-zone their absence from every source means "not visible from here" — an
    /// explicit zero would overwrite the real count the server already holds. Which ids behave
    /// this way is the server's catalog knowledge; the plugin applies the set generically and
    /// never hardcodes an id.
    /// </remarks>
    // NOT `required`, for the same older-server reason as ItemManifestGroups above.
    public IReadOnlyList<uint>? ItemOmitWhenUnseenIds { get; init; }

    /// <summary>
    /// The live occult instance tracker's switches, or null when the server does not send the
    /// block. Null means the server has no tracker endpoint, so the tracker stays off (see
    /// <see cref="Occult.OccultGate"/> for the reasoning and the rest of the gate ladder).
    /// </summary>
    // NOT `required`, for the same older-server reason as ItemManifestGroups above.
    public OccultTrackerConfig? OccultTracker { get; init; }

    /// <summary>
    /// Quest ids whose journal sequence the server wants reported — quests with several
    /// sequential turn-ins, where knowing which step the journal is on lets the server credit
    /// the batches already handed over. Null when the server does not send the field.
    /// </summary>
    /// <remarks>
    /// The ids are Quest Excel row ids, the same id space the <c>quests</c> category uploads.
    /// Which quests qualify is the server's catalog knowledge; the plugin reads the sequences
    /// generically and never hardcodes a quest id.
    /// </remarks>
    // NOT `required`, for the same older-server reason as ItemManifestGroups above.
    public IReadOnlyList<uint>? QuestSequenceManifest { get; init; }

    /// <summary>
    /// Whether the server permits this category right now.
    /// </summary>
    /// <remarks>
    /// A category the server has never heard of reads as <b>enabled</b>. That lets a plugin ship a
    /// new collector before the server grows the matching switch: the server strips payload keys it
    /// does not recognize, so sending one costs a few bytes and breaks nothing. Defaulting to
    /// disabled instead would silently withhold facts until both sides shipped in lockstep.
    /// </remarks>
    public bool IsCategoryEnabled(string categoryKey) =>
        !Categories.TryGetValue(categoryKey, out var enabled) || enabled;
}

/// <summary>
/// The <c>occultTracker</c> block of <c>/config</c>: the live instance tracker's switches.
/// </summary>
public sealed record OccultTrackerConfig
{
    /// <summary>
    /// The tracker's kill switch. The server folds the global, per-user, and category switches
    /// into this one value, so the client honors it alone.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>Idle re-upload cadence while inside an instance (clamped client-side).</summary>
    /// <remarks>
    /// NOT <c>required</c>, unlike <see cref="Enabled"/>: a required member missing from the
    /// wire fails the WHOLE <c>/config</c> deserialization — which would also strand /sync on
    /// its last-known manifest — and the scheduler clamps whatever value arrives into a sane
    /// range anyway, so a defaulted cadence is strictly safe where a guessed kill switch is not.
    /// </remarks>
    public int HeartbeatSeconds { get; init; } = 60;
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
/// <para>
/// The plugin never hardcodes a group key and interprets exactly one flag: <see cref="Legacy"/>.
/// Everything else (which ids, what the group means) is the server's business.
/// </para>
/// <para>
/// <see cref="Key"/>, <see cref="Label"/>, and <see cref="Ids"/> are <c>required</c> on purpose:
/// one malformed group fails the whole <c>/config</c> deserialization rather than being partially
/// trusted — the same all-or-nothing stance every other required config field takes. The plugin
/// then keeps its last known config until the next poll.
/// </para>
/// </remarks>
public sealed record ItemManifestGroup
{
    /// <summary>Stable consent identifier. A server-side RENAME is a new group (re-consent).</summary>
    public required string Key { get; init; }

    /// <summary>User-facing label, shown beside the group's opt-in checkbox.</summary>
    public required string Label { get; init; }

    /// <summary>The item ids this group asks about.</summary>
    public required IReadOnlyList<uint> Ids { get; init; }

    /// <summary>
    /// True when this group's scope was already covered by pre-group items consent — the
    /// one-time migration opts existing users into exactly these groups and nothing else.
    /// </summary>
    public bool Legacy { get; init; }
}
