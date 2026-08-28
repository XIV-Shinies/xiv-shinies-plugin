using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One row of the settings window's category list, as the window should draw it.
/// </summary>
/// <remarks>
/// The window renders these without knowing which collection each one is. That is the whole point:
/// a new collection appears in the settings UI by <i>existing</i>, not by being added to a list.
/// </remarks>
public sealed record CategorySettingsRow
{
    /// <summary>The category this row is about. The window uses it only to write the toggle back.</summary>
    public required string Key { get; init; }

    /// <summary>The label to draw beside the checkbox.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The heading this row is listed under, carried verbatim from the collector's
    /// self-description (see <see cref="CategoryInfo.Section"/>).
    /// </summary>
    public required string Section { get; init; }

    /// <summary>The plain-language description of what uploading this category sends.</summary>
    public required string WhatGetsSent { get; init; }

    /// <summary>
    /// The hover elaboration behind <see cref="WhatGetsSent"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Optional on the row for the same reason it is optional on the collector: a category whose
    /// one-liner already says everything has nothing to elaborate, and the window simply draws no
    /// hover affordance for it.
    /// </remarks>
    public string? Details { get; init; }

    /// <summary>Whether the user has opted this category in.</summary>
    public required bool UserEnabled { get; init; }

    /// <summary>
    /// True when the settings UI has never shown this category to this install, so it is worth
    /// badging as "New".
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="ItemGroupRow.IsNew"/>, one level up: a collection added in a
    /// later version is otherwise indistinguishable from one the user has been ignoring since
    /// install, and the settings' outer header folds, so it could go unnoticed entirely. Defaulted
    /// rather than required, unlike the group flag, so a caller building rows for a surface that
    /// does not badge (or a test that does not care) is not forced to answer.
    /// </remarks>
    public bool IsNew { get; init; }

    /// <summary>
    /// Whether this row's collector announced itself as manifest-driven (see
    /// <see cref="ICollector.UsesItemManifest"/>).
    /// </summary>
    /// <remarks>
    /// Carried on the row so a pure view can act on it without holding a collector, and it is what keeps
    /// <see cref="ReadStatusView"/> free of a category-name branch: the container lines belong to a
    /// manifest-driven collection, so the panel needs to know which row that is — to stand its container
    /// lines in for the collection's own line, and to drop them when no such collection is switched on.
    /// The per-group checkboxes are governed by the same flag one step earlier: <see cref="Groups"/> is
    /// only ever populated for a collector that announced it.
    /// </remarks>
    public required bool UsesItemManifest { get; init; }

    /// <summary>
    /// False when the server has switched this category off for everyone. The checkbox stays
    /// visible but disabled: the user's own preference is remembered and reapplied if the server
    /// turns it back on.
    /// </summary>
    public required bool ServerEnabled { get; init; }

    /// <summary>
    /// The server's own explanation for this category being switched off, or null when it offered
    /// none. Only meaningful while <see cref="ServerEnabled"/> is false.
    /// </summary>
    /// <remarks>
    /// The same no-name-branch route as <see cref="SkipReason"/> and <see cref="PartialNote"/>,
    /// one level further out: there the collector authors the phrase, here the server does, and
    /// the panel prints whichever it is handed. <see cref="ServerOffText"/> is what decides
    /// between this and the generic line.
    /// </remarks>
    public string? ServerNote { get; init; }

    /// <summary>
    /// What to print under a category the server has switched off, or null when it is on and
    /// nothing needs saying.
    /// </summary>
    /// <remarks>
    /// The server's note when it sent one, otherwise the generic line — see
    /// <see cref="ConfigResponse.CategoryNotes"/> for why only one of the two off-states carries a
    /// note. Null while the category is enabled, including when a note came with it: a note has
    /// nowhere to go under a live checkbox, so it is dropped here rather than special-cased at the
    /// place it arrives.
    /// </remarks>
    public string? ServerOffText => ServerEnabled ? null : ServerNote ?? ServerOffFallback;

    /// <summary>
    /// What a switched-off row says when the server offered no explanation of its own.
    /// </summary>
    /// <remarks>
    /// One string, used verbatim by every surface that reports something the server has switched
    /// off — the collections list and the live tracker's own row, which sit on the same screen.
    /// Two copies would let a reword leave them saying different things about the same state, in
    /// view of each other.
    /// </remarks>
    public const string ServerOffFallback = "Temporarily switched off by XIV Shinies.";

    /// <summary>
    /// Why the last collection pass skipped this category, or null if it did not.
    /// </summary>
    /// <remarks>
    /// This is how the "open your Achievements window once" hint reaches the UI without anyone
    /// writing <c>if (key == "achievements")</c>. The collector reports a reason; the window shows
    /// whatever reason it is given.
    /// </remarks>
    public string? SkipReason { get; init; }

    /// <summary>
    /// The category's latest partial-read phrase (see
    /// <see cref="CollectResult.PartialNote"/>), or null when its last read was whole. The same
    /// no-name-branch route as <see cref="SkipReason"/>: the collector authors the phrase, the
    /// panel prints it.
    /// </summary>
    public string? PartialNote { get; init; }

    /// <summary>
    /// The category's latest healthy-chip hover copy (see
    /// <see cref="CollectResult.CollectedDetail"/>), or null when its chip needs none. The same
    /// collector-authored route as <see cref="PartialNote"/>.
    /// </summary>
    public string? CollectedDetail { get; init; }

    /// <summary>True when this category will actually be uploaded as things stand.</summary>
    public bool IsEffectivelyOn => UserEnabled && ServerEnabled;

    /// <summary>
    /// False when the latest <c>/config</c> has not been fetched yet, so
    /// <see cref="ServerEnabled"/> is an assumption rather than the server's answer.
    /// </summary>
    /// <remarks>
    /// What tells a caller whether <see cref="ServerEnabled"/> may be spent on rather than merely
    /// drawn from. See <see cref="WasDrawnAsUsable"/>.
    /// </remarks>
    public bool ServerStateKnown { get; init; } = true;

    /// <summary>True when this row should announce itself as new.</summary>
    /// <remarks>
    /// A collection the server has switched off cannot be used, so badging it would say "here is
    /// something new for you" about something that is not. While the server's answer is still
    /// unknown the badge shows, because a user whose config poll is failing should still learn a
    /// collection exists.
    /// </remarks>
    public bool IsEffectivelyNew => IsNew && ServerEnabled;

    /// <summary>
    /// True when this row was drawn in a state the user could actually act on — the server
    /// permits it, and that is the server's answer rather than an assumption.
    /// </summary>
    /// <remarks>
    /// The server half of the retire condition, shared by this row (see
    /// <see cref="ShowingItRetiresTheBadge"/>) and by the groups beneath it. Retiring an
    /// announcement costs it outright, while showing one early costs only a repeat — so an
    /// unanswered <c>/config</c> is enough to draw on but not enough to spend on.
    /// </remarks>
    public bool WasDrawnAsUsable => ServerEnabled && ServerStateKnown;

    /// <summary>True when drawing this row on a badging surface should retire its badge.</summary>
    public bool ShowingItRetiresTheBadge => IsNew && WasDrawnAsUsable;

    /// <summary>
    /// One row per item-manifest consent group, for a manifest-driven collector — or null when this
    /// row has no groups to draw.
    /// </summary>
    /// <remarks>
    /// Null covers two different situations on purpose: the collector never announced itself as
    /// manifest-driven (see <see cref="ICollector.UsesItemManifest"/>), or the server has not sent any
    /// groups yet. Either way there is nothing to draw beneath this row, so the window does not need
    /// to tell them apart — it just checks for null.
    /// </remarks>
    public IReadOnlyList<ItemGroupRow>? Groups { get; init; }
}

/// <summary>
/// One row of a manifest-driven category's group list — one checkbox per named slice of the item
/// manifest, drawn beneath that category's row in the settings window.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Api.ItemManifestGroup"/> but adds the two things the server response does not
/// carry: whether <i>this</i> user has opted the group in, and whether the settings window has shown
/// it to them before. Keeping those two computed fields off the wire type is why this record exists
/// separately rather than reusing <see cref="Api.ItemManifestGroup"/> directly.
/// </remarks>
public sealed record ItemGroupRow
{
    /// <summary>The server's stable group key, used to read and write this group's consent.</summary>
    public required string Key { get; init; }

    /// <summary>The label to draw beside the group's checkbox.</summary>
    public required string Label { get; init; }

    /// <summary>Whether the user has opted this group in.</summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Whether the collection this group belongs to is one the server currently permits.
    /// </summary>
    /// <remarks>
    /// Carried down from the parent row so a group can answer the same question its parent does.
    /// A group is only ever scanned as part of its collection's pass, so a collection the server
    /// has switched off takes every group beneath it with it, whatever each group's own consent
    /// says.
    /// </remarks>
    public required bool ParentServerEnabled { get; init; }

    /// <summary>True when this group's items will actually be scanned as things stand.</summary>
    /// <remarks>
    /// What the checkbox draws: a ticked box under a parent drawn unticked, one indent below it,
    /// states the opposite of what is happening, and the parent is the one telling the truth. The
    /// stored consent is untouched and returns on its own.
    /// </remarks>
    public bool IsEffectivelyOn => Enabled && ParentServerEnabled;

    /// <summary>
    /// True when the settings window has never shown this group before, so it should carry a "New"
    /// badge. A group the server just added is new for everyone until each user's settings window has
    /// rendered it once.
    /// </summary>
    public required bool IsNew { get; init; }
}

/// <summary>
/// One heading's worth of the consent list: a section title and the rows listed under it.
/// </summary>
/// <remarks>
/// Produced by <see cref="CategorySettingsView.GroupBySection"/>. The consent surfaces draw a
/// plain label per section without ever knowing which sections exist.
/// </remarks>
public sealed record CategorySection
{
    /// <summary>The heading, exactly as the section's collectors declared it.</summary>
    public required string Title { get; init; }

    /// <summary>This section's rows, sorted by display name.</summary>
    public required IReadOnlyList<CategorySettingsRow> Rows { get; init; }
}

/// <summary>Which mark, if any, a collection's row wears at the end of its description.</summary>
public enum CategoryBadgeKind
{
    /// <summary>Nothing to say about this row beyond its own copy.</summary>
    None,

    /// <summary>The server has switched this collection off for everyone.</summary>
    Off,

    /// <summary>This settings window has never shown the collection before.</summary>
    New,
}

/// <summary>
/// Assembles the settings window's category list from the registered collectors.
/// </summary>
/// <remarks>
/// <para>
/// Pure and Dalamud-free, so the <b>extensibility contract is testable</b>: a fake collector for a
/// category this plugin has never heard of must flow through here and appear in the list, proving the
/// settings surface contains no category-name branch.
/// </para>
/// <para>
/// Note what is absent — there is no table of names, no ordering by category, and no special case for
/// any collection. Every row is built from what the collector says about itself.
/// </para>
/// </remarks>
public static class CategorySettingsView
{
    /// <summary>The mark a row should wear, given what the server says and what the user has seen.</summary>
    /// <remarks>
    /// <para>
    /// A row wears at most one mark, and "off" outranks "new". The two would otherwise be able to
    /// appear together on a collection that is both freshly added and switched off — and inviting
    /// the user to look at something they cannot use is worse than saying nothing, so the switched-off
    /// state is answered first and the announcement waits for a viewing the user can act on.
    /// </para>
    /// <para>
    /// Pure so the precedence is testable: a rule stated only inside a draw method is a rule no test
    /// can reach, and this one is a promise about what the user sees rather than an implementation
    /// detail. The window supplies the two facts it alone knows — whether this surface badges at all,
    /// and whether the key is already badged for this session — and turns the answer into a chip.
    /// </para>
    /// </remarks>
    /// <param name="row">The row being drawn.</param>
    /// <param name="showNewChips">
    /// Whether this surface announces new collections. The first-run wizard shows every collection
    /// by definition, so it badges nothing; the settings window does.
    /// </param>
    /// <param name="badgedThisSession">
    /// Whether this key has already been badged since the window opened. It keeps the chip on screen
    /// for the rest of the viewing after the row has been recorded as seen.
    /// </param>
    public static CategoryBadgeKind BadgeFor(
        CategorySettingsRow row, bool showNewChips, bool badgedThisSession)
    {
        // Re-checked rather than trusted from when the key was recorded: a config poll landing
        // mid-session can switch a collection off under a badge already on screen, and the chip must
        // not keep promising something new beside a greyed-out row.
        if (!row.ServerEnabled)
            return CategoryBadgeKind.Off;

        return showNewChips && badgedThisSession ? CategoryBadgeKind.New : CategoryBadgeKind.None;
    }

    /// <summary>
    /// True when anything in the consent list still counts as "New" — a whole collection, or a
    /// manifest group inside one.
    /// </summary>
    /// <remarks>
    /// A row or group qualifies either because this install has never shown it, or because its
    /// badge already went up during this session — in either case only while the server still
    /// permits the collection — the second half is what keeps a badge from
    /// vanishing one frame after it appears, since drawing it persists the seen flag and the next
    /// rebuild reports it un-new. The two session sets are parameters rather than state held here:
    /// they belong to a window's lifetime, and a pure function of its arguments is one the unit
    /// suite can reach.
    /// </remarks>
    /// <param name="rows">The category rows to scan, from <see cref="Build"/>.</param>
    /// <param name="badgedCategories">Category keys whose badge went up this session.</param>
    /// <param name="badgedGroups">Group keys whose badge went up this session.</param>
    public static bool AnythingIsNew(
        IReadOnlyList<CategorySettingsRow> rows,
        IReadOnlySet<string> badgedCategories,
        IReadOnlySet<string> badgedGroups)
    {
        foreach (var row in rows)
        {
            // A whole collection the user has never been shown counts, not just a group inside one
            // — with the header folded, a new collection is exactly as invisible as a new group.
            // The session set is re-checked against ServerEnabled, matching the row badge in
            // MainWindow.DrawCategoryRow.
            if (row.IsEffectivelyNew || (row.ServerEnabled && badgedCategories.Contains(row.Key)))
                return true;

            // A group under a collection the server has switched off is as unusable as the
            // collection, so it raises no chip either — otherwise the header would invite the user
            // to open a list where nothing is actionable.
            if (!row.ServerEnabled || row.Groups is not { Count: > 0 } groups)
                continue;

            foreach (var group in groups)
            {
                if (group.IsNew || badgedGroups.Contains(group.Key))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Builds one row per registered collector, in registration order.</summary>
    /// <param name="collectors">Every registered collector.</param>
    /// <param name="settings">The user's persisted opt-ins.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if it has not been fetched.</param>
    /// <param name="lastSkipped">
    /// Skip reasons from the most recent collection pass, keyed by category. Empty before the first
    /// pass, which simply means no row shows a hint yet.
    /// </param>
    /// <param name="lastPartialNotes">
    /// Each category's latest partial-read phrase, keyed by category. Empty before the first pass
    /// and for categories whose last read was whole.
    /// </param>
    /// <param name="lastCollectedDetails">
    /// Each category's latest healthy-chip hover copy, keyed by category. Empty before the first
    /// pass and for categories whose chip needs none.
    /// </param>
    public static IReadOnlyList<CategorySettingsRow> Build(
        IEnumerable<ICollector> collectors,
        PluginSettings settings,
        ConfigResponse? remoteConfig,
        IReadOnlyDictionary<string, string>? lastSkipped = null,
        IReadOnlyDictionary<string, string>? lastPartialNotes = null,
        IReadOnlyDictionary<string, string>? lastCollectedDetails = null)
    {
        var rows = new List<CategorySettingsRow>();

        foreach (var collector in collectors)
        {
            var key = collector.CategoryKey;

            // Read once and shared with the group rows below. The row's effective state and each
            // group's are two expressions of the same server answer, and the disabled scopes that
            // keep a click from rewriting stored consent assume the two agree — so they are given
            // no way to disagree.
            //
            // A config we have not fetched forbids nothing, matching how the collectors and the
            // upload gate treat it. Otherwise a plugin that cannot reach /config would show every
            // category as disabled by the server, which would be a lie.
            var serverEnabled = remoteConfig?.IsCategoryEnabled(key) ?? true;

            rows.Add(new CategorySettingsRow
            {
                Key = key,
                DisplayName = collector.DisplayName,
                Section = collector.Section,
                WhatGetsSent = collector.WhatGetsSent,
                Details = collector.Details,
                UserEnabled = settings.IsCategoryEnabled(key),

                // Never shown by this install, so the window may badge it. The window marks it seen
                // as it draws, which makes the next rebuild report false.
                IsNew = !settings.IsCategorySeen(key),

                // Carried through verbatim from the collector's own self-description. Nothing here
                // decides which collections are manifest-driven; the collector says so itself.
                UsesItemManifest = collector.UsesItemManifest,

                ServerEnabled = serverEnabled,

                // Carried verbatim from the server, bounded on the way in.
                ServerNote = remoteConfig?.CategoryNote(key),

                // Whether the line above is the server's answer or our assumption.
                ServerStateKnown = remoteConfig is not null,

                // `TryGetValue` fills the out parameter and returns whether the key existed. The
                // discard-style pattern below just means "null when it was not there".
                SkipReason = lastSkipped is not null && lastSkipped.TryGetValue(key, out var reason)
                    ? reason
                    : null,

                PartialNote = lastPartialNotes is not null
                    && lastPartialNotes.TryGetValue(key, out var partialNote)
                    ? partialNote
                    : null,

                CollectedDetail = lastCollectedDetails is not null
                    && lastCollectedDetails.TryGetValue(key, out var collectedDetail)
                    ? collectedDetail
                    : null,

                Groups = BuildGroupRows(collector, settings, remoteConfig, serverEnabled),
            });
        }

        return rows;
    }

    /// <summary>
    /// Buckets rows under the section titles their collectors declared, for the consent surfaces
    /// to draw one heading at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sections appear in the order their first row appears, so <see cref="CollectorRegistry"/>
    /// still decides which heading comes first. <b>Within</b> a section the rows are sorted by
    /// display name, so a reader scanning for one collection can find it where the alphabet says
    /// it is rather than wherever its registration line happened to be inserted.
    /// </para>
    /// <para>
    /// There is no fixed section list and no fallback bucket: whatever titles the rows carry are
    /// the sections that exist, which is what keeps this surface free of category knowledge. The
    /// sort is the same — it reads a name off the row, and never asks which collection it is.
    /// </para>
    /// </remarks>
    /// <param name="rows">The rows to group, from <see cref="Build"/>.</param>
    public static IReadOnlyList<CategorySection> GroupBySection(
        IReadOnlyList<CategorySettingsRow> rows)
    {
        // Two structures side by side: the list remembers first-appearance order (a Dictionary
        // alone promises no order), while the dictionary finds each section's bucket in one step.
        var sections = new List<(string Title, List<CategorySettingsRow> Rows)>();
        var byTitle = new Dictionary<string, List<CategorySettingsRow>>();

        foreach (var row in rows)
        {
            if (!byTitle.TryGetValue(row.Section, out var bucket))
            {
                bucket = new List<CategorySettingsRow>();
                byTitle[row.Section] = bucket;
                sections.Add((row.Section, bucket));
            }

            bucket.Add(row);
        }

        var result = new List<CategorySection>(sections.Count);
        foreach (var (title, bucketRows) in sections)
        {
            // OrdinalIgnoreCase rather than a culture-aware comparison: these labels are English
            // strings authored in this repo, and an ordinal sort gives every user the same order
            // regardless of their machine's locale — which is also what makes it testable. The
            // key breaks a tie, so two collections that chose the same display name still land in
            // a fixed order instead of an arbitrary one.
            bucketRows.Sort((left, right) =>
            {
                var byName = string.Compare(
                    left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
                return byName != 0
                    ? byName
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            result.Add(new CategorySection { Title = title, Rows = bucketRows });
        }

        return result;
    }

    // Group rows attach only when BOTH sides agree there is something to draw: the collector has
    // announced itself as manifest-driven (self-description, not a check on its category key), and
    // the server actually sent a group list this pass. The null return is deliberate — an absent
    // group list and an empty one both look empty to a caller checking `.Count`, but only null
    // tells the window there is nothing here at all, so absence is never papered over with
    // `Array.Empty`.
    private static IReadOnlyList<ItemGroupRow>? BuildGroupRows(
        ICollector collector,
        PluginSettings settings,
        ConfigResponse? remoteConfig,
        bool parentServerEnabled)
    {
        if (!collector.UsesItemManifest || remoteConfig?.ItemManifestGroups is not { } manifestGroups)
            return null;

        var groupRows = new List<ItemGroupRow>();

        // A consent identifier, not prose: long enough for any key a person would write, short
        // enough that one cannot bloat the config it is stored in.
        const int MaxGroupKeyLength = 128;

        foreach (var group in manifestGroups)
        {
            // A blank key is server data gone wrong, and it can never behave: consent reads treat it
            // as off, seen-marking skips it (so it would wear a "New" badge forever and re-trigger a
            // config save every frame), and the consent write would throw. Dropping it here, at the
            // pure boundary, keeps every one of those paths safe — and testable.
            if (string.IsNullOrEmpty(group.Key))
                continue;

            // An over-long key is dropped rather than shortened, and the difference matters: a
            // group key is a consent IDENTITY, not copy. Shortening it would consent on behalf of
            // a group the user was never shown, and two long keys sharing a prefix would collapse
            // into one consent. Dropping is also what keeps it out of the config, where seen-keys
            // are appended and never pruned — so an unbounded key would persist forever, outliving
            // the group that introduced it.
            if (group.Key.Length > MaxGroupKeyLength)
                continue;

            groupRows.Add(new ItemGroupRow
            {
                Key = group.Key,

                // Shortened rather than dropped, because a label carries no identity — consent is
                // written and read under the key beside it — so a clipped label still names the
                // right consent. It is drawn as a checkbox label, and the backend is overridable.
                Label = ServerText.SingleLine(group.Label) ?? group.Key,
                Enabled = settings.IsItemGroupEnabled(group.Key),
                ParentServerEnabled = parentServerEnabled,
                IsNew = !settings.IsItemGroupSeen(group.Key),
            });
        }

        return groupRows;
    }
}
