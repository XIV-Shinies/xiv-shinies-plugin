using System.Collections.Generic;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>Skip reasons the runner itself produces. Collectors may return their own.</summary>
public static class CollectSkipReasons
{
    /// <summary>The user or the server switched this category off.</summary>
    public const string Disabled = "disabled";

    /// <summary>The collector threw. Its facts are omitted; the rest of the snapshot proceeds.</summary>
    public const string CollectorError = "collector_error";

    /// <summary>The game data sheet could not be loaded.</summary>
    public const string SheetUnavailable = "sheet_unavailable";

    /// <summary>
    /// The server has not told us what this collection should look for yet — its manifest (item
    /// ids, quest ids) has not been received. Distinct from "the manifest is empty", which means
    /// there is genuinely nothing to check.
    /// </summary>
    public const string NoRemoteConfig = "no_remote_config";

    /// <summary>The inventory is not readable — usually because no character is logged in.</summary>
    public const string InventoryUnavailable = "inventory_unavailable";

    /// <summary>
    /// The server offered consent groups for this collection and the user has none of them switched
    /// on, so there is nothing the collection is allowed to look for. A skip rather than an empty
    /// result, because no container was ever opened: reporting facts here would claim a scan that did
    /// not happen, and the user would be told the collection was read when it was not.
    /// </summary>
    public const string NoItemGroupsEnabled = "no_item_groups_enabled";

    /// <summary>
    /// The achievements list has never been requested from the server this session, so the game
    /// cannot answer which achievements are complete. The user fixes this by opening their
    /// Achievements window once; the settings UI turns this reason into that hint.
    /// </summary>
    public const string AchievementListNotLoaded = "achievement_list_not_loaded";

    /// <summary>
    /// Turns a skip reason into advice for the settings window, or null if it is not worth saying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note carefully what this switches on: a <b>reason</b>, never a category. That is the whole
    /// trick behind the "open your Achievements window once" hint. The achievements collector reports
    /// <see cref="AchievementListNotLoaded"/>; this turns that reason into advice; and the window
    /// prints it beside whichever category reported it. Nothing anywhere names achievements. A future
    /// collector that reports the same reason gets the same hint for free.
    /// </para>
    /// <para>
    /// Every string returned here is a <b>phrase, not a standalone sentence</b>: it is drawn after the
    /// category's own display name, as "<c>{DisplayName}: {phrase}</c>" (see
    /// <see cref="ReadStatusView"/>). A new reason's copy must complete that pattern, and — because
    /// nothing happens on its own; the plugin cannot see the user open a window — must name the one
    /// in-game action that resolves it.
    /// </para>
    /// <para>
    /// An unrecognized reason returns null rather than the raw code — a wire string like
    /// <c>"collector_error"</c> means nothing to a player. The category is still reported as unread;
    /// there is simply no action to offer alongside it.
    /// </para>
    /// </remarks>
    public static string? Describe(string reason) => reason switch
    {
        AchievementListNotLoaded =>
            "not read yet — open your Achievements window in game once, then press Sync now.",

        // The hint names no category on purpose: every manifest-driven collection — item counts
        // and quest sequences alike — reports this same reason.
        NoRemoteConfig =>
            "not read yet — waiting for XIV Shinies to say what to look for.",

        InventoryUnavailable =>
            "not read yet — log in to a character so your inventory can be read.",

        NoItemGroupsEnabled =>
            "not read — none of its groups are switched on. Tick at least one under Collections to " +
            "include it.",

        // "disabled" needs no explanation: the checkbox beside it already says so. "collector_error"
        // and "sheet_unavailable" are bugs or transient game states the user cannot do anything about.
        _ => null,
    };
}

/// <summary>
/// What a collector produced: either facts, or a reason it could not read them.
/// </summary>
/// <remarks>
/// The distinction matters enormously. Facts — even an <b>empty</b> list — mean "I read the source,
/// and this is what was there". A skip means "I could not read the source", which omits the
/// category from the upload entirely. The server treats absence as "no information" and never as
/// "the collection is empty", so a skip can never erase anything.
/// </remarks>
public sealed record CollectResult
{
    // A private constructor forces callers through the named factory methods below, so a result
    // can never be built in a nonsensical state (both facts and a skip reason, or neither).
    private CollectResult()
    {
    }

    /// <summary>Why the source could not be read, or null when facts were collected.</summary>
    public string? SkipReason { get; private init; }

    /// <summary>The collected facts as JSON, or null when the collector skipped.</summary>
    public JsonNode? Facts { get; private init; }

    /// <summary>
    /// Per-source scan status (inventory live, saddlebag unscanned, retainers cached), or null when
    /// no source status is reported. Only valid when <see cref="WasCollected"/> is true.
    /// </summary>
    /// <remarks>
    /// Source-keyed, never category-keyed: a note describes a physical storage location, not a
    /// collection, so nothing downstream branches on which collector said it. These are ONE
    /// collector's notes; the runner merges every collector's notes into the snapshot.
    /// </remarks>
    public IReadOnlyDictionary<string, ItemSourceStatus>? SourceNotes { get; private init; }

    /// <summary>True when facts were read (possibly an empty list).</summary>
    public bool WasCollected => SkipReason is null && Facts is not null;

    /// <summary>
    /// True when these facts are asserted to be the character's <b>complete</b> set for the
    /// category: the collector enumerated its entire domain, and the list came back non-empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This claim ends up on the wire as the category's <c>collectionScopes</c> <c>"full"</c>
    /// declaration, which is what lets the server treat an id's absence as evidence — see
    /// <see cref="Api.SyncRequest.CollectionScopes"/> for what it licenses. Only a collector can
    /// make it honestly, because only the collector knows whether it walked its whole domain or
    /// took a shortcut, so it travels on the result instead of being inferred downstream. False is
    /// always safe: it merely carries no evidence of absence. Sheet padding does not count against
    /// it — row 0 is not a candidate, so an enumeration is still complete without it.
    /// </para>
    /// <para>
    /// One floor is enforced centrally rather than left to collectors: <b>an empty list never
    /// carries the claim</b>, whatever the collector asked for. "Owns nothing" and "the game had
    /// not filled this in yet" produce the identical empty read, and an empty list declared
    /// complete is the one shape that contradicts every entry the user marked by hand at once.
    /// </para>
    /// </remarks>
    public bool CompleteEnumeration { get; private init; }

    /// <summary>The source could not be read; omit this category from the upload.</summary>
    /// <param name="reason">
    /// A short, stable, machine-readable reason (for example <c>"achievement_list_not_loaded"</c>).
    /// The UI maps it to a hint; nothing else interprets it.
    /// </param>
    public static CollectResult Skipped(string reason) => new() { SkipReason = reason };

    /// <summary>Facts for a category that is a plain list of unlocked or completed IDs.</summary>
    /// <param name="ids">The collected ids.</param>
    /// <param name="completeEnumeration">
    /// True when the collector checked every candidate in its domain. This is a request, not a
    /// verdict: <see cref="CompleteEnumeration"/> additionally withholds the claim for an empty
    /// list. Defaults to false — the safe claim — so a caller must opt in explicitly.
    /// </param>
    /// <remarks>
    /// Zero is dropped. The server requires <b>positive</b> integers, and the game's data sheets
    /// begin with a blank row 0 used as padding. A single zero would fail validation and cause the
    /// server to reject the <b>entire upload</b> — every category, not just this one. Filtering here
    /// rather than in each collector means no collector, present or future, can forget.
    /// (This does not touch <see cref="Items"/>: an item <i>count</i> of zero is legitimate.)
    /// </remarks>
    public static CollectResult Ids(IReadOnlyList<uint> ids, bool completeEnumeration = false)
    {
        var positiveIds = new List<uint>(ids.Count);
        foreach (var id in ids)
        {
            if (id != 0)
                positiveIds.Add(id);
        }

        // The emptiness floor described on CompleteEnumeration. Withholding costs nothing here: an
        // empty list carries no evidence worth asserting either way.
        var complete = completeEnumeration && positiveIds.Count > 0;

        return new() { Facts = SyncFacts.Ids(positiveIds), CompleteEnumeration = complete };
    }

    /// <summary>Facts for the <c>items</c> category, which carries objects rather than IDs.</summary>
    public static CollectResult Items(IReadOnlyList<ItemPossession> items) =>
        Items(items, sourceNotes: null);

    /// <summary>
    /// Facts for the <c>questSequences</c> category: which step of each asked-about quest the
    /// journal is currently on, keyed by quest id.
    /// </summary>
    /// <remarks>
    /// A zero quest id is dropped for the same reason <see cref="Ids"/> drops zeroes: the server
    /// requires positive ids, and one invalid entry would reject the entire upload. An empty map is
    /// a legitimate result ("every asked-about quest was checked; none is in the journal") and is
    /// deliberately different from a skip.
    /// </remarks>
    public static CollectResult Sequences(IReadOnlyDictionary<uint, byte> sequences)
    {
        var positiveIdSequences = new Dictionary<uint, byte>(sequences.Count);
        foreach (var (questId, sequence) in sequences)
        {
            if (questId != 0)
                positiveIdSequences[questId] = sequence;
        }

        return new() { Facts = SyncFacts.Sequences(positiveIdSequences) };
    }

    /// <summary>
    /// Facts for the <c>items</c> category, along with per-source scan status (which containers were
    /// live, cached, or unscanned).
    /// </summary>
    /// <param name="items">The item possession facts.</param>
    /// <param name="sourceNotes">
    /// Per-source scan status, or null when no status is reported. Source-keyed: any collector may
    /// report on any source without special-casing by category.
    /// </param>
    public static CollectResult Items(
        IReadOnlyList<ItemPossession> items,
        IReadOnlyDictionary<string, ItemSourceStatus>? sourceNotes) =>
        new() { Facts = SyncFacts.Items(items), SourceNotes = sourceNotes };
}
