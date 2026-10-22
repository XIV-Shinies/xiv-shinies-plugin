namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One source of collection facts: completed quests, unlocked mounts, item possession, and so on.
/// </summary>
/// <remarks>
/// Adding a collection is one new class implementing this interface and nothing else: the runner,
/// payload and settings UI iterate collectors, and none may branch on a category name. The one
/// exception is a server-manifest collection, which also brings its <c>/config</c> field, its id
/// derivation on <see cref="CollectContext"/> (never on the collector; see CLAUDE.md's extensibility
/// contract) and the <see cref="ManifestCatalog"/> entry, the only place a category is named. Keep
/// Dalamud types out of this interface, which must load outside the game.
/// </remarks>
public interface ICollector
{
    /// <summary>
    /// The payload key for this collector's facts, for example <c>"quests"</c>. It is also the
    /// user's opt-in key and the server's kill-switch key, so all three line up without a lookup.
    /// </summary>
    string CategoryKey { get; }

    /// <summary>
    /// The category's name as a person reads it, for example <c>"Mounts"</c>.
    /// </summary>
    /// <remarks>
    /// Lives on the collector so the settings window holds no key-to-label table, which would be a
    /// category-name branch by another name.
    /// </remarks>
    string DisplayName { get; }

    /// <summary>
    /// The heading this collection is listed under on the consent surfaces (see
    /// <see cref="CategoryInfo.Section"/> for the grouping rules).
    /// </summary>
    string Section { get; }

    /// <summary>
    /// A plain-language sentence naming exactly what leaves the player's machine for this category,
    /// shown next to the opt-in toggle.
    /// </summary>
    /// <remarks>
    /// Dalamud requires users be told what is collected before they consent, so this must describe
    /// the real payload and change whenever <see cref="Collect"/> sends something new. Write it for
    /// someone who has never read this code, and name the website as
    /// <see cref="HostPlaceholder.Token"/>; the surface that draws the line fills in its address.
    /// </remarks>
    string WhatGetsSent { get; }

    /// <summary>
    /// The elaboration behind <see cref="WhatGetsSent"/>, shown on hover, or null when the
    /// one-liner says everything.
    /// </summary>
    /// <remarks>
    /// <see cref="WhatGetsSent"/> must still name every kind of data that leaves the machine; this
    /// carries scope and reassurance, never a disclosure the visible line omits. Name the website as
    /// <see cref="HostPlaceholder.Token"/>, as <see cref="WhatGetsSent"/> does.
    /// </remarks>
    string? Details { get; }

    /// <summary>
    /// True when this collection's scope is driven by the server's item manifest, rather than being
    /// fixed at compile time.
    /// </summary>
    /// <remarks>
    /// The settings window reads this to attach per-group consent rows, instead of comparing
    /// <see cref="CategoryKey"/> against a name.
    /// </remarks>
    bool UsesItemManifest { get; }

    /// <summary>
    /// True when the game announces each new entry of this collection the moment it is earned, so it
    /// uploads within seconds (see <see cref="CategoryInfo.UploadsOnUnlock"/>).
    /// </summary>
    /// <remarks>
    /// Carried onto each <see cref="CategorySettingsRow"/>, where the sync card reads it rather than
    /// naming collections.
    /// </remarks>
    bool UploadsOnUnlock => false;

    /// <summary>
    /// Reads the facts from the game, or explains why it could not.
    /// </summary>
    /// <param name="context">
    /// Values that vary between passes, such as the server's latest config and item manifest.
    /// </param>
    /// <remarks>
    /// Call on the framework thread. Return <see cref="CollectResult.Skipped"/> rather than throwing
    /// when a source is not available yet, such as an achievements list never opened; the server
    /// reads the omitted category as "not read this time".
    /// </remarks>
    CollectResult Collect(CollectContext context);
}
