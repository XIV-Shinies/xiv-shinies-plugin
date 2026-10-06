using System.Collections.Generic;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// The skip reasons the plugin produces, runner and collectors alike, kept together so
/// <see cref="Describe"/> can turn each into advice.
/// </summary>
public static class CollectSkipReasons
{
    /// <summary>The user or the server switched this category off.</summary>
    public const string Disabled = "disabled";

    /// <summary>The collector threw. Its facts are omitted; the rest of the snapshot proceeds.</summary>
    public const string CollectorError = "collector_error";

    /// <summary>The game data sheet could not be loaded.</summary>
    /// <remarks>
    /// Reaching this reason takes a catch rather than a null check: <c>GetExcelSheet</c> reports a
    /// sheet it cannot supply by throwing — missing, a column layout the game patched out from
    /// under the bindings, a language it does not carry. A collector that let the throw escape
    /// would instead be recorded as <see cref="CollectorError"/>, which asserts the collector
    /// itself misbehaved. Both read the same to the user; keeping them apart is what lets a pasted
    /// diagnostic tell an unloadable game sheet from a plugin bug.
    /// </remarks>
    public const string SheetUnavailable = "sheet_unavailable";

    /// <summary>
    /// A read from the game did not have the shape this plugin's layout expects — the appearance
    /// category's customization bytes at the wrong size, for one, or a glamour dresser reading the
    /// glamour category cannot interpret (see <see cref="GlamourSnapshot.Build"/>). The game
    /// and the plugin disagree about the layout, which means one of them was updated without the
    /// other.
    /// </summary>
    /// <remarks>
    /// A skip rather than a best guess: with the shape wrong, which value means what is unknown, so
    /// nothing from the read can be trusted. Kept apart from <see cref="CollectorError"/> for the
    /// same reason as <see cref="SheetUnavailable"/>, so a pasted diagnostic tells a game and plugin
    /// version mismatch from a plugin bug. <see cref="Describe"/> offers no advice for it, because
    /// the fix is an updated plugin, not anything done in game.
    /// </remarks>
    public const string UnexpectedLayout = "unexpected_layout";

    /// <summary>
    /// The server has not told us what this collection should look for yet — its manifest (item
    /// ids, quest ids) has not been received. Distinct from "the manifest is empty", which means
    /// there is genuinely nothing to check.
    /// </summary>
    public const string NoRemoteConfig = "no_remote_config";

    /// <summary>The inventory is not readable — usually because no character is logged in.</summary>
    public const string InventoryUnavailable = "inventory_unavailable";

    /// <summary>
    /// A logged-in character's storage could not be read as a whole this pass: a bag, the equipped
    /// set or an armory chest was not loaded, or a retainer the item finder remembers had no
    /// inventory behind it.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="InventoryUnavailable"/>, whose hint tells the player to log in,
    /// because the player here already is. These states pass on their own, so
    /// <see cref="Describe"/> offers no advice, and the next pass reads the storage again.
    /// </remarks>
    public const string StorageUnreadable = "storage_unreadable";

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
    /// The character is not inside an Occult Crescent instance, where this collection's source
    /// (the instance director's state) lives. The user fixes this by entering the Crescent;
    /// the settings UI turns this reason into that hint.
    /// </summary>
    public const string NotInOccultInstance = "not_in_occult_instance";

    /// <summary>
    /// A storage window is open — the Glamour Dresser or the outfit-glamour window opened from it,
    /// the Armoire, the saddlebag, or a retainer's — so this pass does not read the category. The
    /// user fixes this by closing the window and pressing Sync now; the settings UI turns this
    /// reason into that hint.
    /// </summary>
    /// <remarks>
    /// While one is open, the copies the category reads can lag the live containers;
    /// <see cref="GlamourCollector"/>'s class remarks say why.
    /// </remarks>
    public const string StorageWindowOpen = "storage_window_open";

    /// <summary>
    /// No character is loaded, so there is no local player to read — at the title screen, or
    /// between logging out and the next login. The user fixes this by logging in; the settings UI
    /// turns this reason into that hint.
    /// </summary>
    /// <remarks>
    /// For a collection that reads the character itself. <see cref="InventoryUnavailable"/> is the
    /// reason for a collection that reads containers, and its hint names the inventory.
    /// </remarks>
    public const string LocalPlayerUnavailable = "local_player_unavailable";

    /// <summary>
    /// The character was transformed when this pass ran, so the category was not read. The user
    /// fixes this by returning to normal and pressing Sync now; the settings UI turns this reason
    /// into that hint.
    /// </summary>
    /// <remarks>
    /// <see cref="AppearanceCollector"/>'s class remarks say why a transformed character is not
    /// read.
    /// </remarks>
    public const string Transformed = "transformed";

    /// <summary>
    /// A list a snapshot category read this pass is longer than the server accepts, or a held count
    /// higher than it accepts, so the whole category is withheld rather than cut down or clamped to
    /// fit.
    /// </summary>
    /// <remarks>
    /// <see cref="GlamourSnapshot"/>'s class remarks say why a value past a ceiling is taken for a
    /// misread and why the whole category is withheld. <see cref="Describe"/> offers no advice for
    /// it: a misread is nothing the player can act on.
    /// </remarks>
    public const string OverCap = "over_cap";

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

        NotInOccultInstance =>
            "not read yet — enter the Occult Crescent once; it syncs during your visit.",

        // The hint names no category on purpose: every manifest-driven collection — item counts
        // and quest sequences alike — reports this same reason.
        NoRemoteConfig =>
            "not read yet — waiting for XIV Shinies to say what to look for.",

        InventoryUnavailable =>
            "not read yet — log in to a character so your inventory can be read.",

        NoItemGroupsEnabled =>
            "not read — none of its groups are switched on. Tick at least one under Collections to " +
            "include it.",

        // Every storage the player opens is named, because "a storage window" would leave the
        // player guessing which of the windows they have open is the one in the way. The
        // outfit-glamour window opens from the Glamour Dresser, so that name covers it.
        StorageWindowOpen =>
            "not read this pass — close your Glamour Dresser, Armoire, saddlebag or retainer " +
            "window, then press Sync now.",

        LocalPlayerUnavailable =>
            "not read yet — log in to a character so it can be read.",

        // Names the button as well as the return to normal: a skip stays on screen until a pass
        // reads the category, so the hint has to say how to start that pass.
        Transformed =>
            "not read this pass — your character was transformed; return to normal, then press " +
            "Sync now.",

        // "disabled" needs no explanation: the checkbox beside it already says so. "collector_error",
        // "sheet_unavailable", "unexpected_layout", "over_cap" and "storage_unreadable" are bugs,
        // misreads, version mismatches or transient game states the user cannot do anything about in
        // game.
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
    /// category: the read answered for every candidate the domain holds, and the list came back
    /// non-empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This claim ends up on the wire as the category's <c>collectionScopes</c> <c>"full"</c>
    /// declaration, which is what lets the server treat an id's absence as evidence — see
    /// <see cref="Api.SyncRequest.CollectionScopes"/> for what it licenses. It is declared per
    /// collection on <see cref="CategoryInfo.EnumeratesCompleteDomain"/> and carried through by the
    /// collector. Only the factories whose categories can enumerate a domain take the argument —
    /// <see cref="Ids"/> and <see cref="TamedBeasts"/> — so the others cannot declare it at all.
    /// False is always safe: it merely carries no evidence of absence.
    /// Sheet padding does not count against it — row 0 is not a candidate, so an enumeration is
    /// still complete without it.
    /// </para>
    /// <para>
    /// One floor is enforced centrally rather than left to collectors: <b>an empty list never
    /// carries the claim</b>, whatever the collector asked for. "Owns nothing" and "the game had
    /// not filled this in yet" produce the identical empty read, and an empty list declared
    /// complete is the one shape that contradicts every entry the user marked by hand at once.
    /// </para>
    /// </remarks>
    public bool CompleteEnumeration { get; private init; }

    /// <summary>
    /// A phrase for the settings read-status panel when this pass read only part of what the
    /// category covers, or null when there is nothing partial to say. Drawn after the category's
    /// display name ("<c>{DisplayName}: {phrase}</c>"), like a skip hint — it names the half
    /// that was not read and the in-game action that reads it.
    /// </summary>
    /// <remarks>
    /// Only meaningful on a collected result: a skipped category's whole story is its
    /// <see cref="SkipReason"/>. Self-description like everything else on this record — the
    /// runner files it under the collector's own key, the orchestrator remembers the latest one
    /// per category, and the panel prints whatever it is handed, so no consumer ever knows which
    /// collection is partially read.
    /// </remarks>
    public string? PartialNote { get; private init; }

    /// <summary>
    /// Hover copy for the category's healthy read-status chip, or null when the chip needs
    /// none. Where <see cref="PartialNote"/> is a visible line naming a required action, this
    /// is optional information a reader can live without — which is exactly what the chip's
    /// hover is allowed to carry (see <see cref="SourceNote.Detail"/>).
    /// </summary>
    /// <remarks>
    /// Only meaningful on a collected result, and only rendered while the category draws as the
    /// healthy chip: a skip reason or a partial note replaces the chip with its own line. The
    /// same self-description route as <see cref="PartialNote"/> — no consumer interprets it.
    /// </remarks>
    public string? CollectedDetail { get; private init; }

    /// <summary>
    /// How many things these facts are about, for the upload log's "Sent" column — or null to let
    /// the log count the facts itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log counts a category's facts from their shape: an array's entries, a map's entries.
    /// That is the right answer for a category whose facts ARE the collection. It is the wrong
    /// answer for one whose facts are a wrapper holding several unrelated things, where the shape
    /// count sums them into a number that means nothing to a reader.
    /// </para>
    /// <para>
    /// Self-description like <see cref="PartialNote"/> and <see cref="CollectedDetail"/>: only the
    /// collector knows which part of its facts is the thing being counted, so it says, and nothing
    /// downstream interprets it. Null is the ordinary case.
    /// </para>
    /// </remarks>
    public int? FactCount { get; private init; }

    /// <summary>
    /// True when this pass read none of what the category is about, though it still had something
    /// to send — so the upload log names the category without a count.
    /// </summary>
    /// <remarks>
    /// A count would have to be zero here, and zero is a claim: for a collection that cannot
    /// shrink, it reads as a loss rather than as "not read this time". The category still went
    /// out on the wire, carrying whatever else its facts hold (a remembered observation, say),
    /// so reporting it as unsent would be equally wrong.
    /// </remarks>
    public bool NothingReadThisPass { get; private init; }

    /// <summary>The source could not be read; omit this category from the upload.</summary>
    /// <param name="reason">
    /// A short, stable, machine-readable reason (for example <c>"achievement_list_not_loaded"</c>).
    /// The UI maps it to a hint; nothing else interprets it.
    /// </param>
    public static CollectResult Skipped(string reason) => new() { SkipReason = reason };

    /// <summary>Facts for a category that is a plain list of unlocked or completed IDs.</summary>
    /// <param name="ids">The collected ids.</param>
    /// <param name="completeEnumeration">
    /// True when the read answered for every candidate in the category's domain — see
    /// <see cref="CategoryInfo.EnumeratesCompleteDomain"/>, which is where each collection declares
    /// this — most collectors pass it straight through, while a collection whose domain only
    /// becomes readable on the player's action requires the pass to have earned it as well.
    /// A request, not a verdict: <see cref="CompleteEnumeration"/> additionally withholds the
    /// claim for an empty list. Defaults to false — the safe claim — so a caller must opt in
    /// explicitly.
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
    /// Facts for the <c>occultProgression</c> category: per-job phantom job progress, plus the
    /// optional knowledge sighting.
    /// </summary>
    /// <remarks>
    /// No id filtering: the map is keyed by <c>MKDSupportJob</c> row id, where 0 (Freelancer) is
    /// a real job the zero-drop protecting the id-list categories must not eat. No completeness
    /// claim either — the category is a map, and the scopes vocabulary speaks about id lists.
    /// </remarks>
    /// <param name="jobs">The per-job progress, keyed by <c>MKDSupportJob</c> row id.</param>
    /// <param name="knowledge">The knowledge sighting to ride along, or null when none is held.</param>
    /// <param name="partialNote">See <see cref="PartialNote"/>; null when nothing partial to say.</param>
    /// <param name="collectedDetail">See <see cref="CollectedDetail"/>; null when the chip needs none.</param>
    public static CollectResult Progression(
        IReadOnlyDictionary<byte, OccultJobProgress> jobs,
        KnowledgeObservation? knowledge,
        string? partialNote = null,
        string? collectedDetail = null)
    {
        var facts = SyncFacts.Progression(jobs, knowledge);

        // Counted off the BUILT facts, never off the jobs handed in: a job whose exp lands beyond
        // the schema's ceiling is omitted on the way to the wire, so the two numbers disagree
        // exactly when something went wrong. The log's whole job is to report what shipped, and a
        // misread that drops every job is the moment its honesty matters most.
        var sentJobs = facts["jobs"]!.AsObject().Count;

        return new CollectResult
        {
            Facts = facts,
            PartialNote = partialNote,
            CollectedDetail = collectedDetail,

            // The jobs alone. These facts are a wrapper around two unrelated things, so counting
            // their shape would add the knowledge sighting's own fields to the job total — a
            // number that grows and shrinks for reasons a reader cannot see. The jobs are what
            // this collection is about.
            FactCount = sentJobs > 0 ? sentJobs : null,

            // No jobs on the wire means none were readable this pass — the character was outside
            // the instance that holds them, or every job read past the exp ceiling and was dropped
            // on the way to the wire. Anything else the facts hold (a knowledge sighting, when one
            // is remembered) still travels. Derived from the same number as FactCount so the two
            // can never contradict: exactly one ever describes a pass.
            NothingReadThisPass = sentJobs == 0,
        };
    }

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

    /// <summary>
    /// Facts for the glamour category, with per-source scan status, counted by distinct piece.
    /// </summary>
    /// <param name="facts">
    /// The glamour snapshot, as built by <see cref="GlamourSnapshot.Build"/>. A container it holds
    /// as null was not read and stays off the wire.
    /// </param>
    /// <param name="sourceNotes">
    /// How each storage source was read this pass. Source-keyed like the items category's notes,
    /// because the two categories read the same physical storage.
    /// </param>
    /// <remarks>
    /// <para>
    /// <see cref="FactCount"/> is set because these facts are a wrapper around several lists (the
    /// dresser's loose pieces, its outfits, the Armoire and held gear), so counting their shape
    /// would add up unrelated things into a number that means nothing to a reader. The count is the
    /// number of distinct pieces the facts mention (see <see cref="GlamourSnapshot.CountPieces"/>):
    /// an item held in two places counts once.
    /// </para>
    /// <para>
    /// No completeness claim can be made here. The category is a snapshot of current holdings
    /// rather than an id list, and the completeness vocabulary speaks only about id lists.
    /// </para>
    /// </remarks>
    public static CollectResult Glamour(
        GlamourFacts facts,
        IReadOnlyDictionary<string, ItemSourceStatus> sourceNotes) =>
        new()
        {
            Facts = SyncFacts.Glamour(facts),
            SourceNotes = sourceNotes,
            FactCount = GlamourSnapshot.CountPieces(facts),
        };

    /// <summary>
    /// Facts for the <c>appearance</c> category: one record describing how the local character
    /// looks.
    /// </summary>
    /// <param name="facts">
    /// The appearance record, as built by
    /// <see cref="XIVShinies.SyncPlugin.Appearance.AppearanceSnapshot.Build"/>. It is passed
    /// through unchanged: the builder already wrote the exact keys the wire carries.
    /// </param>
    /// <remarks>
    /// <para>
    /// One record about the character, not a collection of things, so there is nothing for a count
    /// to count. <see cref="FactCount"/> stays null, and the upload log names the category without a
    /// number because the category describes itself as one record
    /// (<see cref="CategoryInfo.IsSingleRecord"/>).
    /// </para>
    /// <para>
    /// No completeness claim can be made here. The facts are a record rather than an id list, and
    /// the completeness vocabulary speaks only about id lists.
    /// </para>
    /// </remarks>
    // An expression-bodied member (`=>`): the method's whole body is the one expression after the
    // arrow, like a TypeScript arrow function that returns its expression without braces.
    public static CollectResult Appearance(JsonObject facts) => new() { Facts = facts };

    /// <summary>
    /// Facts for the <c>tamedBeasts</c> category: the beasts the character has forged a pact with.
    /// </summary>
    /// <param name="numbers">The bestiary numbers to report.</param>
    /// <param name="completeEnumeration">
    /// True when the character's whole bestiary has been accounted for, so an absent number is
    /// evidence the beast is not held. Decided per pass as well as per collection, because the
    /// bestiary becomes readable only once the player has looked at it — the same collection is
    /// incomplete before that and complete after. Defaults to false.
    /// </param>
    /// <param name="collectedDetail">See <see cref="CollectedDetail"/>; null when none.</param>
    /// <param name="partialNote">See <see cref="PartialNote"/>; null when nothing partial to say.</param>
    /// <remarks>
    /// <para>
    /// Non-positive numbers are dropped for the same reason <see cref="Ids"/> drops zeroes: the
    /// server requires positive integers, and one invalid entry rejects the whole upload rather
    /// than this one category.
    /// </para>
    /// <para>
    /// An empty list is reported as nothing read this pass rather than as a count of zero. Zero is
    /// a claim, and for a collection that cannot shrink it reads as a loss; "nothing seen yet" is
    /// what actually happened. Decided here rather than by the caller so the two can never
    /// disagree — and the emptiness floor on the completeness claim applies here exactly as it
    /// does to <see cref="Ids"/>.
    /// </para>
    /// </remarks>
    public static CollectResult TamedBeasts(
        IReadOnlyList<int> numbers,
        bool completeEnumeration = false,
        string? collectedDetail = null,
        string? partialNote = null)
    {
        var beasts = new List<TamedBeast>(numbers.Count);
        foreach (var number in numbers)
        {
            if (number > 0)
                beasts.Add(new TamedBeast { Number = (uint)number });
        }

        return new()
        {
            Facts = SyncFacts.TamedBeasts(beasts),
            CompleteEnumeration = completeEnumeration && beasts.Count > 0,
            CollectedDetail = collectedDetail,
            PartialNote = partialNote,
            NothingReadThisPass = beasts.Count == 0,
        };
    }
}
