using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One row of the settings window's category list, drawn without knowing which collection it is.
/// </summary>
public sealed record CategorySettingsRow
{
    /// <summary>The category this row is about. The window uses it only to write the toggle back.</summary>
    public required string Key { get; init; }

    /// <summary>The label to draw beside the checkbox.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The heading this row is listed under, carried verbatim from the collector (see
    /// <see cref="CategoryInfo.Section"/>).
    /// </summary>
    public required string Section { get; init; }

    /// <summary>The plain-language description of what uploading this category sends.</summary>
    public required string WhatGetsSent { get; init; }

    /// <summary>
    /// The hover detail behind <see cref="WhatGetsSent"/>, or null, in which case the window draws no
    /// hover affordance.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>Whether the user has opted this category in.</summary>
    public required bool UserEnabled { get; init; }

    /// <summary>
    /// True when the settings UI has never shown this category to this install, so it is worth
    /// badging as "New".
    /// </summary>
    /// <remarks>
    /// A newly added collection otherwise looks like one ignored since install, and the settings' outer
    /// header folds, so it could go unnoticed. Defaulted, unlike <see cref="ItemGroupRow.IsNew"/>, so a
    /// caller building rows for a surface that does not badge need not answer.
    /// </remarks>
    public bool IsNew { get; init; }

    /// <summary>
    /// Whether this row's collector is manifest-driven (see <see cref="ICollector.UsesItemManifest"/>).
    /// </summary>
    /// <remarks>
    /// Carried so a view can act on it without holding a collector. <see cref="ReadStatusView"/> uses it
    /// to put the container lines in place of this collection's own line, and to drop them when no such
    /// collection is on; <see cref="Groups"/> is populated only when it is set.
    /// </remarks>
    public required bool UsesItemManifest { get; init; }

    /// <summary>
    /// Whether this row's collector uploads each new entry within seconds (see
    /// <see cref="ICollector.UploadsOnUnlock"/>).
    /// </summary>
    /// <remarks>
    /// Lets the sync card decide from the rows alone whether to promise that. Defaulted, unlike
    /// <see cref="UsesItemManifest"/>, because false only withholds a promise.
    /// </remarks>
    public bool UploadsOnUnlock { get; init; }

    /// <summary>
    /// False when the server refuses this category: off for everyone, held back from this account, or
    /// syncing off altogether.
    /// </summary>
    /// <remarks>
    /// The checkbox stays visible but disabled, keeping the user's choice for when it returns.
    /// </remarks>
    public required bool ServerEnabled { get; init; }

    /// <summary>
    /// True when the row is unusable because syncing is paused altogether (see
    /// <see cref="ServerOffCopy.Paused"/>) rather than by a decision about this collection.
    /// </summary>
    /// <remarks>
    /// Every row carries the same value, so the sync card can say it once, and <see cref="ServerOffText"/>
    /// picks its wording by it. Defaulted, unlike <see cref="ServerEnabled"/>, because a pause is not a
    /// fact about any one collection; the default yields the per-category wording, which is harmless
    /// while <see cref="Build"/> is the only producer that draws.
    /// </remarks>
    public bool ServerGloballyOff { get; init; }

    /// <summary>
    /// The server's own explanation for switching this category off, or null when it gave none.
    /// Meaningful only while <see cref="ServerEnabled"/> is false; see <see cref="ServerOffText"/>.
    /// </summary>
    public string? ServerNote { get; init; }

    /// <summary>
    /// What to print under a category the server has switched off, or null when it is on and
    /// nothing needs saying.
    /// </summary>
    /// <remarks>
    /// The server's note when it sent one, otherwise the generic line; see
    /// <see cref="ConfigResponse.CategoryNotes"/> for why only one off-state carries a note. A note that
    /// arrives while the category is on is dropped, since it has nowhere to go under a live checkbox. A
    /// global pause outranks the note and the generic line, since either would send the user looking for
    /// a decision about this collection that nobody made.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    // A method: the generic lines name an address (see ServerOffCopy) that a row does not hold.
    public string? ServerOffText(string host) => ServerEnabled
        ? null
        : ServerGloballyOff
            ? ServerOffCopy.Paused(host)
            : ServerNote ?? ServerOffCopy.Feature(host);

    /// <summary>
    /// Why the last collection pass skipped this category, or null if it did not. The window turns it
    /// into a hint (see <see cref="CollectSkipReasons.Describe"/>).
    /// </summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// The category's latest partial-read phrase (see <see cref="CollectResult.PartialNote"/>), or null
    /// when its last read was whole.
    /// </summary>
    public string? PartialNote { get; init; }

    /// <summary>
    /// The category's latest healthy-chip hover copy (see <see cref="CollectResult.CollectedDetail"/>),
    /// or null when its chip needs none.
    /// </summary>
    public string? CollectedDetail { get; init; }

    /// <summary>True when this category will actually be uploaded as things stand.</summary>
    public bool IsEffectivelyOn => UserEnabled && ServerEnabled;

    /// <summary>
    /// False until <c>/config</c> has been fetched, while <see cref="ServerEnabled"/> is an assumption
    /// rather than the server's answer. See <see cref="WasDrawnAsUsable"/>.
    /// </summary>
    public bool ServerStateKnown { get; init; } = true;

    /// <summary>True when this row should announce itself as new.</summary>
    /// <remarks>
    /// Badging a collection the server switched off would announce something the user cannot use.
    /// While the server's answer is unknown the badge shows, so a user whose config poll is failing
    /// still learns a collection exists.
    /// </remarks>
    public bool IsEffectivelyNew => IsNew && ServerEnabled;

    /// <summary>
    /// True when this row was drawn in a state the user could act on: the server permits it, and that
    /// is the server's answer rather than an assumption.
    /// </summary>
    /// <remarks>
    /// The server half of the retire condition, shared by this row (see
    /// <see cref="ShowingItRetiresTheBadge"/>) and its groups. Retiring an announcement loses it for
    /// good, while showing one early only repeats it, so an unanswered <c>/config</c> is enough to draw
    /// on but not to spend on.
    /// </remarks>
    public bool WasDrawnAsUsable => ServerEnabled && ServerStateKnown;

    /// <summary>True when drawing this row on a badging surface should retire its badge.</summary>
    public bool ShowingItRetiresTheBadge => IsNew && WasDrawnAsUsable;

    /// <summary>
    /// One row per item-manifest consent group, or null when this row has no groups to draw.
    /// </summary>
    /// <remarks>
    /// Null covers both a collector that is not manifest-driven and a server that has sent no groups
    /// yet; the window need not tell them apart.
    /// </remarks>
    public IReadOnlyList<ItemGroupRow>? Groups { get; init; }
}

/// <summary>
/// One checkbox in a manifest-driven category's group list, drawn beneath that category's row.
/// </summary>
/// <remarks>
/// Adds to <see cref="Api.ItemManifestGroup"/> the two facts the server does not send: this user's
/// consent, and whether the settings window has shown the group before.
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
    /// A group is scanned only in its collection's pass, so a collection the server switched off takes
    /// every group with it, whatever each group's own consent says.
    /// </remarks>
    public required bool ParentServerEnabled { get; init; }

    /// <summary>True when this group's items will actually be scanned as things stand.</summary>
    /// <remarks>
    /// What the checkbox draws: a ticked box under an unticked parent would state the opposite of what
    /// happens. The stored consent is untouched and returns on its own.
    /// </remarks>
    public bool IsEffectivelyOn => Enabled && ParentServerEnabled;

    /// <summary>
    /// True when the settings window has never shown this group, so it wears a "New" badge until it is
    /// drawn once.
    /// </summary>
    public required bool IsNew { get; init; }
}

/// <summary>
/// One heading's worth of the consent list, from <see cref="CategorySettingsView.GroupBySection"/>.
/// </summary>
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

    /// <summary>
    /// The server will not accept this collection (see <see cref="CategorySettingsRow.ServerEnabled"/>).
    /// </summary>
    Off,

    /// <summary>This settings window has never shown the collection before.</summary>
    New,
}

/// <summary>
/// Assembles the settings window's category list from the registered collectors.
/// </summary>
/// <remarks>
/// Reads no game state. Every row comes from what its collector says about itself, so a collector for a
/// category this code has never heard of still appears, with no name table or special case here.
/// </remarks>
public static class CategorySettingsView
{
    /// <summary>The mark a row should wear, given what the server says and what the user has seen.</summary>
    /// <remarks>
    /// At most one mark, and "off" outranks "new": inviting the user to look at something they cannot
    /// use is worse than saying nothing, so the announcement waits for a viewing they can act on. The
    /// window supplies the two facts only it knows and draws the chip the answer names.
    /// </remarks>
    /// <param name="row">The row being drawn.</param>
    /// <param name="showNewChips">
    /// Whether this surface announces new collections. The first-run wizard shows every collection
    /// anyway, so it badges nothing; the settings window does.
    /// </param>
    /// <param name="badgedThisSession">
    /// Whether this key was already badged since the window opened, which keeps the chip up for the
    /// rest of the viewing after the row is recorded as seen.
    /// </param>
    public static CategoryBadgeKind BadgeFor(
        CategorySettingsRow row, bool showNewChips, bool badgedThisSession)
    {
        // Re-checked each time: a config poll mid-session can switch a collection off under a badge
        // already on screen.
        if (!row.ServerEnabled)
            return CategoryBadgeKind.Off;

        // Either fact is enough. The row's flag answers for a caller with no session record; the
        // session set keeps the chip up once the batched save makes the next rebuild report false.
        return showNewChips && (row.IsEffectivelyNew || badgedThisSession)
            ? CategoryBadgeKind.New
            : CategoryBadgeKind.None;
    }

    /// <summary>
    /// True when drawing this row on the given surface should record it as seen, retiring its
    /// "New" announcement.
    /// </summary>
    /// <remarks>
    /// The surfaces differ only in whether an unanswered <c>/config</c> counts. A surface that draws no
    /// badges, the first-run wizard, shows every collection before the user can finish, so a failed
    /// config poll must not stop it recording what it showed; a badging surface waits for the answer
    /// (see <see cref="CategorySettingsRow.WasDrawnAsUsable"/>). Neither counts a row the server switched
    /// off, since grayed and unusable is not an introduction.
    /// </remarks>
    /// <param name="row">The row that was just drawn.</param>
    /// <param name="showNewChips">Whether the drawing surface announces new collections.</param>
    public static bool ShowingRetiresTheBadge(CategorySettingsRow row, bool showNewChips) =>
        showNewChips
            ? row.ShowingItRetiresTheBadge
            : row.IsNew && row.ServerEnabled;

    /// <summary>
    /// True when anything in the consent list still counts as "New": a whole collection, or a
    /// manifest group inside one.
    /// </summary>
    /// <remarks>
    /// A row or group counts when this install has never shown it or its badge went up this session,
    /// and only while the server permits the collection. The session half keeps a badge from vanishing
    /// a frame after it appears, since drawing it saves the seen flag. The session sets are parameters
    /// because they belong to a window's lifetime.
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
            // With the header folded, a new collection is as invisible as a new group. The session
            // set is re-checked against ServerEnabled, matching MainWindow.DrawCategoryRow.
            if (row.IsEffectivelyNew || (row.ServerEnabled && badgedCategories.Contains(row.Key)))
                return true;

            // Groups under a switched-off collection raise no chip, or the header would invite the
            // user into a list where nothing is actionable.
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
    /// Skip reasons from the latest collection pass, keyed by category; empty before the first pass.
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

            // Group rows reuse serverEnabled: the scopes guarding stored consent assume row and groups agree.
            // An unfetched config forbids nothing, or an offline plugin would show every category off.
            var serverPaused = remoteConfig is { Enabled: false };
            var serverEnabled = remoteConfig is null
                || (remoteConfig.Enabled && remoteConfig.IsCategoryEnabled(key));

            rows.Add(new CategorySettingsRow
            {
                Key = key,
                DisplayName = collector.DisplayName,
                Section = collector.Section,
                WhatGetsSent = collector.WhatGetsSent,
                Details = collector.Details,
                UserEnabled = settings.IsCategoryEnabled(key),

                // The window marks it seen as it draws, so the next rebuild reports false.
                IsNew = !settings.IsCategorySeen(key),

                UsesItemManifest = collector.UsesItemManifest,

                UploadsOnUnlock = collector.UploadsOnUnlock,

                ServerEnabled = serverEnabled,

                ServerGloballyOff = serverPaused,

                // CategoryNote bounds and folds the server's text.
                ServerNote = remoteConfig?.CategoryNote(key),

                ServerStateKnown = remoteConfig is not null,

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
    /// Sections keep the order of their first row, so <see cref="CollectorRegistry"/> decides which
    /// heading comes first. Within a section, rows sort by display name so a reader finds a collection
    /// where the alphabet puts it. There is no fixed section list and no fallback bucket.
    /// </remarks>
    /// <param name="rows">The rows to group, from <see cref="Build"/>.</param>
    public static IReadOnlyList<CategorySection> GroupBySection(
        IReadOnlyList<CategorySettingsRow> rows)
    {
        // The list keeps first-appearance order, which a Dictionary does not promise.
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
            // Ordinal, not culture-aware: the labels are English strings authored here, and every
            // locale should see one order. The key breaks a tie between equal display names.
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

    // Rows attach only when the collector is manifest-driven and the server sent groups this pass.
    // Null rather than an empty list tells the window there is nothing to draw at all.
    private static IReadOnlyList<ItemGroupRow>? BuildGroupRows(
        ICollector collector,
        PluginSettings settings,
        ConfigResponse? remoteConfig,
        bool parentServerEnabled)
    {
        if (!collector.UsesItemManifest || remoteConfig?.ItemManifestGroups is not { } manifestGroups)
            return null;

        var groupRows = new List<ItemGroupRow>();

        // Long enough for any key a person would write, short enough not to bloat the config.
        const int MaxGroupKeyLength = 128;

        // The list is rebuilt every frame the window is open. An honest manifest has a handful of
        // groups, so this only stops a hostile or broken config from becoming a frame-time sink.
        const int MaxGroupRows = 100;

        foreach (var group in manifestGroups)
        {
            if (groupRows.Count >= MaxGroupRows)
                break;

            // No path handles a blank key: consent reads it as off, seen-marking skips it (a "New"
            // badge and a config save every frame), and the consent write throws.
            if (string.IsNullOrEmpty(group.Key))
                continue;

            // Dropped, not shortened: a shortened key would consent for a group the user never saw, and
            // two keys sharing a prefix would merge. Seen-keys are never pruned, so it would also persist.
            if (group.Key.Length > MaxGroupKeyLength)
                continue;

            // Consent uses the raw key, so a clipped label still names the right group. A key that
            // folds to nothing would leave a consent checkbox nobody can read, so it is dropped.
            var label = ServerText.SingleLine(group.Label) ?? ServerText.SingleLine(group.Key);
            if (label is null)
                continue;

            groupRows.Add(new ItemGroupRow
            {
                Key = group.Key,
                Label = label,
                Enabled = settings.IsItemGroupEnabled(group.Key),
                ParentServerEnabled = parentServerEnabled,
                IsNew = !settings.IsItemGroupSeen(group.Key),
            });
        }

        return groupRows;
    }
}
