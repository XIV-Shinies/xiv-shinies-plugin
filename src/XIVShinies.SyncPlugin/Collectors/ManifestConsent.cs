using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Answers consent and server-permission questions about the category rows without naming a category.
/// </summary>
/// <remarks>
/// Its callers, the sync orchestrator among them, live outside the collectors, and the extensibility
/// contract forbids them from naming a category. Reads no game state; everything it uses is handed in.
/// </remarks>
public static class ManifestConsent
{
    /// <summary>
    /// True when the user opted into a collection driven by the item manifest: the consent the one-time
    /// group migration carries over to the server's <c>legacy</c> groups.
    /// </summary>
    /// <remarks>
    /// Asks the collectors (<see cref="ICollector.UsesItemManifest"/>) rather than naming the category,
    /// so a second manifest-driven collection is migrated too rather than silently skipped.
    /// </remarks>
    /// <param name="collectors">Every registered collector.</param>
    /// <param name="settings">The consent to read.</param>
    public static bool AnyManifestCategoryEnabled(
        IEnumerable<ICollector> collectors, PluginSettings settings)
    {
        foreach (var collector in collectors)
        {
            if (collector.UsesItemManifest && settings.IsCategoryEnabled(collector.CategoryKey))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The categories whose whole scope is settled by ticking them: every collection except one whose
    /// contents come from consent groups the user answers separately.
    /// </summary>
    /// <remarks>
    /// Anything that enables a category for the user must use this list: one switched on without its
    /// groups stays ticked, collects nothing and reports itself skipped (see
    /// <see cref="SetGroupConsent"/>). Only the item manifest attaches group rows, so a collection
    /// driven by another manifest has no groups to strand and stays in the list.
    /// </remarks>
    /// <param name="collectors">Every registered collector.</param>
    public static IReadOnlyList<string> FixedScopeCategoryKeys(IEnumerable<ICollector> collectors)
    {
        var keys = new List<string>();
        foreach (var collector in collectors)
        {
            if (!collector.UsesItemManifest)
                keys.Add(collector.CategoryKey);
        }

        return keys;
    }

    /// <summary>
    /// Writes one category's consent, and every manifest group nested under it, together.
    /// </summary>
    /// <remarks>
    /// A manifest category sends only what its enabled groups cover, so with every group off it would
    /// upload nothing while showing as on; ticking a category ticks its groups, and unticking unticks
    /// them. Only the groups on the row are written, so a group the server adds later arrives off and
    /// waits for its own tick.
    /// </remarks>
    /// <param name="row">The category row whose consent is being written.</param>
    /// <param name="enabled">The value to write to the category and to each of its groups.</param>
    /// <param name="settings">The consent to write.</param>
    public static void SetRowConsent(CategorySettingsRow row, bool enabled, PluginSettings settings)
    {
        settings.SetCategoryEnabled(row.Key, enabled);

        if (row.Groups is not { Count: > 0 } groups)
            return;

        foreach (var group in groups)
            settings.SetItemGroupEnabled(group.Key, enabled);
    }

    /// <summary>
    /// Writes one group's consent, keeping its category in agreement with it.
    /// </summary>
    /// <remarks>
    /// The group-side half of <see cref="SetRowConsent"/>. Switching a group on switches its category
    /// on, since a group is scanned only in its category's pass. Switching the last enabled group off
    /// switches the category off: with nothing to scan it would collect nothing while showing as on, a
    /// state a consent surface must never reach.
    /// </remarks>
    /// <param name="row">The category row the group belongs to.</param>
    /// <param name="groupKey">The group the user just clicked.</param>
    /// <param name="enabled">What they set it to.</param>
    /// <param name="settings">The consent to write.</param>
    public static void SetGroupConsent(
        CategorySettingsRow row, string groupKey, bool enabled, PluginSettings settings)
    {
        settings.SetItemGroupEnabled(groupKey, enabled);

        if (enabled)
        {
            settings.SetCategoryEnabled(row.Key, true);
            return;
        }

        if (row.Groups is { Count: > 0 } groups && !AnyGroupEnabled(groups, settings))
            settings.SetCategoryEnabled(row.Key, false);
    }

    /// <summary>
    /// Whether every consent control these rows cover is on: the state of a select-all box over them.
    /// </summary>
    /// <remarks>
    /// False when no server-enabled row was seen; otherwise a box over nothing (no collectors, or every
    /// category switched off by the server) would draw ticked while nothing is on. Rows the server
    /// switched off are skipped: they are not the user's to answer for, and their controls draw grayed.
    /// </remarks>
    /// <param name="rows">The category rows on screen, group state included.</param>
    public static bool AllConsentGiven(IReadOnlyList<CategorySettingsRow> rows)
    {
        var sawServerEnabledRow = false;
        var allEnabled = true;

        foreach (var row in rows)
        {
            if (!row.ServerEnabled)
                continue;

            sawServerEnabledRow = true;

            allEnabled &= row.UserEnabled;

            if (row.Groups is not { Count: > 0 } groups)
                continue;

            foreach (var group in groups)
                allEnabled &= group.Enabled;
        }

        return allEnabled && sawServerEnabledRow;
    }

    /// <summary>
    /// Whether any collection on screen is one the server permits, so the select-all has something to
    /// act on.
    /// </summary>
    /// <remarks>
    /// With none permitted the select-all would visibly do nothing, so the window disables it, as it
    /// does each row.
    /// </remarks>
    public static bool AnyServerEnabled(IReadOnlyList<CategorySettingsRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.ServerEnabled)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the server has paused syncing entirely, rather than switching off any particular
    /// collection.
    /// </summary>
    /// <remarks>
    /// Read from the rows so the window keeps one source for what the server permits. The pause is one
    /// flag on every row, so the first row that reports it settles the question. An empty list answers
    /// "not paused", the permissive direction, which cannot happen while the registry yields collectors.
    /// </remarks>
    /// <param name="rows">This frame's category rows.</param>
    public static bool ServerHasPausedEverything(IReadOnlyList<CategorySettingsRow> rows)
    {
        foreach (var row in rows)
        {
            // Both halves, so a row that claims a pause while still usable cannot make the window
            // announce an outage over live checkboxes.
            if (row.ServerGloballyOff && !row.ServerEnabled)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the server has switched off every collection one by one, without pausing syncing.
    /// </summary>
    /// <remarks>
    /// Every box is then grayed for a reason no pause sentence explains, so a consent surface owes the
    /// user its own sentence. An empty list is nobody's refusal.
    /// </remarks>
    /// <param name="rows">This frame's category rows.</param>
    public static bool ServerPermitsNoCollection(IReadOnlyList<CategorySettingsRow> rows) =>
        rows.Count > 0 && !AnyServerEnabled(rows) && !ServerHasPausedEverything(rows);

    /// <summary>
    /// Whether any collection will be uploaded as things stand, by both the user's choice and the
    /// server's.
    /// </summary>
    /// <remarks>
    /// The sync card's "Everything else syncs automatically every &lt;interval&gt;" depends on it: with
    /// no collection running that claim is false, however healthy the pipeline. Which way the user got
    /// here, and what the card says about it, is <c>SyncStatusView.Select</c>'s decision.
    /// </remarks>
    /// <param name="rows">This frame's category rows.</param>
    public static bool AnyEffectivelyOn(IReadOnlyList<CategorySettingsRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsEffectivelyOn)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the server offers consent groups for this collection and the user has none of them
    /// switched on, so the pass may look for nothing.
    /// </summary>
    /// <remarks>
    /// The item collector acts on it. Distinct from an empty manifest, where the server asked about
    /// nothing: here it asked, and the user has not agreed to answer.
    /// </remarks>
    /// <param name="context">The collect context for this pass.</param>
    public static bool GroupsOfferedButNoneEnabled(CollectContext context) =>
        context.RemoteConfig?.ItemManifestGroups is { Count: > 0 }
        && context.EnabledGroups.Count == 0;

    private static bool AnyGroupEnabled(
        IReadOnlyList<ItemGroupRow> groups, PluginSettings settings)
    {
        foreach (var group in groups)
        {
            if (settings.IsItemGroupEnabled(group.Key))
                return true;
        }

        return false;
    }
}
