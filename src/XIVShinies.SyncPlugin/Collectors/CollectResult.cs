using System.Collections.Generic;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>The skip reasons the runner and the collectors report.</summary>
public static class CollectSkipReasons
{
    /// <summary>The user or the server switched this category off.</summary>
    public const string Disabled = "disabled";

    /// <summary>The collector threw. Its facts are omitted; the rest of the snapshot proceeds.</summary>
    public const string CollectorError = "collector_error";

    /// <summary>The game data sheet could not be loaded.</summary>
    /// <remarks>
    /// <c>GetExcelSheet</c> throws for a sheet it cannot supply (missing, a patched column layout, a
    /// language the game lacks), so this reason takes a catch; an escaped throw records
    /// <see cref="CollectorError"/> instead. Keeping the two apart lets a pasted diagnostic tell an
    /// unloadable game sheet from a plugin bug.
    /// </remarks>
    public const string SheetUnavailable = "sheet_unavailable";

    /// <summary>
    /// The server's manifest for this collection (item ids, quest ids) has not arrived yet. An empty
    /// manifest is different: it means there is nothing to check.
    /// </summary>
    public const string NoRemoteConfig = "no_remote_config";

    /// <summary>
    /// The server answered <c>/config</c> without this collection's manifest field, so it does not ask
    /// for the collection. Unlike <see cref="NoRemoteConfig"/>, this is the server's whole answer.
    /// </summary>
    public const string ManifestNotOffered = "manifest_not_offered";

    /// <summary>The inventory is not readable, usually because no character is logged in.</summary>
    public const string InventoryUnavailable = "inventory_unavailable";

    /// <summary>
    /// The server offered consent groups for this collection and the user has none switched on.
    /// </summary>
    /// <remarks>
    /// A skip, not an empty result: no container was opened, so facts would claim a scan that did not
    /// happen.
    /// </remarks>
    public const string NoItemGroupsEnabled = "no_item_groups_enabled";

    /// <summary>
    /// The achievements list has not been requested from the server this session, so the game cannot
    /// say which are complete. Opening the Achievements window once fixes it.
    /// </summary>
    public const string AchievementListNotLoaded = "achievement_list_not_loaded";

    /// <summary>
    /// The character is outside an Occult Crescent instance, whose director holds this collection's
    /// source. A full sync run inside the Crescent reads it.
    /// </summary>
    public const string NotInOccultInstance = "not_in_occult_instance";

    /// <summary>
    /// Turns a skip reason into advice for the settings window, or null if it is not worth saying.
    /// </summary>
    /// <remarks>
    /// Keyed on the reason, never the category, so any collector reporting a reason gets its hint. Each
    /// string is a phrase drawn as "<c>{DisplayName}: {phrase}</c>" (see <see cref="ReadStatusView"/>)
    /// and must name what resolves the skip: an action in game or in settings, the condition a sync
    /// must run under, or what it waits on, since only a later sync reads the collection again. An
    /// unknown reason gets null rather than its raw wire code; the category still shows as unread.
    /// </remarks>
    /// <param name="reason">The skip reason.</param>
    /// <param name="host">The configured website's address, named by a hint that waits on it.</param>
    public static string? Describe(string reason, string host) => reason switch
    {
        AchievementListNotLoaded =>
            "not read yet — open your Achievements window in game once.",

        // A short visit can fall between full passes, so the hint names when a sync reads it.
        NotInOccultInstance =>
            "not read yet — the automatic sync or Sync now reads it while you are inside the Occult " +
            "Crescent.",

        // Names no category: every manifest-driven collection reports this reason.
        NoRemoteConfig =>
            $"not read yet — waiting for {host} to say what to look for.",

        InventoryUnavailable =>
            "not read yet — log in to a character so your inventory can be read.",

        NoItemGroupsEnabled =>
            "not read — none of its groups are switched on. Tick at least one under Collections to " +
            "include it.",

        // Decisions (see IsDeliberate) draw no line. "collector_error" and "sheet_unavailable" leave
        // the user nothing to do.
        _ => null,
    };

    /// <summary>
    /// True when the category was skipped by a decision rather than a failure: the user or the
    /// server switched it off, or the server answered without asking for it.
    /// </summary>
    /// <remarks>
    /// Neither the settings panel nor the upload log reports such a category as unread: "could not
    /// read" beside a decision reads as a fault, and would stay there for good.
    /// </remarks>
    /// <param name="reason">The skip reason.</param>
    public static bool IsDeliberate(string reason) => reason is Disabled or ManifestNotOffered;
}

/// <summary>
/// What a collector produced: either facts, or a reason it could not read them.
/// </summary>
/// <remarks>
/// Facts, even an empty list, mean the source was read and this is what it held. A skip omits the
/// category from the upload, and the server reads absence as no information, never as empty, so a
/// skip never erases anything.
/// </remarks>
public sealed record CollectResult
{
    // Callers go through the factories, so a result never holds both facts and a skip reason, or
    // neither.
    private CollectResult()
    {
    }

    /// <summary>Why the source could not be read, or null when facts were collected.</summary>
    public string? SkipReason { get; private init; }

    /// <summary>The collected facts as JSON, or null when the collector skipped.</summary>
    public JsonNode? Facts { get; private init; }

    /// <summary>
    /// Per-source scan status (inventory live, saddlebag unscanned, retainers cached), or null when
    /// none is reported. Valid only when <see cref="WasCollected"/> is true.
    /// </summary>
    /// <remarks>
    /// Keyed by storage location, never by category, so nothing downstream branches on which collector
    /// wrote it. These are one collector's notes; the runner merges them all into the snapshot.
    /// </remarks>
    public IReadOnlyDictionary<string, ItemSourceStatus>? SourceNotes { get; private init; }

    /// <summary>True when facts were read (possibly an empty list).</summary>
    public bool WasCollected => SkipReason is null && Facts is not null;

    /// <summary>
    /// True when these facts claim to be the character's complete set for the category, sent as its
    /// <c>"full"</c> scope (see <see cref="Api.SyncRequest.CollectionScopes"/>).
    /// </summary>
    /// <remarks>
    /// Only <see cref="Ids"/> and <see cref="TamedBeasts"/> take the claim, passed on from
    /// <see cref="CategoryInfo.EnumeratesCompleteDomain"/>; row 0 is sheet padding, not a candidate, so
    /// a read without it is still complete. An empty list never carries the claim, whatever the
    /// collector asked: "owns nothing" and "not filled in yet" read the same, and an empty list declared
    /// complete would contradict every entry the user marked by hand.
    /// </remarks>
    public bool CompleteEnumeration { get; private init; }

    /// <summary>
    /// A phrase for the read-status panel when this pass read only part of the category, or null.
    /// </summary>
    /// <remarks>
    /// Meaningful only on a collected result. Like a skip hint, it is drawn as
    /// "<c>{DisplayName}: {phrase}</c>" and names the unread half and what reads it: an in-game action,
    /// or the condition a sync must run under. The orchestrator keeps the latest per category and the
    /// panel prints it as handed, filling in the website's address for
    /// <see cref="HostPlaceholder.Token"/>.
    /// </remarks>
    public string? PartialNote { get; private init; }

    /// <summary>
    /// Hover copy for the category's healthy read-status chip, or null when the chip needs none.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PartialNote"/>, this is information a reader can live without, which is all a
    /// chip's hover may carry (see <see cref="SourceNote"/>). It shows only on the healthy chip,
    /// which a skip reason or partial note replaces, and names the website as
    /// <see cref="HostPlaceholder.Token"/>.
    /// </remarks>
    public string? CollectedDetail { get; private init; }

    /// <summary>
    /// How many things these facts are about, for the upload log's "Sent" column, or null to let the
    /// log count the facts' entries.
    /// </summary>
    /// <remarks>
    /// Counting entries suits facts that are the collection. For a wrapper holding unrelated things it
    /// sums them into a number that means nothing, and only the collector knows what to count.
    /// </remarks>
    public int? FactCount { get; private init; }

    /// <summary>
    /// True when this pass read nothing the category is about but still had something to send, so the
    /// upload log names the category without a count.
    /// </summary>
    /// <remarks>
    /// A zero count reads as a loss for a collection that cannot shrink. The category still went out
    /// with whatever else its facts hold (a remembered observation, say), so "unsent" would be wrong too.
    /// </remarks>
    public bool NothingReadThisPass { get; private init; }

    /// <summary>The source could not be read; omit this category from the upload.</summary>
    /// <param name="reason">
    /// A short, stable, machine-readable reason, for example <c>"achievement_list_not_loaded"</c>.
    /// The settings UI turns it into a hint, and the upload log uses it to tell a decision from a miss
    /// (see <see cref="CollectSkipReasons.IsDeliberate"/>).
    /// </param>
    public static CollectResult Skipped(string reason) => new() { SkipReason = reason };

    /// <summary>Facts for a category that is a plain list of unlocked or completed IDs.</summary>
    /// <param name="ids">The collected ids.</param>
    /// <param name="completeEnumeration">
    /// True when the read answered for every candidate in the domain (see
    /// <see cref="CategoryInfo.EnumeratesCompleteDomain"/>). Defaults to false, the safe claim.
    /// </param>
    /// <remarks>
    /// Drops zero: the server requires positive ids, and game sheets start with a blank padding row 0.
    /// One zero fails validation and rejects the entire upload, every category, so filtering here means
    /// no collector can forget. An item count of zero, in <see cref="Items"/>, is legitimate.
    /// </remarks>
    public static CollectResult Ids(IReadOnlyList<uint> ids, bool completeEnumeration = false)
    {
        var positiveIds = new List<uint>(ids.Count);
        foreach (var id in ids)
        {
            if (id != 0)
                positiveIds.Add(id);
        }

        // The empty-list floor; see CompleteEnumeration.
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
    /// No id filtering: the map is keyed by <c>MKDSupportJob</c> row id, and row 0 (Freelancer) is a
    /// real job. No completeness claim either: the category is a map, and scopes describe id lists.
    /// </remarks>
    /// <param name="jobs">The per-job progress, keyed by <c>MKDSupportJob</c> row id.</param>
    /// <param name="knowledge">The knowledge sighting to ride along, or null when none is held.</param>
    /// <param name="partialNote">See <see cref="PartialNote"/>; null when nothing partial to say.</param>
    /// <param name="collectedDetail">
    /// See <see cref="CollectedDetail"/>; null when the chip needs none.
    /// </param>
    public static CollectResult Progression(
        IReadOnlyDictionary<byte, OccultJobProgress> jobs,
        KnowledgeObservation? knowledge,
        string? partialNote = null,
        string? collectedDetail = null)
    {
        var facts = SyncFacts.Progression(jobs, knowledge);

        // Counted from the built facts, not the jobs passed in: a job whose exp passes the schema's
        // ceiling is dropped on the way to the wire, and the log reports what shipped.
        var sentJobs = facts["jobs"]!.AsObject().Count;

        return new CollectResult
        {
            Facts = facts,
            PartialNote = partialNote,
            CollectedDetail = collectedDetail,

            // Jobs only: counting entries would add the knowledge sighting's fields to the total.
            FactCount = sentJobs > 0 ? sentJobs : null,

            // No jobs sent means none were readable: the character was outside the instance, or every
            // job passed the exp ceiling. Shares FactCount's number, so exactly one describes a pass.
            NothingReadThisPass = sentJobs == 0,
        };
    }

    /// <summary>
    /// Facts for the <c>questSequences</c> category: which step of each asked-about quest the
    /// journal is currently on, keyed by quest id.
    /// </summary>
    /// <remarks>
    /// Drops a zero quest id, as <see cref="Ids"/> does. An empty map is a legitimate result, not a
    /// skip: every asked-about quest was checked and none is in the journal.
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
    /// Per-source scan status, or null when none is reported. Keyed by source, so any collector may
    /// report on any source.
    /// </param>
    public static CollectResult Items(
        IReadOnlyList<ItemPossession> items,
        IReadOnlyDictionary<string, ItemSourceStatus>? sourceNotes) =>
        new() { Facts = SyncFacts.Items(items), SourceNotes = sourceNotes };

    /// <summary>
    /// Facts for the <c>tamedBeasts</c> category: the beasts the character has forged a pact with.
    /// </summary>
    /// <param name="numbers">The bestiary numbers to report.</param>
    /// <param name="completeEnumeration">
    /// True when the whole bestiary has been accounted for, so an absent number is evidence the beast
    /// is not held. Decided per pass, because the bestiary is readable only once the player has looked
    /// at it. Defaults to false.
    /// </param>
    /// <param name="collectedDetail">See <see cref="CollectedDetail"/>; null when none.</param>
    /// <param name="partialNote">See <see cref="PartialNote"/>; null when nothing partial to say.</param>
    /// <param name="ranks">
    /// Each beast's rank by bestiary number; empty or null when none has been read.
    /// </param>
    /// <remarks>
    /// Non-positive numbers, and ranks outside <see cref="TamedBeast.MinRank"/> to
    /// <see cref="TamedBeast.MaxRank"/>, are dropped as in <see cref="Ids"/>; a beast still goes up
    /// without its rank. A rank for a beast the list does not name is not sent: the list is what says a
    /// beast is held. An empty list reports as nothing read this pass rather than a count of zero, and
    /// never carries the completeness claim.
    /// </remarks>
    public static CollectResult TamedBeasts(
        IReadOnlyList<int> numbers,
        bool completeEnumeration = false,
        string? collectedDetail = null,
        string? partialNote = null,
        IReadOnlyDictionary<int, int>? ranks = null)
    {
        var beasts = new List<TamedBeast>(numbers.Count);
        foreach (var number in numbers)
        {
            if (number <= 0)
                continue;

            int? rank = null;
            if (ranks is not null
                && ranks.TryGetValue(number, out var read)
                && read is >= TamedBeast.MinRank and <= TamedBeast.MaxRank)
            {
                rank = read;
            }

            beasts.Add(new TamedBeast { Number = (uint)number, Rank = rank });
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
