using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>One category's part of an upload. The log keeps counts and a fingerprint, never ids.</summary>
/// <param name="Key">The category's wire key.</param>
/// <param name="Count">
/// How many facts went out, or null when the pass read none of what the category is about but still
/// sent something (see <see cref="CollectResult.NothingReadThisPass"/>). Null is no measurement, not
/// zero, so the content diff skips it.
/// </param>
/// <param name="Fingerprint">
/// A short hash of the facts. It catches a one-for-one swap that leaves the count unchanged.
/// </param>
/// <param name="UsesItemManifest">
/// Mirrors <see cref="Collectors.ICollector.UsesItemManifest"/>. Such a category's contents move
/// whenever the server edits the manifest, so its change signal compares
/// <paramref name="OwnedCount"/> instead of its contents.
/// </param>
/// <param name="OwnedCount">
/// For a manifest-driven category, how many entries the character holds at least one copy of, in any
/// quality. It ignores new unowned entries and balance changes on held items, so it moves when the
/// visible holdings change: usually a pickup, but also a first look in the armoire, glamour dresser or
/// saddlebag, a consent-group toggle, or a manifest edit to a held item. Null for every other
/// category, and when the facts are not the possession shape; then no change is flagged.
/// </param>
public sealed record UploadLogCategory(
    string Key,
    int? Count,
    string Fingerprint = "",
    bool UsesItemManifest = false,
    int? OwnedCount = null);

/// <summary>Which upload path an upload log row is about.</summary>
public enum UploadLogSource
{
    /// <summary>A collection sync to <c>POST /sync</c>: the log's ordinary row.</summary>
    Sync,

    /// <summary>
    /// The live Occult tracker's <c>POST /occult/instance-state</c>, logged only when a refusal
    /// halts it (see <see cref="UploadLogEntry.LiveHalt"/>).
    /// </summary>
    LiveTracker,

    /// <summary>
    /// The Crucible run sharing's <c>POST /crucible/observations</c>, logged only when a refusal
    /// halts it (see <see cref="UploadLogEntry.LiveHalt"/>).
    /// </summary>
    CrucibleRuns,
}

/// <summary>
/// One upload in the settings window's upload log: when, why, what was sent, and how the server
/// answered.
/// </summary>
/// <remarks>
/// For transparency, a sync row is built from the snapshot the payload was built from, when the
/// payload was assembled, never reconstructed later. A live tracker or Crucible sharing row records a
/// halting refusal instead (see <see cref="LiveHalt"/>). Rows carry category keys, not names, which
/// the window maps to the registered collectors' names, so this type knows no category names.
/// </remarks>
public sealed record UploadLogEntry
{
    /// <summary>
    /// When a sync row's payload was assembled, or a live tracker or Crucible sharing row's refusal
    /// arrived. UTC; the window shows local time.
    /// </summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Which upload path the row is about.</summary>
    public UploadLogSource Source { get; init; } = UploadLogSource.Sync;

    /// <summary>What prompted a sync upload; null on a live tracker or Crucible sharing row.</summary>
    // Required although nullable, so a sync row cannot leave it out by accident.
    public required SyncTrigger? Trigger { get; init; }

    /// <summary>
    /// The longest server-supplied string an entry keeps (the validation detail, the manifest
    /// version); see <see cref="ServerText.MaxAdoptedLength"/>.
    /// </summary>
    public const int MaxServerStringLength = ServerText.MaxAdoptedLength;

    /// <summary>
    /// How the attempt ended. A draft carries <see cref="ApiStatus.Unknown"/> until the response
    /// lands; only settled entries reach the log.
    /// </summary>
    public required ApiStatus Status { get; init; }

    /// <summary>What was sent, per category, in the order the collectors ran.</summary>
    public required IReadOnlyList<UploadLogCategory> Categories { get; init; }

    /// <summary>The categories this pass could not read, keyed by category, with the reason code.</summary>
    public required IReadOnlyDictionary<string, string> Skipped { get; init; }

    /// <summary>The keys from <see cref="Skipped"/> that mean something went wrong, in key order.</summary>
    /// <remarks>
    /// A skip the user chose, such as a switched-off category, is not a failure (see
    /// <see cref="CollectSkipReasons.IsDeliberate"/>). It filters on the reason, never the category,
    /// so it knows no collections. <see cref="Skipped"/> stays whole for the pasted diagnostic, where
    /// "disabled" answers "why did this not sync?".
    /// </remarks>
    public IReadOnlyList<string> UnreadableCategoryKeys
    {
        get
        {
            var keys = new List<string>(Skipped.Count);
            foreach (var (key, reason) in Skipped)
            {
                if (!CollectSkipReasons.IsDeliberate(reason))
                    keys.Add(key);
            }

            // Sorted so the line reads the same twice running: a Dictionary's order is not guaranteed.
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }

    // --- Failure diagnostics -----------------------------------------------------------------
    // Optional, and filled at settle time except the manifest version, known at build time. Each
    // answers a "why did it fail" question for the bug report a user pastes. None carries identity:
    // the log must be unable to leak who the character is.

    /// <summary>Which attempt settled the upload: 1 on the first try, 2 after a retry.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// The <c>/config</c> manifest version the items list was built against, or null when no config
    /// had been fetched. The first question when relic counts look wrong server-side.
    /// </summary>
    public string? ManifestVersion { get; init; }

    /// <summary>How long the server asked us to wait, on a rate-limited or paused deferral.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The literal HTTP status code, when a response arrived, for diagnostics
    /// (<see cref="ApiResponse{T}.HttpStatusCode"/> says why the contract status alone is not enough).
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>
    /// The server's validation complaints on a rejected payload, flattened to one line. A 400 is
    /// always a plugin bug, and this names the field the server rejected.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// How many relic-step rows this upload created or promoted, from the response's
    /// <c>provenSteps</c> key.
    /// </summary>
    /// <remarks>
    /// Zero means the items applied but proved nothing new. Null on an accepted upload that sent item
    /// facts means derivation failed server-side, and the next upload retries it. A manifest-driven
    /// category that went out empty carries no information, so the server applies nothing and owes
    /// no answer.
    /// </remarks>
    public int? ProvenSteps { get; init; }

    /// <summary>
    /// Summarizes a collection snapshot into a draft entry, before the outcome is known. Settle it
    /// when the response lands: <c>draft with { Status = response.Status, … }</c>.
    /// </summary>
    public static UploadLogEntry Draft(
        DateTimeOffset at,
        SyncTrigger trigger,
        CollectionSnapshot snapshot,
        string? manifestVersion = null)
    {
        var categories = new List<UploadLogCategory>(snapshot.Collections.Count);
        foreach (var (key, facts) in snapshot.Collections)
        {
            var manifestDriven = snapshot.ManifestDrivenKeys.Contains(key);

            // Null when the pass read none of what the category is about. Otherwise the collector's
            // own count when it gave one (see CollectResult.FactCount), else a shape count.
            var count = snapshot.NothingReadKeys.Contains(key)
                ? (int?)null
                : snapshot.FactCounts.TryGetValue(key, out var reported)
                    ? reported
                    : CountFacts(facts);

            categories.Add(new UploadLogCategory(
                key,
                count,
                Fingerprint(facts),
                manifestDriven,
                manifestDriven ? CountOwned(facts) : null));
        }

        return new UploadLogEntry
        {
            At = at,
            Trigger = trigger,
            Status = ApiStatus.Unknown,
            Categories = categories,
            Skipped = snapshot.Skipped,

            // Our server sends a 12-character hash, but this is clamped like any adopted server
            // string. No ellipsis: a reader checks it against the server's, and a marker would hide it.
            ManifestVersion = manifestVersion is null ? null : ServerText.Clamp(manifestVersion),
        };
    }

    /// <summary>
    /// The row for a live tracker or Crucible sharing upload refused in a way only the player can fix
    /// (a character refusal or a rejected token), which halts syncing as a /sync refusal does.
    /// </summary>
    /// <remarks>
    /// Those paths' ordinary uploads get no row: they are frequent, are not the player's collection,
    /// and succeed or back off on their own. A halt gets one because a player with only that sharing
    /// on has no sync row to explain why it stopped. It has no categories, so the change signal never
    /// uses it as a baseline.
    /// </remarks>
    /// <param name="source">The upload path the server refused.</param>
    /// <param name="at">When the refusal arrived.</param>
    /// <param name="status">The refusal's status.</param>
    /// <param name="httpStatusCode">The literal HTTP status, for the pasted diagnostic.</param>
    public static UploadLogEntry LiveHalt(
        UploadLogSource source, DateTimeOffset at, ApiStatus status, int? httpStatusCode) =>
        new()
        {
            At = at,
            Source = source,
            Trigger = null,
            Status = status,
            Categories = Array.Empty<UploadLogCategory>(),
            Skipped = new Dictionary<string, string>(),
            HttpStatusCode = httpStatusCode,
        };

    /// <summary>
    /// How many facts a category's JSON carries, judged by shape alone, never by category name.
    /// </summary>
    /// <remarks>
    /// An array counts its elements. An object counts its members, but a container member counts its
    /// own entries: a map of quest id to sequence counts one per quest, and a "jobs" object of per-job
    /// records counts one per job. Any other shape counts as one rather than crashing or hiding it.
    /// </remarks>
    private static int CountFacts(JsonNode facts) => facts switch
    {
        JsonArray array => array.Count,
        JsonObject members => CountMembers(members),
        _ => 1,
    };

    /// <summary>
    /// Every per-quality count member of the possession shape (<see cref="Api.ItemPossession"/> on
    /// the wire).
    /// </summary>
    // A quality added to ItemPossession must be added here, or an entry owned only in that quality
    // reads as unowned and its pickup goes unflagged.
    private static readonly string[] PossessionCountMembers = ["count", "hqCount", "collectableCount"];

    /// <summary>
    /// How many entries of a possession array the character holds at least one copy of, in any
    /// quality: the <see cref="UploadLogCategory.OwnedCount"/> value.
    /// </summary>
    /// <remarks>
    /// Null when the facts are not an array of objects, such as an id list, so the change signal stays
    /// silent rather than comparing a guess. An empty array counts zero, not null: "collected, owns
    /// none" is a real baseline a first pickup can move.
    /// </remarks>
    private static int? CountOwned(JsonNode facts)
    {
        if (facts is not JsonArray entries)
            return null;

        // Possession arrays are homogeneous, so a non-object first element means some other kind of
        // list, where a count of zero would be a lie.
        if (entries.Count > 0 && entries[0] is not JsonObject)
            return null;

        var owned = 0;
        foreach (var entry in entries)
        {
            if (entry is JsonObject possession && HoldsAnyCopy(possession))
                owned++;
        }

        return owned;
    }

    /// <summary>True when any of the entry's per-quality counts is positive.</summary>
    private static bool HoldsAnyCopy(JsonObject possession)
    {
        foreach (var member in PossessionCountMembers)
        {
            // An absent member, or one no uint can hold, means no copies. The uint read relies on the
            // JsonElement-backed values SyncFacts.Items builds; SyncFacts.Sequences says why.
            if (possession[member] is JsonValue value
                && value.TryGetValue(out uint count)
                && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One fact per scalar member; a container member contributes its own count.</summary>
    private static int CountMembers(JsonObject members)
    {
        var count = 0;
        foreach (var (_, value) in members)
        {
            count += value switch
            {
                JsonArray array => array.Count,
                JsonObject nested => nested.Count,
                _ => 1,
            };
        }

        return count;
    }

    /// <summary>
    /// A short, deterministic hash of a category's facts. Any change, even one that keeps the count,
    /// changes it.
    /// </summary>
    /// <remarks>
    /// Stable only because collectors build their facts in a stable order (game sheets iterate in
    /// ascending row order). A source that reordered identical facts would show a spurious
    /// "(changed)" with no visible difference; the payload is unaffected.
    /// </remarks>
    private static string Fingerprint(JsonNode facts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(facts.ToJsonString()));

        // Eight hex characters: a change detector, not a security boundary. 32 bits makes a
        // collision between two consecutive uploads implausible.
        return Convert.ToHexStringLower(bytes.AsSpan(0, 4));
    }
}

/// <summary>The most recent uploads, newest first: what the settings window's upload log renders.</summary>
/// <remarks>
/// In memory only: a persisted history of what a character owns would be a new data store to
/// disclose, for a log about the current session. Bounded so a long session cannot grow it forever.
/// </remarks>
public sealed class UploadLog
{
    /// <summary>How many entries are kept before the oldest falls off.</summary>
    public const int Capacity = 20;

    // Writers publish a new list and swap the reference, so the draw thread reads without a lock and
    // at worst sees the previous list.
    private volatile IReadOnlyList<UploadLogEntry> entries = Array.Empty<UploadLogEntry>();

    // Serializes the writers: the upload tasks, the clear button on the draw thread, logout, and the
    // Debug seeding. Two writers building from the same old list would lose one change.
    private readonly object writeLock = new();

    /// <summary>The recorded uploads, newest first. The returned list is never mutated.</summary>
    public IReadOnlyList<UploadLogEntry> Entries => entries;

    /// <summary>Adds a settled upload at the front, dropping the oldest entry past capacity.</summary>
    public void Record(UploadLogEntry entry)
    {
        lock (writeLock)
        {
            var current = entries;
            var next = new List<UploadLogEntry>(Math.Min(current.Count + 1, Capacity)) { entry };

            foreach (var existing in current)
            {
                if (next.Count >= Capacity)
                    break;

                next.Add(existing);
            }

            entries = next;
        }
    }

    /// <summary>
    /// Empties the log. Called by the settings window's clear button, at logout, and before the Debug
    /// build's screenshot seeding.
    /// </summary>
    public void Clear()
    {
        lock (writeLock)
        {
            entries = Array.Empty<UploadLogEntry>();
        }
    }
}

/// <summary>
/// Compares log entries so the window can highlight what changed: the "you just got something new"
/// signal beside a count.
/// </summary>
public static class UploadLogDiff
{
    /// <summary>
    /// The category keys in <c>newestFirst[index]</c> whose count or fingerprint, or for a
    /// manifest-driven category whose owned count, differs from the category's last measurement.
    /// </summary>
    /// <remarks>
    /// The baseline is the nearest older entry that measured the category, since an unlock upload
    /// carries only the categories that changed and a carried one may have no reading. A category
    /// with no baseline is not flagged: "changed" would be a guess that paints each session's first
    /// upload. A manifest-driven category stays unflagged when either side lacks an owned count (see
    /// <see cref="UploadLogCategory"/> for why it compares that).
    /// </remarks>
    public static IReadOnlySet<string> ChangedCategories(
        IReadOnlyList<UploadLogEntry> newestFirst, int index)
    {
        var changed = new HashSet<string>();

        foreach (var category in newestFirst[index].Categories)
        {
            if (Baseline(newestFirst, index, category) is not { } baseline)
                continue;

            if (category.UsesItemManifest)
            {
                if (baseline.OwnedCount is { } was
                    && category.OwnedCount is { } now
                    && was != now)
                {
                    changed.Add(category.Key);
                }

                continue;
            }

            // A pass that read nothing makes no claim about the contents, so it cannot have changed;
            // differing from an older reading is not movement.
            if (category.Count is null)
                continue;

            if (baseline.Count != category.Count
                || baseline.Fingerprint != category.Fingerprint)
            {
                changed.Add(category.Key);
            }
        }

        return changed;
    }

    /// <summary>
    /// The category's entry in the nearest row older than <paramref name="index"/> that measured it,
    /// or null when none did.
    /// </summary>
    /// <remarks>
    /// Rows that carried the category without measuring it are skipped, so an unmeasured pass in
    /// between neither hides a real change nor invents one. The caller's category decides what counts
    /// as measured: the owned count for a manifest-driven one, the fact count otherwise.
    /// </remarks>
    private static UploadLogCategory? Baseline(
        IReadOnlyList<UploadLogEntry> newestFirst, int index, UploadLogCategory category)
    {
        for (var older = index + 1; older < newestFirst.Count; older++)
        {
            foreach (var baseline in newestFirst[older].Categories)
            {
                if (baseline.Key != category.Key)
                    continue;

                var measured = category.UsesItemManifest
                    ? baseline.OwnedCount is not null
                    : baseline.Count is not null;

                if (measured)
                    return baseline;
            }
        }

        return null;
    }
}

/// <summary>Turns an upload log entry's enums into the words the window prints.</summary>
public static class UploadLogText
{
    /// <summary>
    /// What a live tracker or Crucible sharing row's Sent column says. Null for a sync row, which
    /// lists its categories instead.
    /// </summary>
    /// <param name="source">The row's upload path.</param>
    public static string? LiveSentText(UploadLogSource source) => source switch
    {
        UploadLogSource.LiveTracker => "Occult instance state",
        UploadLogSource.CrucibleRuns => "Crucible run snapshots",
        _ => null,
    };

    /// <summary>
    /// The endpoint a live tracker or Crucible sharing row uploaded to, in the contract's wire terms,
    /// for the pasted log. Null for a sync row.
    /// </summary>
    /// <param name="source">The row's upload path.</param>
    public static string? LiveEndpoint(UploadLogSource source) => source switch
    {
        UploadLogSource.LiveTracker => "occult/instance-state",
        UploadLogSource.CrucibleRuns => "crucible/observations",
        _ => null,
    };

    /// <summary>
    /// The Trigger column: the path's name for a live tracker or Crucible sharing row, the sync
    /// trigger for a sync row, and plain "sync" for a sync row that carries none.
    /// </summary>
    /// <remarks>
    /// Decided by <see cref="UploadLogEntry.Source"/> first, as <see cref="ClipboardText"/> is, so the
    /// table and the pasted log always agree on a row's path.
    /// </remarks>
    public static string TriggerText(UploadLogEntry entry) => entry.Source switch
    {
        UploadLogSource.LiveTracker => "live tracker",
        UploadLogSource.CrucibleRuns => "Crucible sharing",
        _ => entry.Trigger is { } trigger ? TriggerText(trigger) : "sync",
    };

    /// <summary>The trigger, as a person would say it.</summary>
    public static string TriggerText(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => "manual sync",
        SyncTrigger.Login => "login sync",
        SyncTrigger.Unlock => "new unlock",
        SyncTrigger.Interval => "scheduled sync",
        _ => "sync",
    };

    /// <summary>
    /// The outcome, one short phrase. "Refused" means the user must fix something; "deferred" means
    /// the plugin will try again later; "failed" covers everything else.
    /// </summary>
    /// <param name="status">The upload's outcome.</param>
    /// <param name="host">The configured website's address, named where the website is the subject.</param>
    public static string StatusText(ApiStatus status, string host) => status switch
    {
        ApiStatus.Ok => "accepted",
        ApiStatus.CharacterNotClaimed => "refused — character not claimed",
        ApiStatus.CharacterNotVerified => "refused — character not verified",
        ApiStatus.CharacterAmbiguous => "refused — character claimed more than once",
        ApiStatus.CharacterBoundElsewhere => "refused — character linked elsewhere",
        ApiStatus.InvalidToken => "refused — token rejected",
        ApiStatus.RateLimited => "deferred — rate limited",
        // Every 503 maps here, whatever sent it (see ApiStatus.SyncDisabled), so the phrase names no
        // cause. A pause is named on the settings card, which reads it from /config.
        ApiStatus.SyncDisabled => $"deferred — {host} is not taking uploads",
        ApiStatus.NetworkError => $"failed — could not reach {host}",
        _ => "failed",
    };

    /// <summary>True only for an accepted upload, the one outcome the log paints green.</summary>
    public static bool IsSuccess(ApiStatus status) => status == ApiStatus.Ok;

    /// <summary>
    /// The Outcome column: the status phrase, plus the wait the server asked for and, after a retry,
    /// the attempt number. A first-try success stays one word.
    /// </summary>
    /// <param name="entry">The logged upload.</param>
    /// <param name="host">The configured website's address (see <see cref="StatusText"/>).</param>
    public static string OutcomeText(UploadLogEntry entry, string host)
    {
        var text = StatusText(entry.Status, host);

        if (entry.RetryAfter is { } wait)
            text += $" — asked to wait {(int)wait.TotalSeconds}s";

        if (entry.Attempt > 1)
            text += $" (attempt {entry.Attempt})";

        return text;
    }

    /// <summary>
    /// Flattens a rejected payload's validation complaints to one line, form errors first, then each
    /// failing field with its messages. Null when the server sent none, or the line folds to nothing.
    /// </summary>
    /// <remarks>
    /// Bounded like every adopted server string (see <see cref="UploadLogEntry.MaxServerStringLength"/>).
    /// The response body is already capped upstream; this is defense in depth.
    /// </remarks>
    public static string? IssuesText(ErrorResponse? error)
    {
        if (error?.Issues is not { } issues)
            return null;

        var parts = new List<string>();

        if (issues.FormErrors is { } formErrors)
        {
            foreach (var formError in formErrors)
                parts.Add(formError);
        }

        if (issues.FieldErrors is { } fieldErrors)
        {
            foreach (var (field, messages) in fieldErrors)
            {
                // An explicit JSON null gets past `required` (see ApiJson), and Join throws on a null
                // collection though it tolerates null elements.
                parts.Add($"{field}: {string.Join("; ", messages ?? Array.Empty<string>())}");
            }
        }

        if (parts.Count == 0)
            return null;

        var text = string.Join(" · ", parts);

        // Folded as well as bounded: a newline would push the rest of the log cell out of sight.
        return ServerText.SingleLine(text);
    }

    /// <summary>
    /// True when the outcome is a delay the plugin retries by itself. The log draws these in the
    /// normal text color: they need no action, unlike refusals, which render red.
    /// </summary>
    public static bool IsDeferral(ApiStatus status) =>
        status is ApiStatus.RateLimited or ApiStatus.SyncDisabled or ApiStatus.NetworkError;

    /// <summary>
    /// The note the window prints beside a manifest-driven category, from the server's proof answer,
    /// or null when there is nothing worth saying.
    /// </summary>
    /// <remarks>
    /// The "(changed)" mark tracks possession (see <see cref="UploadLogDiff.ChangedCategories"/>);
    /// this note says what the sent items proved. It counts proved steps, stays silent on zero, and
    /// says "proof pending" when an upload owed an answer and got none (see
    /// <see cref="UploadLogEntry.ProvenSteps"/> for which uploads owe one).
    /// </remarks>
    public static string? ProofText(UploadLogEntry entry)
    {
        if (entry.Status != ApiStatus.Ok)
            return null;

        if (entry.ProvenSteps is { } steps)
        {
            if (steps == 0)
                return null;

            // "New" matters: the server's number is what this upload proved (rows created plus
            // promoted), never a running total, and the wording must not read as one.
            return steps == 1 ? "1 new step proven" : $"{steps} new steps proven";
        }

        return CarriesManifestDrivenFacts(entry) ? "proof pending" : null;
    }

    /// <summary>
    /// The entry's categories ordered by the name the reader sees, for the window's Sent column.
    /// </summary>
    /// <remarks>
    /// The settings checklist and the sync card's chips also name the collections alphabetically, so
    /// a reader can compare all three by position. It returns a copy: the entry keeps the order the
    /// collectors ran in, which the clipboard export prints for whoever is debugging a payload.
    /// </remarks>
    /// <param name="categories">The entry's categories, in the order the collectors ran.</param>
    /// <param name="displayNameFor">
    /// The label the window draws for a category key; only the window can reach the collectors' names.
    /// Sorting on the label, not the key, because a wire key need not resemble its label.
    /// OrdinalIgnoreCase: the labels are English strings from this repo, so every locale gets one order.
    /// </param>
    public static IReadOnlyList<UploadLogCategory> InDisplayOrder(
        IReadOnlyList<UploadLogCategory> categories,
        Func<string, string> displayNameFor)
    {
        var ordered = new List<UploadLogCategory>(categories);

        // A key is unique within an entry, so it breaks a tie between equal display names. List.Sort
        // is unstable, so without it their order could differ between runs.
        ordered.Sort((left, right) =>
        {
            var byName = string.Compare(
                displayNameFor(left.Key), displayNameFor(right.Key), StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
        });

        return ordered;
    }

    /// <summary>
    /// The category keys ordered by the name the reader sees, for the window's "Could not read" line.
    /// </summary>
    /// <remarks>
    /// Same order as <see cref="InDisplayOrder"/>, because both lines sit in one table cell. It takes
    /// keys because an unreadable category has nothing recorded but its key.
    /// </remarks>
    /// <param name="categoryKeys">The unreadable category keys.</param>
    /// <param name="displayNameFor">The label the window draws for a category key.</param>
    public static IReadOnlyList<string> KeysInDisplayOrder(
        IReadOnlyList<string> categoryKeys,
        Func<string, string> displayNameFor)
    {
        var ordered = new List<string>(categoryKeys);

        // The key breaks ties, as in InDisplayOrder.
        ordered.Sort((left, right) =>
        {
            var byName = string.Compare(
                displayNameFor(left), displayNameFor(right), StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(left, right, StringComparison.Ordinal);
        });

        return ordered;
    }

    /// <summary>
    /// The text spans the window prints for one sent category, each with whether it draws gold: the
    /// label with its "(changed)" mark, and for a manifest-driven category the proof note.
    /// </summary>
    /// <remarks>
    /// "(changed)" always highlights: it is the "you just got something" signal. The proof note
    /// highlights only when steps were proved, since "proof pending" is not good news. The note
    /// attaches only to a manifest-driven category, because the server answers about sent items.
    /// </remarks>
    /// <param name="displayName">The category's collector-declared display name.</param>
    /// <param name="category">The category as the log entry recorded it.</param>
    /// <param name="changed">Whether the diff flagged this category against its baseline.</param>
    /// <param name="proof">The entry's proof note (<see cref="ProofText"/>), or null.</param>
    /// <param name="stepsProven">Whether the server's answer proved at least one step.</param>
    public static IReadOnlyList<(string Text, bool Highlight)> SentSpans(
        string displayName,
        UploadLogCategory category,
        bool changed,
        string? proof,
        bool stepsProven)
    {
        // No count means the pass saw none of the collection. Not worded with "read", which names
        // categories that never went out ("Could not read:" here, "not read yet" in settings).
        var label = category.Count is { } count
            ? $"{displayName} {count:N0}"
            : $"{displayName} (none seen)";
        if (changed)
            label += " (changed)";

        if (category.UsesItemManifest && proof is not null)
            return [(label, changed), ($" ({proof})", stepsProven)];

        return [(label, changed)];
    }

    /// <summary>
    /// True when the entry sent at least one fact under a manifest-driven category, so the server
    /// owes a proof answer.
    /// </summary>
    /// <remarks>
    /// A measured zero is empty and owes no answer. A null count is not: the facts went out
    /// unmeasured, so an answer may be owed, and treating it as empty would hide the proof note where
    /// a reader needs it.
    /// </remarks>
    private static bool CarriesManifestDrivenFacts(UploadLogEntry entry)
    {
        foreach (var category in entry.Categories)
        {
            if (category.UsesItemManifest && category.Count is > 0 or null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Renders the log as plain text for the clipboard, one line per upload, for a user to paste into
    /// Discord when reporting a problem.
    /// </summary>
    /// <remarks>
    /// In wire terms (category keys, status enum names, UTC, invariant formatting), because the reader
    /// is debugging and stable identifiers beat display copy. It holds only what the log shows:
    /// counts and outcomes, never the ids.
    /// </remarks>
    public static string ClipboardText(
        string pluginVersion, string backendUrl, IReadOnlyList<UploadLogEntry> entries)
    {
        var text = new StringBuilder();

        // The backend is user-overridable, and "pointed at the wrong server" is a common support case
        // a paste would otherwise hide.
        text.AppendLine($"XIV Shinies Sync v{pluginVersion} upload log — backend: {backendUrl}");

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            text.Append(entry.At.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            // Source first, as TriggerText decides it.
            text.Append(" | ").Append(entry.Source != UploadLogSource.Sync
                ? entry.Source.ToString()
                : entry.Trigger?.ToString() ?? entry.Source.ToString());
            text.Append(" | ").Append(entry.Status);

            text.Append(" | sent:");
            if (UploadLogText.LiveEndpoint(entry.Source) is { } endpoint)
                text.Append(' ').Append(endpoint);

            foreach (var category in entry.Categories)
            {
                text.Append(' ').Append(category.Key).Append('=');

                // A word, not a blank or a zero: "went out with no reading" is the fact a debugger
                // needs. Underscored to match the skip reason codes on the same line.
                text.Append(category.Count is { } sentCount
                    ? sentCount.ToString(CultureInfo.InvariantCulture)
                    : "none_seen");

                // A manifest category's "changed:" flag compares this count, so printing it lets
                // the paste alone confirm the flag or its absence.
                if (category.OwnedCount is { } owned)
                    text.Append(" owned=").Append(owned);
            }

            if (entry.Skipped.Count > 0)
            {
                text.Append(" | skipped:");
                foreach (var (key, reason) in entry.Skipped)
                    text.Append(' ').Append(key).Append('=').Append(reason);
            }

            // The window's gold highlight, as text. The counts alone miss a one-for-one swap.
            var changed = UploadLogDiff.ChangedCategories(entries, index);
            if (changed.Count > 0)
            {
                text.Append(" | changed:");
                foreach (var key in changed)
                    text.Append(' ').Append(key);
            }

            // The proof answer verbatim, zero included, which the window hides. "absent" marks the
            // derivation-failed case, by the same rule as the window's "proof pending".
            if (entry.ProvenSteps is { } steps)
                text.Append(" | provenSteps: ").Append(steps);
            else if (entry.Status == ApiStatus.Ok && CarriesManifestDrivenFacts(entry))
                text.Append(" | provenSteps: absent");

            // Only when they say something, so a first-try success stays short. The HTTP code is
            // skipped on Ok, where it can only be 200.
            if (entry.Attempt > 1)
                text.Append(" | attempt: ").Append(entry.Attempt);

            if (entry.RetryAfter is { } wait)
                text.Append(" | retryAfter: ").Append((int)wait.TotalSeconds).Append('s');

            if (entry.HttpStatusCode is { } code && entry.Status != ApiStatus.Ok)
                text.Append(" | http: ").Append(code);

            // On every line: when relic counts look wrong, the question is which manifest the
            // successful upload counted against.
            if (entry.ManifestVersion is { } manifest)
                text.Append(" | manifest: ").Append(manifest);

            if (entry.Detail is { } detail)
                text.Append(" | issues: ").Append(detail);

            text.AppendLine();
        }

        return text.ToString();
    }
}
