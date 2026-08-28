using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// Every user-facing setting, and the rules that govern them.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of Dalamud types. <see cref="Configuration"/> implements Dalamud's
/// <c>IPluginConfiguration</c>, which means merely constructing it requires loading
/// <c>Dalamud.dll</c> — impossible outside the running game. Keeping the settings and their logic
/// here lets the xUnit suite exercise them directly, while <see cref="Configuration"/> stays a
/// thin persistence shell.
/// </para>
/// <para>
/// Every default is "off". A fresh install must upload nothing until the user has been shown what
/// gets sent and has explicitly opted in — a Dalamud compliance rule, not a preference.
/// </para>
/// </remarks>
[Serializable]
public class PluginSettings
{
    /// <summary>
    /// The XIV Shinies API token, pasted by the user. Stored as plain text in Dalamud's plugin
    /// config, which is standard for the ecosystem: it is a plugin-scoped, revocable credential
    /// that cannot act on the account itself.
    /// </summary>
    /// <remarks>Never write this value to the log.</remarks>
    public string Token { get; set; } = string.Empty;

    /// <summary>The master switch. While false, the plugin uploads nothing at all.</summary>
    public bool MasterEnabled { get; set; }

    /// <summary>True once the user has completed the first-run wizard and chosen categories.</summary>
    public bool OnboardingComplete { get; set; }

    /// <summary>
    /// True while the user shares live Occult Crescent instance state (CE/FATE/tower status)
    /// with the tracker. Defaults ON, unlike every category consent, because it describes
    /// <b>world</b> state rather than the player (the Universalis precedent) — and it still
    /// only ever acts behind <see cref="MasterEnabled"/> and <see cref="OnboardingComplete"/>,
    /// so a fresh install shares nothing until the user finishes the wizard, where this box is
    /// visible and ticked before anything can send. A config from an install that finished
    /// onboarding before this setting existed starts it OFF instead (see
    /// <see cref="ApplyUpgradeMigrations"/>).
    /// </summary>
    public bool ShareOccultInstanceState { get; set; } = true;

    /// <summary>
    /// True when the user chose to have sharing features added by later updates start switched
    /// on, rather than being asked each time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by <see cref="AutoEnableUnseenCategories"/>, which switches on every collection this
    /// install has never been shown — the whole point being that a user who always opts in does
    /// not have to keep ticking boxes.
    /// </para>
    /// <para>
    /// Defaults OFF, unlike <see cref="ShareOccultInstanceState"/>. That toggle defaults on for a
    /// defence that does not carry here: it is one named thing, disclosed in full on the same
    /// screen, and what it shares is world state. This is open-ended — an answer about collections
    /// that do not exist yet, whose disclosure the user cannot have read — and it governs uploads
    /// of their own collection data. A ticked-by-default box would collect that answer from
    /// everyone who did not think to untick it, which is the weakest form consent takes.
    /// </para>
    /// <para>
    /// A collection nobody switched on still announces itself with a "New" badge, so this setting
    /// is convenience rather than the way anything gets discovered.
    /// </para>
    /// </remarks>
    public bool AutoEnableNewFeatures { get; set; }

    /// <summary>
    /// Brings a config written by an older plugin version up to the current schema. Called once
    /// at load when the persisted <see cref="Configuration.Version"/> is behind; returns true
    /// when anything changed (the caller then saves).
    /// </summary>
    /// <remarks>
    /// The version-1 rule: <see cref="ShareOccultInstanceState"/> did not exist before version 1
    /// and defaults ON, so on an install whose onboarding is already complete — a user the wizard
    /// will never show that toggle to — it starts OFF, and the settings screen is where they opt
    /// in. An install still ahead of its wizard keeps the default: the wizard will put the box in
    /// front of them.
    /// </remarks>
    /// <param name="fromVersion">The version the loaded config was written at.</param>
    public bool ApplyUpgradeMigrations(int fromVersion)
    {
        var changed = false;

        if (fromVersion < 1 && OnboardingComplete)
        {
            ShareOccultInstanceState = false;
            changed = true;
        }

        // The version-2 rule: a config written before this version predates the seen-set, so its
        // owner went through a wizard that recorded nothing. The collections of that era are taken
        // as already-seen without evidence, because over-claiming "New" on a familiar list is the
        // worse error. The list is frozen, so anything added since still announces itself, and an
        // install still ahead of its wizard is left alone, because the wizard marks what it shows.
        if (fromVersion < 2 && OnboardingComplete)
        {
            // Held across both writes so the flag and the baseline land as one step, the same
            // nested-lock shape as InitializeSeenCategories.
            lock (gate)
            {
                MarkCategoriesSeen(CategoriesPresentBeforeSeenTracking);
                SeenCategoriesInitialized = true;
            }

            changed = true;
        }

        // The version-3 rule: AutoEnableNewFeatures decides whether a newly added COLLECTION starts
        // switched on (see AutoEnableUnseenCategories), and a config written before this version
        // stored its answer against copy that named only "a new kind of sharing" — a narrower
        // question than the one now asked of it. The build that wrote those configs defaulted this
        // on, so nearly all of them carry a true nobody ticked. It starts off, and the settings
        // screen, where the copy names collections, is where that user opts back in.
        //
        // Not gated on OnboardingComplete, unlike the rule above: a config saved part-way through
        // the wizard carries that same untouched true, and leaving it would hand that user a
        // second pre-ticked box the wizard's own copy does not account for. A genuinely fresh
        // install is untouched either way, because it never runs migrations at all.
        if (fromVersion < 3 && AutoEnableNewFeatures)
        {
            AutoEnableNewFeatures = false;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The collections that existed in the last version to ship without seen-tracking — the
    /// baseline the version-2 migration writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This list is a historical fact and must never grow.</b> It records what a config written
    /// before the seen-set had already had the chance to show its owner, so that a collection
    /// added afterwards is correctly new to them. Appending a later collection would silence
    /// exactly the badge it should raise — which is what
    /// <c>PluginSettingsTests.The_pre_seen_tracking_baseline_never_grows</c> exists to prevent.
    /// </para>
    /// <para>
    /// Naming keys here is not a category-name branch: nothing branches on any of these names —
    /// the list is handed to <see cref="MarkCategoriesSeen"/> whole — and adding a collector
    /// requires no change to it. It is a frozen snapshot consulted once per install, in the same
    /// spirit as a database migration naming the columns of its own era.
    /// </para>
    /// </remarks>
    // IReadOnlyList rather than an array: `static readonly` freezes only the reference, so a
    // public array's elements stay reassignable and the "never changes" promise above would be
    // one the type does not actually make.
    public static readonly IReadOnlyList<string> CategoriesPresentBeforeSeenTracking = new[]
    {
        CategoryKeys.Achievements,
        CategoryKeys.Items,
        CategoryKeys.Minions,
        CategoryKeys.Mounts,
        CategoryKeys.OccultProgression,
        CategoryKeys.OccultRecords,
        CategoryKeys.QuestSequences,
        CategoryKeys.Quests,
        CategoryKeys.TripleTriadCards,
        CategoryKeys.TripleTriadNpcs,
    };

    /// <summary>
    /// The backend server. User-overridable per Dalamud's recommendation; validate any change with
    /// <see cref="BackendUrl.TryNormalize"/> before storing it.
    /// </summary>
    public string BaseUrl { get; set; } = BackendUrl.Default;

    /// <summary>
    /// True once the user has acknowledged that pointing the plugin at a non-default server sends
    /// their API token to that server. Reset this whenever <see cref="BaseUrl"/> changes.
    /// </summary>
    public bool CustomBackendAcknowledged { get; set; }

    /// <summary>
    /// Guards every read and write of the collections below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two threads touch them. Dalamud draws the settings window on its render thread, where a
    /// checkbox click writes consent; the game's framework thread reads the same values on every
    /// collection pass (<see cref="Collectors.CollectorRunner"/> copies the enabled group keys into
    /// each pass's context). <c>List&lt;T&gt;</c> and <c>Dictionary&lt;K,V&gt;</c> tolerate neither:
    /// an <c>Add</c> on one thread while the other is enumerating throws, and the collection pass is
    /// lost.
    /// </para>
    /// <para>
    /// <c>lock (gate) { … }</c> is C#'s mutual exclusion block — only one thread may be inside a lock
    /// on a given object at a time, and the others wait their turn. JavaScript has no equivalent
    /// because a single event loop makes one impossible to need. The object itself is arbitrary and
    /// private: it exists only to be the thing threads queue on, and being private means no outside
    /// code can lock on it and deadlock us.
    /// </para>
    /// </remarks>
    // Private fields are not serialized into the config file, so this never reaches disk.
    private readonly object gate = new();

    /// <summary>
    /// Which collection categories the user opted into, keyed by the collector's category key
    /// (for example <c>"quests"</c>).
    /// </summary>
    /// <remarks>
    /// A dictionary rather than one named property per category, on purpose: adding a new
    /// collection must be one new collector class and nothing else. Naming the categories here
    /// would force a settings change (and a migration) for every new collection.
    /// </remarks>
    // A Dictionary maps keys to values — like a JS object used as a lookup, or a `Map`.
    public Dictionary<string, bool> EnabledCategories { get; set; } = new();

    /// <summary>
    /// True when the user has opted into uploading the given category. An unknown key reads as
    /// false, so a collector added in a later version starts opted-out rather than silently on.
    /// </summary>
    // `TryGetValue` is the allocation-free "look it up, tell me if it was there" pattern: it
    // returns a bool and hands the value back through the `out` parameter. The blank-key guard
    // matters because a Dictionary throws on a null key rather than simply missing.
    public bool IsCategoryEnabled(string categoryKey)
    {
        if (string.IsNullOrEmpty(categoryKey))
            return false;

        lock (gate)
            return EnabledCategories.TryGetValue(categoryKey, out var enabled) && enabled;
    }

    /// <summary>Opts the given category in or out.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public void SetCategoryEnabled(string categoryKey, bool enabled)
    {
        // Fail loudly on a blank key rather than writing an unreachable entry. Reading tolerates a
        // blank key (returns false); writing one is always a caller bug.
        ArgumentException.ThrowIfNullOrEmpty(categoryKey);

        lock (gate)
            EnabledCategories[categoryKey] = enabled;
    }

    /// <summary>
    /// Which item manifest groups the user opted into, by the server's group key. An opt-in
    /// ALLOWLIST: an unknown key reads as disabled, so a group added server-side starts OFF
    /// until the user ticks it.
    /// </summary>
    public List<string> EnabledItemGroupKeys { get; set; } = new();

    /// <summary>Group keys the settings UI has already shown once — everything else gets a "New" badge.</summary>
    /// <remarks>
    /// A list for the config file, mirrored by a set for the reads: the settings window asks
    /// <see cref="IsItemGroupSeen"/> once per group per frame, and the keys come from a server
    /// whose manifest can grow — so the per-frame cost must not scale with how many groups this
    /// install has ever been shown. The mirror is discarded when the list is replaced (which is how
    /// deserialization hands it in), rebuilt on the next read, and kept in step by
    /// <see cref="MarkItemGroupsSeen"/>.
    /// </remarks>
    public List<string> SeenItemGroupKeys
    {
        get => seenItemGroupKeys;
        set
        {
            seenItemGroupKeys = value ?? new List<string>();
            seenItemGroupLookup = null;
        }
    }

    private List<string> seenItemGroupKeys = new();

    // Null until first asked, so a fresh or freshly-loaded settings object costs nothing extra.
    private HashSet<string>? seenItemGroupLookup;

    /// <summary>Category keys the settings UI has already shown once — everything else gets a "New" badge.</summary>
    public List<string> SeenCategoryKeys { get; set; } = new();

    /// <summary>True once <see cref="InitializeSeenCategories"/> has established this install's baseline.</summary>
    public bool SeenCategoriesInitialized { get; set; }

    /// <summary>True once the one-time pre-group consent migration has run.</summary>
    public bool ItemGroupConsentMigrated { get; set; }

    /// <summary>
    /// True when the user has opted into the given item group. An unknown key reads as false, so
    /// a group added in a later version starts opted-out rather than silently on.
    /// </summary>
    // `Contains` on a List is O(n), but the list stays tiny (a handful of group keys), so a plain
    // List wins on simplicity. If groups ever number in the hundreds, switch to a HashSet.
    public bool IsItemGroupEnabled(string groupKey)
    {
        if (string.IsNullOrEmpty(groupKey))
            return false;

        lock (gate)
            return EnabledItemGroupKeys.Contains(groupKey);
    }

    /// <summary>
    /// Runs an action with the settings held still, for a caller that needs to read all of them at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written for exactly one caller — <see cref="Configuration.Save"/>, which serializes this object
    /// and so walks every collection in it. An `Action` is C#'s type for "a function taking nothing and
    /// returning nothing", the closest thing to passing a callback in JavaScript. Whatever it does runs
    /// with the lock held, so keep it to the read it was needed for.
    /// </para>
    /// <para>
    /// The trade-off is deliberate: that one caller writes the config file to disk, so a settings read on
    /// the framework thread can be made to wait on a slow write. It is a handful of milliseconds on a bad
    /// day, against a torn read of a collection being walked while another thread adds to it — which
    /// throws, and takes the whole collection pass down with it.
    /// </para>
    /// </remarks>
    public void RunLocked(Action action)
    {
        lock (gate)
            action();
    }

    /// <summary>
    /// A point-in-time copy of the enabled group keys, for a caller that needs to read them all.
    /// </summary>
    /// <remarks>
    /// A copy, not the live list. The collection pass consults these keys for every item it checks, and
    /// the user can tick a checkbox on another thread while it does — enumerating the live list would
    /// throw the moment those two met. Copying under the lock hands the pass a set that cannot change
    /// underneath it, at the cost of one small allocation per pass.
    /// </remarks>
    public HashSet<string> SnapshotEnabledItemGroupKeys()
    {
        lock (gate)
            return new HashSet<string>(EnabledItemGroupKeys);
    }

    /// <summary>Opts the given item group in or out.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public void SetItemGroupEnabled(string groupKey, bool enabled)
    {
        // Fail loudly on a blank key rather than writing an unreachable entry. Reading tolerates a
        // blank key (returns false); writing one is always a caller bug.
        ArgumentException.ThrowIfNullOrEmpty(groupKey);

        lock (gate)
        {
            if (enabled)
            {
                // Add to the list only if it is not already there (idempotent, no duplicates).
                if (!EnabledItemGroupKeys.Contains(groupKey))
                {
                    EnabledItemGroupKeys.Add(groupKey);
                }
            }
            else
            {
                // Remove from the list if present. Remove(item) does nothing if the item is not in the list.
                EnabledItemGroupKeys.Remove(groupKey);
            }
        }
    }

    /// <summary>True when the settings UI has already shown the given category once.</summary>
    public bool IsCategorySeen(string categoryKey)
    {
        if (string.IsNullOrEmpty(categoryKey))
            return false;

        lock (gate)
            return SeenCategoryKeys.Contains(categoryKey);
    }

    /// <summary>Mark the given categories as seen in the settings UI.</summary>
    /// <remarks>
    /// Idempotent, and best-effort about blanks, for the same reasons
    /// <see cref="MarkItemGroupsSeen"/> is: it is called from the draw loop, where one malformed
    /// entry must not take the frame down.
    /// </remarks>
    public void MarkCategoriesSeen(IEnumerable<string> categoryKeys)
    {
        if (categoryKeys == null)
        {
            return;
        }

        lock (gate)
        {
            foreach (var categoryKey in categoryKeys)
            {
                if (!string.IsNullOrEmpty(categoryKey) && !SeenCategoryKeys.Contains(categoryKey))
                {
                    SeenCategoryKeys.Add(categoryKey);
                }
            }
        }
    }

    /// <summary>
    /// Establishes, once per install, which categories count as already-seen — the baseline every
    /// later "New" badge is measured against. Does nothing after the first call.
    /// </summary>
    /// <param name="categoryKeys">Every category registered in this build.</param>
    /// <returns>True when this call established the baseline, so the caller knows to save.</returns>
    /// <remarks>
    /// <para>
    /// The baseline turns on whether the user has been through onboarding. Someone still in the
    /// wizard is shown every category as part of it, and the wizard marks each one seen as it
    /// draws — so nothing is pre-marked here and their first genuinely new category is the first
    /// thing to ever wear a badge.
    /// </para>
    /// <para>
    /// An install that reaches this call with onboarding complete has no recorded baseline and no
    /// way to recover which categories its wizard showed, so every category present at this call
    /// is taken as already-seen — silencing a screenful of badges on collections the user has been
    /// using all along, at the cost of a category shipping alongside the baseline going unbadged.
    /// A config written before the seen-set existed does not arrive in that state:
    /// <see cref="ApplyUpgradeMigrations"/> writes its baseline from the frozen pre-tracking list
    /// first, so a collection added after that list still badges for those installs.
    /// </para>
    /// </remarks>
    public bool InitializeSeenCategories(IEnumerable<string> categoryKeys)
    {
        lock (gate)
        {
            if (SeenCategoriesInitialized)
                return false;

            SeenCategoriesInitialized = true;

            // Nested inside the lock this method already holds, which C# permits: `lock` is
            // reentrant for the thread that owns it, so the inner call re-enters rather than
            // deadlocking. Holding it across both makes the flag and the baseline one atomic step.
            if (OnboardingComplete)
                MarkCategoriesSeen(categoryKeys);
        }

        return true;
    }

    /// <summary>
    /// Switches on every collection this install has never been shown, for a user who asked not to
    /// be asked again.
    /// </summary>
    /// <param name="categoryKeys">Every category registered in this build.</param>
    /// <returns>The keys switched on, in the order given — empty when nothing changed.</returns>
    /// <remarks>
    /// <para>
    /// This is the one place a collection is switched on without the user ticking its own box.
    /// What licenses it: <see cref="AutoEnableNewFeatures"/> is an explicit, visible, disclosed
    /// standing answer to exactly this question, ticked on a consent surface that says collections
    /// added later start switched on. A user who did not tick it gets nothing here, and neither
    /// does one still ahead of their wizard — for them the wizard is the consent surface, and it
    /// puts every collection in front of them.
    /// </para>
    /// <para>
    /// Keyed on the seen-set rather than on a list of what is new, so it needs no per-release
    /// bookkeeping: a collection the install has been shown is one the user has had the chance to
    /// judge, and their answer — including switching it back off — is left alone forever after.
    /// The badge is deliberately not suppressed: a collection that turned itself on is one the
    /// user has MORE reason to be told about, not less.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AutoEnableUnseenCategories(IEnumerable<string> categoryKeys)
    {
        if (categoryKeys == null)
            return Array.Empty<string>();

        List<string>? enabled = null;

        lock (gate)
        {
            if (!OnboardingComplete || !AutoEnableNewFeatures)
                return Array.Empty<string>();

            // No recorded baseline means nothing can be known to be new. An onboarded install
            // always has one — the wizard marks every collection it shows — so reaching here with
            // an empty set means the record was lost rather than never written, and treating every
            // collection as never-shown would switch on ones the user declined.
            if (SeenCategoryKeys.Count == 0)
                return Array.Empty<string>();

            foreach (var categoryKey in categoryKeys)
            {
                if (string.IsNullOrEmpty(categoryKey) || SeenCategoryKeys.Contains(categoryKey))
                    continue;

                // A present entry is a value someone already decided — a click, or an earlier run
                // of this method — and re-deciding it here would overwrite a standing state. In
                // particular an answer of "no" is left alone. The guard above asks whether the
                // collection has been announced; this one asks whether it already has a value,
                // and the two are recorded separately.
                if (EnabledCategories.ContainsKey(categoryKey))
                    continue;

                // The accessor takes the same lock this method already holds; C#'s lock is
                // reentrant for the owning thread, so this re-enters rather than deadlocking.
                SetCategoryEnabled(categoryKey, true);
                (enabled ??= new List<string>()).Add(categoryKey);
            }
        }

        return enabled ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>True when the settings UI has already shown the given item group once.</summary>
    public bool IsItemGroupSeen(string groupKey)
    {
        if (string.IsNullOrEmpty(groupKey))
            return false;

        lock (gate)
        {
            seenItemGroupLookup ??= new HashSet<string>(seenItemGroupKeys, StringComparer.Ordinal);
            return seenItemGroupLookup.Contains(groupKey);
        }
    }

    /// <summary>Mark the given item groups as seen in the settings UI.</summary>
    /// <remarks>
    /// <para>
    /// Idempotent: calling this multiple times with the same group keys does not duplicate list
    /// entries.
    /// </para>
    /// <para>
    /// Deliberately best-effort, unlike <see cref="SetItemGroupEnabled"/>'s throw-on-blank
    /// convention: a null sequence is a no-op, and a blank key is skipped while the rest of the
    /// batch is still marked. The keys here come from server-supplied group data during UI
    /// rendering, so a malformed group must degrade gracefully rather than crash the draw loop.
    /// <see cref="SetItemGroupEnabled"/>, by contrast, receives the key of a known row the user
    /// clicked — a blank key there is always a caller bug worth failing loudly on.
    /// </para>
    /// </remarks>
    public void MarkItemGroupsSeen(IEnumerable<string> groupKeys)
    {
        if (groupKeys == null)
        {
            return;
        }

        lock (gate)
        {
            seenItemGroupLookup ??= new HashSet<string>(seenItemGroupKeys, StringComparer.Ordinal);

            foreach (var groupKey in groupKeys)
            {
                // The set answers "already there?" and the list keeps what the config file
                // stores; both are written together so they cannot disagree.
                if (!string.IsNullOrEmpty(groupKey) && seenItemGroupLookup.Add(groupKey))
                {
                    seenItemGroupKeys.Add(groupKey);
                }
            }
        }
    }

    /// <summary>
    /// One-time migration: a user whose Items toggle was on had already consented to the
    /// scope the server now marks <c>legacy: true</c> — enable exactly those groups. Every
    /// other group starts OFF regardless (explicit opt-in is a Dalamud rule, not a preference).
    /// Legacy groups are also marked seen: they are not new to this user. Returns true when
    /// the migration ran (caller persists the config), false when it had already run.
    /// </summary>
    /// <param name="groups">
    /// The item manifest groups from the server. Must be non-null — pass an empty list when the
    /// config carries no groups.
    /// </param>
    /// <param name="itemsCategoryEnabled">
    /// Whether the user had the Items category enabled. Passed in rather than looked up so this
    /// stays free of category-name knowledge — the caller owns which category the manifest
    /// belongs to.
    /// </param>
    /// <returns>When true, the caller should persist the updated config.</returns>
    public bool MigrateItemGroupConsent(
        IReadOnlyList<ItemManifestGroup> groups, bool itemsCategoryEnabled)
    {
        // Held for the whole method, so the run-once flag and every write it makes land as one: a
        // checkbox click on the UI thread cannot slip between them and be lost. (What the caller passed
        // for itemsCategoryEnabled was read before this lock was taken, so a click can still change that
        // answer underneath us — harmlessly, since a category the user has just switched off uploads
        // nothing whatever its groups say, and switching it back on ticks them again.)
        //
        // Taking a lock this thread may already hold is fine in C# — locks are re-entrant, so the
        // SetItemGroupEnabled and MarkItemGroupsSeen calls below simply pass through.
        lock (gate)
        {
            return MigrateItemGroupConsentCore(groups, itemsCategoryEnabled);
        }
    }

    private bool MigrateItemGroupConsentCore(
        IReadOnlyList<ItemManifestGroup> groups, bool itemsCategoryEnabled)
    {
        // Run only once.
        if (ItemGroupConsentMigrated)
        {
            return false;
        }

        // Mark that the migration has run, regardless of the outcome.
        ItemGroupConsentMigrated = true;

        // Collect the legacy group keys once; both steps below operate on the same set. A blank
        // key is skipped rather than passed on: group data comes from the server, and a malformed
        // group must degrade gracefully here for the same reason it does in MarkItemGroupsSeen —
        // SetItemGroupEnabled would throw on it, and a throw mid-migration would leave the
        // run-once flag set with only part of the work done.
        var legacyGroupKeys = new List<string>();
        foreach (var group in groups)
        {
            if (group.Legacy && !string.IsNullOrEmpty(group.Key))
            {
                legacyGroupKeys.Add(group.Key);
            }
        }

        // Enabling is conditional on the user's prior Items consent — a group is only turned on
        // when its scope was already covered by what the user agreed to send.
        if (itemsCategoryEnabled)
        {
            foreach (var groupKey in legacyGroupKeys)
            {
                SetItemGroupEnabled(groupKey, true);
            }
        }

        // Seen-marking is UNCONDITIONAL: a legacy group predates this user's install either way,
        // so it must never wear a "New" badge — even for a user whose items consent was off.
        MarkItemGroupsSeen(legacyGroupKeys);

        return true;
    }

    /// <summary>
    /// Records that there is no pre-group consent to carry over, because the user chose their
    /// groups by hand — in the first-run wizard, from group checkboxes it put on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MigrateItemGroupConsent"/> speaks for a user who never saw a group checkbox: it
    /// reads their category-level items consent and grants the <c>legacy</c> group the scope that
    /// consent already covered. A user who <i>was</i> shown the groups has said something more
    /// specific than that, and it can disagree — leaving the legacy group unticked while opting the
    /// category itself in is a coherent, deliberate choice. Letting the migration run for them would
    /// silently re-enable exactly the group they turned down. Settling the shared
    /// <see cref="ItemGroupConsentMigrated"/> flag is what makes that impossible: the migration is
    /// run-once, so an install that starts life settled never migrates at all.
    /// </para>
    /// <para>
    /// Grants no consent of its own — the only thing it writes is the flag. What the user ticked in
    /// the wizard was already written as they ticked it.
    /// </para>
    /// </remarks>
    /// <param name="groupsWereShown">
    /// Whether the wizard actually RENDERED group checkboxes for this user — answered from what it
    /// drew, never from what the server sent. A user who was shown none made no group-level choice, so
    /// there is nothing to settle and the flag must stay unset, leaving the ordinary migration free to
    /// speak for them.
    /// </param>
    /// <returns>When true, the caller should persist the updated config.</returns>
    public bool SettleItemGroupConsent(bool groupsWereShown)
    {
        if (!groupsWereShown)
        {
            return false;
        }

        lock (gate)
        {
            // Already settled, or already migrated (they are the same flag). Nothing to write, so the
            // caller is told not to spend a config save on it.
            if (ItemGroupConsentMigrated)
            {
                return false;
            }

            ItemGroupConsentMigrated = true;
            return true;
        }
    }

    /// <summary>
    /// True when a token is present and has the shape the server issues. A local sanity check
    /// only — only the server can say whether a well-formed token is actually valid.
    /// </summary>
    // Deliberately a method rather than a property: Dalamud's serializer would otherwise write
    // this computed value into the saved config file as a redundant field.
    public bool HasUsableToken() => TokenFormat.IsWellFormed(Token);
}
