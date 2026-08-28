using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// One category's contribution to an upload: its wire key, how many facts went out, a short
/// content fingerprint, whether its scope comes from the server's item manifest, and — for a
/// manifest-driven category — how many of its entries the character holds a copy of.
/// </summary>
/// <remarks>
/// The fingerprint exists because the count alone cannot see an exchange: swapping one fact for
/// another leaves the count identical while the payload's contents change. It is a hash of the
/// facts, so the log still carries no ids — just enough to answer "did this category's contents
/// change since last time?".
/// <para>
/// <paramref name="Count"/> is null when the pass read none of what the category is about while
/// still having something to send (see <see cref="CollectResult.NothingReadThisPass"/>). That is
/// not the same as a count of zero: zero is a measurement, null is the absence of one. A null
/// count makes no claim about the category's contents, so the content diff passes over it; a
/// manifest-driven category is still compared on its owned-entry count.
/// </para>
/// <para>
/// <paramref name="UsesItemManifest"/> mirrors the collector's own
/// <see cref="Collectors.ICollector.UsesItemManifest"/> flag. It decides the category's change
/// signal: a manifest-driven category's contents move whenever the server edits the manifest, so
/// a content diff cannot tell "the player obtained something" from "the manifest grew". Its
/// signal compares <paramref name="OwnedCount"/> instead — how many manifest entries the
/// character holds at least one copy of, in any quality. That number ignores manifest growth
/// with items the character lacks (a newly asked-about item adds an unowned entry) and ignores
/// balance movement on items already held (a currency ticking between two positive values), so
/// when it moves, the set of items the plugin could see the character holding changed. Usually
/// that is a pickup — but a storage source becoming readable mid-session (opening the armoire,
/// glamour dresser, or saddlebag for the first time), a consent-group toggle, or a manifest
/// edit touching an item already held moves it too, so the mark reads "your visible holdings
/// changed", not strictly "you looted something". Null for every other category, and for a
/// manifest-driven one whose facts were not the possession shape — with nothing honest to
/// compare, no flag is shown.
/// </para>
/// </remarks>
// A "positional record": the parameter list declares init-only properties and a constructor in
// one line — the C# shorthand for a tiny immutable data shape.
public sealed record UploadLogCategory(
    string Key,
    int? Count,
    string Fingerprint = "",
    bool UsesItemManifest = false,
    int? OwnedCount = null);

/// <summary>
/// One upload, as shown in the settings window's upload log: when, why, what was sent, and how
/// the server answered.
/// </summary>
/// <remarks>
/// A transparency surface: it is built from the same snapshot the payload was built from, at the
/// moment the payload was assembled — never reconstructed after the fact. It carries category
/// <b>keys</b>, not names: the window maps keys to whatever the registered collectors call
/// themselves, so this type stays free of category-name knowledge.
/// </remarks>
public sealed record UploadLogEntry
{
    /// <summary>When the payload was assembled (UTC; the window renders it in local time).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>What prompted the upload.</summary>
    public required SyncTrigger Trigger { get; init; }

    /// <summary>
    /// The longest server-supplied string an entry will keep (the validation detail, the manifest
    /// version). The backend is user-overridable and therefore untrusted; entries persist for up
    /// to twenty uploads and render in ImGui, so adopted text is clamped at the door.
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

    /// <summary>
    /// The keys from <see cref="Skipped"/> that represent something going wrong, in key order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A category the user switched off is skipped, but nothing failed — they chose not to send
    /// it, and the consent list already shows that choice. Listing it as unread would report a
    /// deliberate decision as a fault, and every user who leaves a collection unticked would carry
    /// a permanent complaint in their log.
    /// </para>
    /// <para>
    /// Filtered on the REASON, never on a category, so nothing here knows which collections exist.
    /// The full <see cref="Skipped"/> map is left intact for the pasted diagnostic, where
    /// "disabled" is exactly the answer to "why did this not sync?".
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> UnreadableCategoryKeys
    {
        get
        {
            var keys = new List<string>(Skipped.Count);
            foreach (var (key, reason) in Skipped)
            {
                if (!string.Equals(reason, CollectSkipReasons.Disabled, StringComparison.Ordinal))
                    keys.Add(key);
            }

            // Sorted so the line reads the same twice running: the underlying map is a Dictionary,
            // whose enumeration order is not part of its contract.
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }

    // --- Failure diagnostics -----------------------------------------------------------------
    // Optional, and filled at settle time (except the manifest version, known at build time).
    // They exist for the pasted-into-Discord bug report: each answers a "why did it fail"
    // question the status alone cannot. None carries identity — the log must stay structurally
    // incapable of leaking who the character is.

    /// <summary>Which attempt settled the upload: 1 on the first try, 2 after a retry.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// The <c>/config</c> manifest version the items list was built against, or null when no
    /// config had been fetched — the first question when relic counts look wrong server-side.
    /// </summary>
    public string? ManifestVersion { get; init; }

    /// <summary>How long the server asked us to wait, on rate-limited/paused deferrals.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The literal HTTP status code, when a response arrived. The contract mapping erases it
    /// (a 502 from a proxy and a 418 both become <see cref="ApiStatus.Unknown"/>), and
    /// diagnostics need the real number.
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>
    /// The server's validation complaints on a rejected payload, flattened to one line — a 400
    /// is by definition a plugin bug, and this is the server saying exactly which field it hated.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The relic-step rows this upload created or promoted, from the response's
    /// <c>provenSteps</c> key — the server's answer to "did these items prove anything new?".
    /// Zero means the items were applied but nothing new was proved. Null means the server sent
    /// no answer: on an accepted upload that sent item facts, that is derivation failing
    /// server-side (the next upload retries it). A manifest-driven category that went out empty
    /// carries no information — the server applies nothing — so it is never owed an answer.
    /// </summary>
    public int? ProvenSteps { get; init; }

    /// <summary>
    /// Summarizes a collection snapshot into a draft entry, before the upload's outcome is known.
    /// Settle it once the response lands: <c>draft with { Status = response.Status, … }</c>.
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

            // No count at all where the collector read none of what it is about; otherwise the
            // collector's own count wins where it gave one, since it knows which part of its facts
            // is the thing being counted and the shape count below cannot (see
            // CollectResult.FactCount). Most categories give none, and are counted from shape.
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

            // A content hash (12 chars from our server), but the backend is user-overridable, so
            // clamp it like every other adopted server string before it persists in the log. No
            // ellipsis: this is a hash a reader checks back against the server's, so a trailing
            // marker would make it unrecognizable.
            ManifestVersion = manifestVersion is null ? null : ServerText.Clamp(manifestVersion),
        };
    }

    /// <summary>
    /// How many facts a category's JSON carries. Array categories (id lists, item-count objects)
    /// count their elements; object categories count their members, except that a member which
    /// is itself a container counts its own entries — so a flat map (quest id → sequence byte)
    /// counts one per quest, and a nested map (a "jobs" object of 24 per-job records) counts one
    /// per job rather than collapsing to a single fact. Shape alone decides; no category names.
    /// Any other shape counts as one fact rather than crashing or hiding it in the log.
    /// </summary>
    // A `switch` EXPRESSION: each arm is `pattern => value`, and `_` is the required
    // catch-all — like a chain of ternaries in JS, but the compiler checks the patterns.
    private static int CountFacts(JsonNode facts) => facts switch
    {
        JsonArray array => array.Count,
        JsonObject members => CountMembers(members),
        _ => 1,
    };

    /// <summary>
    /// The per-quality count members of the manifest possession shape
    /// (<see cref="Api.ItemPossession"/> on the wire) — every way an entry can hold copies.
    /// </summary>
    // If ItemPossession ever grows another quality, it must be added here too, or entries owned
    // only in that quality would read as unowned and their pickups would go unflagged.
    private static readonly string[] PossessionCountMembers = ["count", "hqCount", "collectableCount"];

    /// <summary>
    /// How many entries of a manifest possession array the character holds at least one copy of,
    /// in any quality — the number <see cref="UploadLogCategory.OwnedCount"/> carries. Null when
    /// the facts are not a possession array (not an array at all, or an array of something other
    /// than objects — an id list, say): there is no possession shape to count, and null keeps
    /// the change signal honestly silent instead of comparing a guess. An empty array counts as
    /// zero, not null — "collected, owns none of it" is a real baseline a first pickup can move.
    /// </summary>
    private static int? CountOwned(JsonNode facts)
    {
        if (facts is not JsonArray entries)
            return null;

        // Shape-sniff the first element, the same way PayloadCaps tells the two array shapes
        // apart: possession arrays are homogeneous, so a non-object first element means this
        // whole array is some other kind of list and a count of zero would be a lie, not an
        // answer.
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
            // The pattern handles an absent member (the indexer returns null); TryGetValue
            // handles a present member that is not a whole number a uint can hold — a string,
            // a negative, a fraction. Both simply mean "no copies in this quality". The uint
            // read works because possession facts come from SyncFacts.Items, which serializes
            // the DTOs with SerializeToNode and so hands back JsonElement-backed values; a
            // hand-built JsonValue remembers its source CLR type and can refuse the read —
            // the trap SyncFacts.Sequences documents where it widens a byte to int.
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
    /// A short, deterministic hash of a category's facts. Collectors build their facts in a
    /// stable order (game sheets iterate in ascending row order), so identical contents always
    /// hash identically — and any change, even one that leaves the count the same, changes the
    /// hash.
    /// </summary>
    /// <remarks>
    /// The stability depends on that ordering: if a source ever reordered identical facts, the
    /// hash would change and the log would show a spurious "(changed)". Cosmetic-only — the
    /// payload and the diff of real content are unaffected — but worth knowing if a gold
    /// highlight ever appears with no visible difference.
    /// </remarks>
    private static string Fingerprint(JsonNode facts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(facts.ToJsonString()));

        // Eight hex characters: not a security boundary, just a change detector — 32 bits is
        // plenty to make an accidental collision between two consecutive uploads implausible.
        // (Same hex idiom as ContentIdHash, for one way of doing things across the codebase.)
        return Convert.ToHexStringLower(bytes.AsSpan(0, 4));
    }
}

/// <summary>
/// The most recent uploads, newest first — what the settings window's upload log renders.
/// </summary>
/// <remarks>
/// In memory only, deliberately: persisting a history of what a character owns would be a new
/// data store to disclose, for a feature whose job is transparency about the current session.
/// Bounded so a long session cannot grow it forever.
/// </remarks>
public sealed class UploadLog
{
    /// <summary>How many entries are kept before the oldest falls off.</summary>
    public const int Capacity = 20;

    // Readers get whatever list this reference points at; writers publish a NEW list and swap
    // the reference. A reference swap is atomic in .NET, so the draw thread can read Entries at
    // any moment without locks — at worst it sees the list from one moment earlier. `volatile`
    // keeps either thread from caching a stale reference.
    private volatile IReadOnlyList<UploadLogEntry> entries = Array.Empty<UploadLogEntry>();

    // Guards the WRITERS against each other (readers need no lock — see above). Record runs on
    // the upload task and Clear on the draw thread; without this, a Clear landing between
    // Record's read of the old list and its swap would be undone — the freshly built list still
    // contains the entries the user just cleared.
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

    /// <summary>Empties the log — the settings window's clear button.</summary>
    public void Clear()
    {
        lock (writeLock)
        {
            entries = Array.Empty<UploadLogEntry>();
        }
    }
}

/// <summary>
/// Compares log entries so the window can highlight what changed — the "you just got something
/// new" signal beside a count.
/// </summary>
public static class UploadLogDiff
{
    /// <summary>
    /// The category keys in <c>newestFirst[index]</c> whose signal moved against that category's
    /// most recent earlier appearance in the log — a different count or fingerprint, or, for a
    /// manifest-driven category, a different owned-entry count.
    /// </summary>
    /// <remarks>
    /// The baseline is the nearest OLDER entry that MEASURED the category — not simply the
    /// previous entry, and not merely one that mentions it: an unlock upload carries only the
    /// categories that changed, and a carried category may go out with no reading of its own.
    /// A category the log has never seen before is not flagged — with no baseline, "changed"
    /// would be a guess, and it would paint the whole first upload of every session.
    /// Manifest-driven categories compare their owned-entry count rather than their contents —
    /// <see cref="UploadLogCategory"/> explains why a content diff cannot carry their signal —
    /// and stay unflagged when either side has no owned count to offer.
    /// </remarks>
    public static IReadOnlySet<string> ChangedCategories(
        IReadOnlyList<UploadLogEntry> newestFirst, int index)
    {
        var changed = new HashSet<string>();

        foreach (var category in newestFirst[index].Categories)
        {
            // `is not { } baseline` is a null test and an unwrap in one: the loop moves on when
            // no baseline exists, and every line past it has `baseline` as the record inside
            // the nullable.
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

            // A category that read nothing this pass makes no claim about its contents, so it
            // cannot have changed. The baseline it would otherwise be compared against is the last
            // pass that measured, and "different from a reading taken earlier" is not movement.
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
    /// The category's entry in the nearest log row older than <paramref name="index"/> that
    /// measured it, or null when no older row did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows that carried the category without measuring it are passed over, not treated as the
    /// baseline: they hold nothing to compare against. Skipping them means the last row that
    /// actually measured stays the point of comparison, so an unmeasured pass in the middle
    /// neither hides a real change nor invents one.
    /// </para>
    /// <para>
    /// Which number counts as the measurement depends on the category, so the caller's own
    /// category decides it — a manifest-driven one is compared on its owned-entry count, every
    /// other on its fact count (<see cref="UploadLogCategory"/> explains why they differ).
    /// </para>
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
    /// The outcome, one short phrase. "Refused" means the user must fix something; "deferred"
    /// means the plugin will simply try again later; "failed" covers everything else.
    /// </summary>
    public static string StatusText(ApiStatus status) => status switch
    {
        ApiStatus.Ok => "accepted",
        ApiStatus.CharacterNotClaimed => "refused — character not claimed",
        ApiStatus.InvalidToken => "refused — token rejected",
        ApiStatus.RateLimited => "deferred — rate limited",
        ApiStatus.SyncDisabled => "deferred — syncing paused by the server",
        ApiStatus.NetworkError => "failed — could not reach the server",
        _ => "failed",
    };

    /// <summary>True only for an accepted upload — the one outcome the log paints green.</summary>
    public static bool IsSuccess(ApiStatus status) => status == ApiStatus.Ok;

    /// <summary>
    /// The full Outcome-column text: the status phrase plus its qualifiers — the wait the server
    /// asked for, and the attempt number when a retry was needed. Qualifiers appear only when
    /// they carry information, so the common first-try success stays one clean word.
    /// </summary>
    public static string OutcomeText(UploadLogEntry entry)
    {
        var text = StatusText(entry.Status);

        if (entry.RetryAfter is { } wait)
            text += $" — retry in {(int)wait.TotalSeconds}s";

        if (entry.Attempt > 1)
            text += $" (attempt {entry.Attempt})";

        return text;
    }

    /// <summary>
    /// Flattens a rejected payload's validation complaints to one line, or null when the server
    /// sent none — form-level errors first, then each failing field with its messages.
    /// </summary>
    /// <remarks>
    /// The strings come from the backend, which is user-overridable and therefore not trusted:
    /// the result is truncated so a hostile server cannot make a log entry (kept for up to 20
    /// uploads and rendered in ImGui) arbitrarily large. The response body is already capped
    /// upstream; this is defense in depth at the point the text is adopted.
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
                // A null array is an explicit JSON null, which gets past `required` (see ApiJson)
                // — and Join throws on a null collection even though it tolerates null elements.
                parts.Add($"{field}: {string.Join("; ", messages ?? Array.Empty<string>())}");
            }
        }

        if (parts.Count == 0)
            return null;

        var text = string.Join(" · ", parts);

        // Folded as well as bounded: this is drawn in the log table, where a newline inside a
        // server complaint would push the rest of the cell out of sight. SingleLine marks a cut
        // the same way, and its null-for-blank answer matches this method's contract.
        return ServerText.SingleLine(text);
    }

    /// <summary>
    /// True when the outcome is only a delay the plugin handles by itself (it will retry later).
    /// The log draws these at the normal text color: they need no action, unlike refusals, which
    /// render red.
    /// </summary>
    public static bool IsDeferral(ApiStatus status) =>
        status is ApiStatus.RateLimited or ApiStatus.SyncDisabled or ApiStatus.NetworkError;

    /// <summary>
    /// The note the window prints beside a manifest-driven category, from the server's proof
    /// answer — or null when there is nothing worth saying.
    /// </summary>
    /// <remarks>
    /// A manifest-driven category's "(changed)" mark tracks possession only (see
    /// <see cref="UploadLogDiff.ChangedCategories"/>); this note is the server's side of the
    /// story — what the sent items proved. The cases:
    /// steps were proved → say how many; zero proved → silence (the items applied, nothing new —
    /// no note is the honest rendering); no answer on an accepted upload that sent item facts →
    /// "proof pending", because derivation failed server-side and the next upload retries it. An
    /// upload that was not accepted was never owed an answer, and neither was one whose
    /// manifest-driven categories were all empty — the contract treats an empty array as "no
    /// information", so the server applies nothing and correctly stays silent. Both return null.
    /// </remarks>
    public static string? ProofText(UploadLogEntry entry)
    {
        if (entry.Status != ApiStatus.Ok)
            return null;

        if (entry.ProvenSteps is { } steps)
        {
            if (steps == 0)
                return null;

            // "New" is load-bearing: the server's number is the delta this upload proved (rows
            // created plus promoted), never a running total, and the wording must not read as one.
            return steps == 1 ? "1 new step proven" : $"{steps} new steps proven";
        }

        return CarriesManifestDrivenFacts(entry) ? "proof pending" : null;
    }

    /// <summary>
    /// The entry's categories ordered by the name the reader sees, for the window's Sent column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settings checklist and the sync card's chips both name the collections alphabetically, and
    /// this column names the same collections a third time. Ordering all three the same way is what
    /// lets a reader compare them by position instead of hunting for a name in each list.
    /// </para>
    /// <para>
    /// A COPY is returned rather than the list being sorted in place. The entry records the order the
    /// collectors ran in, which is a fact about the upload, and the clipboard export prints the
    /// categories in exactly that order for whoever is debugging a payload — sorting the entry itself
    /// would quietly rewrite that surface too.
    /// </para>
    /// </remarks>
    /// <param name="categories">The entry's categories, in the order the collectors ran.</param>
    /// <param name="displayNameFor">
    /// The label the window draws for a category key. Passed in rather than looked up here so this
    /// stays Dalamud-free and testable: the names come from the registered collectors, which only the
    /// window can reach. Ordering on this rather than on the key matters because a key is a wire
    /// identifier that need not resemble its label, so a key order would put the words on screen in an
    /// order the reader cannot account for. Compared with OrdinalIgnoreCase: these labels are English
    /// strings authored in this repo, so every user gets the same order whatever their locale.
    /// </param>
    public static IReadOnlyList<UploadLogCategory> InDisplayOrder(
        IReadOnlyList<UploadLogCategory> categories,
        Func<string, string> displayNameFor)
    {
        var ordered = new List<UploadLogCategory>(categories);

        // A category key is unique within an entry, so the key breaks a tie between two collections
        // that chose the same display name. List.Sort is unstable, so without a tie-break their
        // order could differ between two runs over equal input.
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
    /// The same rule as <see cref="InDisplayOrder"/> and for the same reason — the two lines sit in
    /// one table cell, so a reader meets both orders at once. Keys rather than categories because
    /// an unreadable category has nothing recorded about it beyond its key: it is named precisely
    /// because nothing was collected for it.
    /// </remarks>
    /// <param name="categoryKeys">The unreadable category keys.</param>
    /// <param name="displayNameFor">The label the window draws for a category key.</param>
    public static IReadOnlyList<string> KeysInDisplayOrder(
        IReadOnlyList<string> categoryKeys,
        Func<string, string> displayNameFor)
    {
        var ordered = new List<string>(categoryKeys);

        // Tie broken by the key, as in InDisplayOrder.
        ordered.Sort((left, right) =>
        {
            var byName = string.Compare(
                displayNameFor(left), displayNameFor(right), StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(left, right, StringComparison.Ordinal);
        });

        return ordered;
    }

    /// <summary>
    /// The text spans the window prints for one sent category, each paired with whether it draws
    /// highlighted (gold). A span-based answer because one category can carry two independently
    /// colored parts: the label with its "(changed)" mark, and — for a manifest-driven category —
    /// the server's proof note beside it.
    /// </summary>
    /// <remarks>
    /// The highlight rules: "(changed)" always highlights its span — that is the "you just got
    /// something" signal. The proof note highlights only when steps were actually proved;
    /// "proof pending" is not good news and stays plain even when it lands next to a highlighted
    /// change mark. The proof note only ever attaches to a manifest-driven category — the server
    /// answers about sent items, so it would be noise beside any other category.
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
        // No count means the pass read none of what this category is about, so the column names
        // that state instead of printing a number. Deliberately not worded around "read": that
        // word belongs to the categories that never went out at all, both in this window's
        // "Could not read:" line and in the settings panel's "not read yet" hints. This category
        // was sent — the plugin simply saw none of the collection it is about.
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
    /// True when the entry sent at least one fact under a manifest-driven category. The count
    /// matters: an empty category carries no information, so the server applies nothing and owes
    /// no proof answer for it.
    /// </summary>
    /// <remarks>
    /// A measured zero and an absent count are decided differently on purpose. Zero is a reading —
    /// the category was measured and holds nothing — so the server owes no answer. Null is the
    /// absence of a reading, and the facts went out regardless, so an answer may well be owed;
    /// treating it as empty would silence the proof note exactly where a reader needs it.
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
    /// Renders the log as plain text for the clipboard, one line per upload — what a user pastes
    /// into Discord when reporting a problem.
    /// </summary>
    /// <remarks>
    /// Deliberately in wire terms — category keys, status enum names, UTC timestamps, invariant
    /// formatting — because the reader is whoever is debugging, and stable identifiers beat
    /// localized display copy there. Contains only what the log already shows: counts and
    /// outcomes, never the ids themselves.
    /// </remarks>
    public static string ClipboardText(
        string pluginVersion, string backendUrl, IReadOnlyList<UploadLogEntry> entries)
    {
        var text = new StringBuilder();

        // The backend matters because it is user-overridable: "you are pointed at the wrong
        // server" is a classic support case that is otherwise invisible in a pasted log.
        text.AppendLine($"XIV Shinies Sync v{pluginVersion} upload log — backend: {backendUrl}");

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            text.Append(entry.At.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            text.Append(" | ").Append(entry.Trigger);
            text.Append(" | ").Append(entry.Status);

            text.Append(" | sent:");
            foreach (var category in entry.Categories)
            {
                text.Append(' ').Append(category.Key).Append('=');

                // A word rather than an empty slot or a zero: the paste is a diagnostic, and "the
                // category went out carrying no reading" is exactly the fact a reader needs.
                // Underscored to sit alongside the skip reason codes further down the same line.
                text.Append(category.Count is { } sentCount
                    ? sentCount.ToString(CultureInfo.InvariantCulture)
                    : "none_seen");

                // A manifest category's "changed:" flag compares this owned-entry count, not the
                // fact count — printing it makes the flag (and its absence) verifiable from the
                // paste alone, which is this surface's whole job.
                if (category.OwnedCount is { } owned)
                    text.Append(" owned=").Append(owned);
            }

            if (entry.Skipped.Count > 0)
            {
                text.Append(" | skipped:");
                foreach (var (key, reason) in entry.Skipped)
                    text.Append(' ').Append(key).Append('=').Append(reason);
            }

            // The same fact the window's gold highlight shows, in text: which categories moved
            // against their previous appearance. The printed fact counts alone cannot carry
            // it — a one-for-one content swap leaves an ordinary category's count identical.
            var changed = UploadLogDiff.ChangedCategories(entries, index);
            if (changed.Count > 0)
            {
                text.Append(" | changed:");
                foreach (var key in changed)
                    text.Append(' ').Append(key);
            }

            // The server's proof answer, verbatim — including zero, which the window stays
            // silent about but a debugger wants to see. An accepted upload that sent item facts
            // and got NO answer is the derivation-failed case, and "absent" is exactly the fact
            // a pasted bug report needs. (An all-empty manifest-driven category was never owed
            // an answer, so it gets no marker — same rule as the window's "proof pending".)
            if (entry.ProvenSteps is { } steps)
                text.Append(" | provenSteps: ").Append(steps);
            else if (entry.Status == ApiStatus.Ok && CarriesManifestDrivenFacts(entry))
                text.Append(" | provenSteps: absent");

            // Diagnostics, only when they say something: a clean first-try success stays a clean
            // one-liner. The raw HTTP code is skipped on Ok — it can only be 200 there.
            if (entry.Attempt > 1)
                text.Append(" | attempt: ").Append(entry.Attempt);

            if (entry.RetryAfter is { } wait)
                text.Append(" | retryAfter: ").Append((int)wait.TotalSeconds).Append('s');

            if (entry.HttpStatusCode is { } code && entry.Status != ApiStatus.Ok)
                text.Append(" | http: ").Append(code);

            // On every line, not just failures: when relic counts look wrong server-side, the
            // question is which manifest the SUCCESSFUL upload counted against.
            if (entry.ManifestVersion is { } manifest)
                text.Append(" | manifest: ").Append(manifest);

            if (entry.Detail is { } detail)
                text.Append(" | issues: ").Append(detail);

            text.AppendLine();
        }

        return text.ToString();
    }
}
