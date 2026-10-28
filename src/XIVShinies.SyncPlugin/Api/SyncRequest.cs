using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>What caused this upload. Serialized as a lowercase string per the contract.</summary>
public enum SyncTrigger
{
    /// <summary>The periodic full sweep, or held facts going up on their own.</summary>
    Interval,

    /// <summary>A full sweep shortly after the character finished loading.</summary>
    Login,

    /// <summary>The user pressed "Sync now".</summary>
    Manual,

    /// <summary>A real-time unlock event fired in game.</summary>
    Unlock,
}

/// <summary>
/// The body of <c>POST /api/plugin/v1/sync</c>: a collection snapshot for one character.
/// </summary>
public sealed record SyncRequest
{
    /// <summary>SHA-256 of the character's ContentId, lowercase hex. The raw id never travels.</summary>
    public required string CharacterContentIdHash { get; init; }

    /// <summary>Sent for the server's first-upload binding; a 403 echoes it back.</summary>
    public required string CharacterName { get; init; }

    /// <summary>The character's home world name.</summary>
    public required string HomeWorld { get; init; }

    /// <summary>This plugin's version string.</summary>
    public required string PluginVersion { get; init; }

    /// <summary>
    /// The <c>manifestVersion</c> from <c>/config</c> that the items list was built against. Optional:
    /// null omits the key from the JSON.
    /// </summary>
    public string? ManifestVersion { get; init; }

    /// <summary>What prompted this upload.</summary>
    public required SyncTrigger Trigger { get; init; }

    /// <summary>
    /// The facts being uploaded, keyed by category, such as <c>"quests"</c> or <c>"items"</c>.
    /// </summary>
    /// <remarks>
    /// A dictionary, so a new collection is one new collector and nothing branches on a category name.
    /// <b>A category that could not be read is absent, never an empty array</b>: absence means "not
    /// read this time", an empty array means "read, and it was empty" (the monotonic-write rule).
    /// Values are <see cref="JsonNode"/> because shapes differ per category, and a node serializes
    /// exactly as built.
    /// </remarks>
    public required Dictionary<string, JsonNode> Collections { get; init; }

    /// <summary>
    /// Per-category completeness declarations (<c>"full"</c>, per <see cref="CollectionScopeValues"/>),
    /// or null when there is nothing to declare. An omitted key means "partial" on the server.
    /// </summary>
    /// <remarks>
    /// The one way an id's absence from a list becomes meaningful: <c>"full"</c> asserts the list is
    /// the character's complete set, so the server may surface a manual mark the list contradicts. The
    /// server never infers completeness, so an undeclared upload carries no evidence of absence. Each
    /// collection declares its own and the builder passes it through, so nothing branches on a name.
    /// </remarks>
    public Dictionary<string, string>? CollectionScopes { get; init; }

    /// <summary>
    /// Per-source scan status (inventory live, retainers cached, armoire loaded), or null. Optional on
    /// the wire: omitted when null or empty.
    /// </summary>
    /// <remarks>
    /// It tells the server how each storage location was read, which makes a low or zero item count
    /// judgeable. Source-keyed, never category-keyed (see <see cref="SourceKeys"/>).
    /// </remarks>
    public Dictionary<string, ItemSourceStatus>? ItemSources { get; init; }
}

/// <summary>
/// The values the contract accepts in <see cref="SyncRequest.CollectionScopes"/>. Anything else is
/// rejected with a 400.
/// </summary>
public static class CollectionScopeValues
{
    /// <summary>This list is the character's complete set for its category at upload time.</summary>
    public const string Full = "full";

    /// <summary>
    /// Anything less than complete. An omitted key already means this, so the plugin never sends it;
    /// it documents the contract's full value set.
    /// </summary>
    public const string Partial = "partial";
}

/// <summary>
/// Builds the <see cref="JsonNode"/> values that collectors place into
/// <see cref="SyncRequest.Collections"/>.
/// </summary>
public static class SyncFacts
{
    /// <summary>Facts for a category that is a plain list of unlocked/completed IDs.</summary>
    // A node rather than a string, so the whole request serializes once.
    public static JsonNode Ids(IReadOnlyList<uint> ids) =>
        JsonSerializer.SerializeToNode(ids, ApiJson.Options)!;

    /// <summary>Facts for the <c>items</c> category, which carries objects rather than IDs.</summary>
    public static JsonNode Items(IReadOnlyList<ItemPossession> items) =>
        JsonSerializer.SerializeToNode(items, ApiJson.Options)!;

    /// <summary>
    /// Facts for the <c>questSequences</c> category: which step of each asked-about quest the
    /// journal is currently on, as an object keyed by quest id.
    /// </summary>
    /// <remarks>
    /// Built by hand so the ids become plain decimal strings (<c>"70562"</c>), which a dictionary-key
    /// policy could reshape.
    /// </remarks>
    public static JsonNode Sequences(IReadOnlyDictionary<uint, byte> sequences)
    {
        var facts = new JsonObject();
        foreach (var (questId, sequence) in sequences)
        {
            // Widened to int: an in-memory JsonValue keeps its CLR type, so a byte-backed node
            // refuses GetValue<int>() until serialized. The wire output is the same.
            facts[questId.ToString(CultureInfo.InvariantCulture)] = (int)sequence;
        }

        return facts;
    }

    /// <summary>
    /// Facts for the <c>occultProgression</c> category: per-job phantom job progress keyed by
    /// <c>MKDSupportJob</c> row id, plus the optional knowledge sighting.
    /// </summary>
    /// <remarks>
    /// Built by hand so the job ids become plain decimal strings. The knowledge key is present only
    /// with an observation, since the contract reads its absence as "never sighted", and its
    /// <c>observedAt</c> must be second-exact UTC with a trailing <c>Z</c>, never a numeric offset.
    /// </remarks>
    public static JsonNode Progression(
        IReadOnlyDictionary<byte, OccultJobProgress> jobs,
        KnowledgeObservation? knowledge)
    {
        // The schema's ceiling for a job's exp.
        const int maxExp = 100_000_000;

        var jobsNode = new JsonObject();
        foreach (var (jobId, progress) in jobs)
        {
            // An exp past the ceiling is a misread, so the job is left out ("not read"). Clamping
            // would pin a maximum the server never walks back; sending it raw fails the whole upload.
            if (progress.Exp > maxExp)
                continue;

            jobsNode[jobId.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                // Widened to int so the node answers GetValue<int>() before serialization.
                ["exp"] = (int)progress.Exp,
                ["level"] = (int)progress.Level,
            };
        }

        var facts = new JsonObject { ["jobs"] = jobsNode };

        if (knowledge is not null)
        {
            facts["knowledge"] = new JsonObject
            {
                ["level"] = (int)knowledge.Level,
                ["observedAt"] = WireTime.Format(knowledge.ObservedAt),
            };
        }

        return facts;
    }

    /// <summary>
    /// Facts for the <c>achievementProgress</c> category: one object per reading, with exactly the
    /// contract's four keys.
    /// </summary>
    /// <remarks>Built by hand so <c>observedAt</c> goes through <see cref="WireTime"/>.</remarks>
    public static JsonNode AchievementProgress(IReadOnlyList<AchievementProgressReading> readings)
    {
        var facts = new JsonArray();
        foreach (var reading in readings)
        {
            facts.Add(new JsonObject
            {
                ["id"] = reading.Id,
                ["current"] = reading.Current,
                ["max"] = reading.Max,
                ["observedAt"] = WireTime.Format(reading.ObservedAt),
            });
        }

        return facts;
    }

    /// <summary>
    /// Facts for the <c>tamedBeasts</c> category, which carries objects rather than bare IDs.
    /// </summary>
    public static JsonNode TamedBeasts(IReadOnlyList<TamedBeast> beasts) =>
        JsonSerializer.SerializeToNode(beasts, ApiJson.Options)!;
}

/// <summary>A beast the character has forged a pact with, and what is known about that pact.</summary>
/// <remarks>
/// The contract gives a pact a rank and a battlehorn slot as well as its existence. An unread field is
/// left off, as the contract expects; no source the plugin reads carries the battlehorn slot, so it is
/// never sent.
/// </remarks>
public sealed record TamedBeast
{
    /// <summary>
    /// The bestiary number, which is the game's <c>XBMPet</c> sheet row id and the id the server's
    /// catalog is keyed on.
    /// </summary>
    public required uint Number { get; init; }

    /// <summary>The lowest rank the contract accepts.</summary>
    public const int MinRank = 1;

    /// <summary>The highest rank the contract accepts.</summary>
    public const int MaxRank = 25;

    /// <summary>
    /// The beast's rank, <see cref="MinRank"/> to <see cref="MaxRank"/>, or null when unread.
    /// </summary>
    /// <remarks>A null is left off the wire, not written as <c>"rank": null</c>.</remarks>
    public int? Rank { get; init; }
}

/// <summary>How many of a manifest item the character possesses.</summary>
public sealed record ItemPossession
{
    /// <summary>The item ID, taken from the server's manifest.</summary>
    // The contract requires a positive integer. Unsigned rules out negatives, and
    // ItemTallies.BuildPossessions skips a zero id.
    public required uint Id { get; init; }

    /// <summary>
    /// How many normal-quality copies are held. Zero is a reported fact: it proves no relic step, but
    /// it sets a count-tracked item's total to zero (an omit-when-unseen id's zero is dropped instead).
    /// </summary>
    public required uint Count { get; init; }

    /// <summary>
    /// How many high-quality copies are held, or null when none; null is omitted from the JSON.
    /// </summary>
    /// <remarks>
    /// <c>count</c> stays normal-quality only. The plugin reports each quality raw and never sums them;
    /// the server decides whether HQ satisfies a requirement.
    /// </remarks>
    public uint? HqCount { get; init; }

    /// <summary>How many collectable-quality copies are held, or null when none.</summary>
    /// <remarks>
    /// A collectable is the same item id with a collectability flag, never also HQ; turn-in materials
    /// such as Gobbiegoo exist only in this form.
    /// </remarks>
    public uint? CollectableCount { get; init; }

    /// <summary>
    /// False when the count came from a cache rather than a live container read. The server ignores
    /// it, since a stale positive is still a positive, but it is reported honestly.
    /// </summary>
    public required bool Fresh { get; init; }
}
