using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// Every user-facing setting, and the rules that govern them.
/// </summary>
/// <remarks>
/// Free of Dalamud types; <see cref="Configuration"/> is the thin shell Dalamud persists. Nothing
/// uploads until the user has seen what gets sent and opted in (a Dalamud rule), so every default is
/// off except <see cref="ShareOccultInstanceState"/>.
/// </remarks>
[Serializable]
public class PluginSettings
{
    /// <summary>
    /// The XIV Shinies API token, pasted by the user. Plain text in the plugin config is normal for
    /// Dalamud plugins: it is a revocable, plugin-scoped credential that cannot act on the account.
    /// </summary>
    /// <remarks>Never write this value to the log.</remarks>
    public string Token { get; set; } = string.Empty;

    /// <summary>The master switch. While false, the plugin uploads nothing at all.</summary>
    public bool MasterEnabled { get; set; }

    /// <summary>True once the user has completed the first-run wizard and chosen categories.</summary>
    public bool OnboardingComplete { get; set; }

    /// <summary>
    /// True while the user shares live Occult Crescent instance state (CE, FATE and tower status) with
    /// the tracker.
    /// </summary>
    /// <remarks>
    /// The one default-on setting: it describes world state, not the player (the Universalis
    /// precedent), and the wizard shows it ticked before anything can send. An install that finished
    /// onboarding before this setting existed starts it off (see <see cref="ApplyUpgradeMigrations"/>).
    /// </remarks>
    public bool ShareOccultInstanceState { get; set; } = true;

    /// <summary>
    /// True while the user shares Crucible runs with the website's run companion: what the Crucible's
    /// windows show out of combat, the character's and familiar's HP, and an empty check-in on a board.
    /// </summary>
    /// <remarks>
    /// The familiar's HP is read during fights too, and sent once each fight is over. Off by default,
    /// and <see cref="AutoEnableNewFeatures"/> never turns it on: it shares the player's own play as it
    /// happens, so only the user's own tick enables it.
    /// </remarks>
    public bool ShareCrucibleRuns { get; set; }

    /// <summary>
    /// True once the user has seen the Crucible sharing card while the server permitted it, or clicked
    /// its checkbox. Until then the settings screen badges the card "New".
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SeenCategoryKeys"/>, which <see cref="AutoEnableUnseenCategories"/>
    /// reads, so that method can never switch the sharing on.
    /// </remarks>
    public bool CrucibleSharingSeen { get; set; }

    /// <summary>
    /// True when the user chose to have collections added by later updates start switched on.
    /// <see cref="AutoEnableUnseenCategories"/> acts on it.
    /// </summary>
    /// <remarks>
    /// Off by default. Unlike the tracker toggle, this answers for collections the user has not read
    /// a disclosure for yet, and a pre-ticked box would collect that answer from everyone who did not
    /// untick it. New collections announce themselves either way.
    /// </remarks>
    public bool AutoEnableNewFeatures { get; set; }

    /// <summary>
    /// Brings a config written at an older <see cref="Configuration.Version"/> up to date. Called once
    /// at load; returns true when anything changed, so the caller saves.
    /// </summary>
    /// <param name="fromVersion">The version the loaded config was written at.</param>
    public bool ApplyUpgradeMigrations(int fromVersion)
    {
        var changed = false;

        // Version 1 added the default-on tracker toggle. A user who already finished the wizard never
        // saw it, so it starts off for them; a user still ahead of the wizard will see it there.
        if (fromVersion < 1 && OnboardingComplete)
        {
            ShareOccultInstanceState = false;
            changed = true;
        }

        // Version 2 added the seen-set, so an onboarded config has no record of what its wizard showed.
        // Mark that era's collections seen: a false "New" is the worse error. The wizard marks what it
        // shows, so an install still ahead of it is left alone.
        if (fromVersion < 2 && OnboardingComplete)
        {
            // One lock across both writes, so the flag and the baseline land together.
            // MarkCategoriesSeen re-enters it.
            lock (gate)
            {
                MarkCategoriesSeen(CategoriesPresentBeforeSeenTracking);
                SeenCategoriesInitialized = true;
            }

            changed = true;
        }

        // Version 3: earlier builds defaulted AutoEnableNewFeatures on, under copy that asked a narrower
        // question, so most older configs carry a true nobody ticked, mid-wizard saves included.
        if (fromVersion < 3 && AutoEnableNewFeatures)
        {
            AutoEnableNewFeatures = false;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The collections that existed before seen-tracking: the baseline the version-2 migration writes.
    /// </summary>
    /// <remarks>
    /// Frozen. Adding a later collection here would silence the "New" badge it should raise, and
    /// <c>PluginSettingsTests.The_pre_seen_tracking_baseline_never_grows</c> fails if it grows. Not a
    /// category-name branch: nothing branches on these names.
    /// </remarks>
    // Typed IReadOnlyList, not string[], so callers cannot reassign its elements.
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
    /// The backend server. User-overridable, per Dalamud's guidance; validate a new value with
    /// <see cref="BackendUrl.TryNormalize"/> before storing it.
    /// </summary>
    public string BaseUrl { get; set; } = BackendUrl.Default;

    /// <summary>
    /// True once the user has acknowledged that a non-default server receives their API token. Reset
    /// it whenever <see cref="BaseUrl"/> changes.
    /// </summary>
    public bool CustomBackendAcknowledged { get; set; }

    /// <summary>Guards every read and write of the collections below.</summary>
    /// <remarks>
    /// The settings window writes them on the render thread while each collection pass reads them on
    /// the framework thread, and a list or dictionary throws when one thread adds while another
    /// enumerates.
    /// </remarks>
    // Private, so it is never written to the config file.
    private readonly object gate = new();

    /// <summary>
    /// Which collection categories the user opted into, keyed by category key (for example
    /// <c>"quests"</c>).
    /// </summary>
    /// <remarks>
    /// A dictionary rather than a property per category, so a new collection needs no settings change
    /// or migration.
    /// </remarks>
    public Dictionary<string, bool> EnabledCategories { get; set; } = new();

    /// <summary>
    /// True when the user opted into uploading the category. An unknown key reads false, so a new
    /// collector starts opted out.
    /// </summary>
    public bool IsCategoryEnabled(string categoryKey)
    {
        // A dictionary throws on a null key.
        if (string.IsNullOrEmpty(categoryKey))
            return false;

        lock (gate)
            return EnabledCategories.TryGetValue(categoryKey, out var enabled) && enabled;
    }

    /// <summary>Opts the given category in or out.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public void SetCategoryEnabled(string categoryKey, bool enabled)
    {
        // Reads tolerate a blank key; a write with one is always a caller bug.
        ArgumentException.ThrowIfNullOrEmpty(categoryKey);

        lock (gate)
            EnabledCategories[categoryKey] = enabled;
    }

    /// <summary>
    /// The item manifest groups the user opted into, by server group key. An allowlist: a group the
    /// server adds later starts off.
    /// </summary>
    public List<string> EnabledItemGroupKeys { get; set; } = new();

    /// <summary>Group keys the settings UI has shown once. Any other group wears a "New" badge.</summary>
    /// <remarks>
    /// The list is what the config file stores. A set mirrors it for the per-frame
    /// <see cref="IsItemGroupSeen"/>, whose cost must not grow with the server's manifest. Replacing
    /// the list, as loading the config does, drops the mirror until the next read rebuilds it.
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

    // Built on first read.
    private HashSet<string>? seenItemGroupLookup;

    /// <summary>Category keys the settings UI has shown once. Any other wears a "New" badge.</summary>
    public List<string> SeenCategoryKeys { get; set; } = new();

    /// <summary>True once <see cref="InitializeSeenCategories"/> has set this install's baseline.</summary>
    public bool SeenCategoriesInitialized { get; set; }

    /// <summary>True once the one-time item group consent migration has run.</summary>
    public bool ItemGroupConsentMigrated { get; set; }

    /// <summary>
    /// True when the user opted into the item group. An unknown key reads false, so a new group starts
    /// opted out.
    /// </summary>
    // A list scan is fine: a config holds a handful of group keys.
    public bool IsItemGroupEnabled(string groupKey)
    {
        if (string.IsNullOrEmpty(groupKey))
            return false;

        lock (gate)
            return EnabledItemGroupKeys.Contains(groupKey);
    }

    /// <summary>
    /// Runs an action with the settings locked, for <see cref="Configuration.Save"/>, which walks every
    /// collection here while it serializes.
    /// </summary>
    /// <remarks>
    /// The action runs with the lock held, so keep it to that serialization. A framework-thread read
    /// may wait on the disk write. A few milliseconds is the better trade: reading a collection
    /// mid-write throws and loses the whole collection pass.
    /// </remarks>
    public void RunLocked(Action action)
    {
        lock (gate)
            action();
    }

    /// <summary>
    /// A copy of the enabled group keys, so the collection pass can read them while the user ticks a
    /// checkbox on another thread.
    /// </summary>
    public HashSet<string> SnapshotEnabledItemGroupKeys()
    {
        lock (gate)
            return new HashSet<string>(EnabledItemGroupKeys);
    }

    /// <summary>Opts the given item group in or out.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public void SetItemGroupEnabled(string groupKey, bool enabled)
    {
        // Reads tolerate a blank key; a write with one is always a caller bug.
        ArgumentException.ThrowIfNullOrEmpty(groupKey);

        lock (gate)
        {
            if (enabled)
            {
                if (!EnabledItemGroupKeys.Contains(groupKey))
                {
                    EnabledItemGroupKeys.Add(groupKey);
                }
            }
            else
            {
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

    /// <summary>Marks the given categories as seen in the settings UI.</summary>
    /// <remarks>
    /// Idempotent, and it skips blanks rather than throwing: it runs in the draw loop, where one bad
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
    /// Sets, once per install, which categories count as already seen: the baseline every later "New"
    /// badge is measured against.
    /// </summary>
    /// <param name="categoryKeys">Every category registered in this build.</param>
    /// <returns>True when this call set the baseline, so the caller saves.</returns>
    /// <remarks>
    /// A user still in the wizard gets an empty baseline, because the wizard marks each category as it
    /// draws it. An onboarded install with no baseline marks everything present seen, so familiar
    /// collections stay quiet; a pre-seen-set config already has its baseline from
    /// <see cref="ApplyUpgradeMigrations"/>.
    /// </remarks>
    public bool InitializeSeenCategories(IEnumerable<string> categoryKeys)
    {
        lock (gate)
        {
            if (SeenCategoriesInitialized)
                return false;

            SeenCategoriesInitialized = true;

            // One lock across the flag and the baseline, so they land together. MarkCategoriesSeen
            // re-enters it.
            if (OnboardingComplete)
                MarkCategoriesSeen(categoryKeys);
        }

        return true;
    }

    /// <summary>
    /// Switches on every collection this install has never shown, for a user who ticked
    /// <see cref="AutoEnableNewFeatures"/>.
    /// </summary>
    /// <param name="categoryKeys">Every category registered in this build.</param>
    /// <returns>The keys switched on, in the order given; empty when nothing changed.</returns>
    /// <remarks>
    /// The only place a collection turns on without its own tick, licensed by that standing answer.
    /// It does nothing for a user still ahead of the wizard, which shows every collection anyway. A
    /// collection the user has been shown keeps their answer forever, and an enabled collection still
    /// wears its "New" badge.
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

            // An onboarded install always has a seen-set, so an empty one means the record was lost.
            // Treating everything as unseen would switch on collections the user declined.
            if (SeenCategoryKeys.Count == 0)
                return Array.Empty<string>();

            foreach (var categoryKey in categoryKeys)
            {
                if (string.IsNullOrEmpty(categoryKey) || SeenCategoryKeys.Contains(categoryKey))
                    continue;

                // An existing entry is a decision already made, a "no" included. Leave it.
                if (EnabledCategories.ContainsKey(categoryKey))
                    continue;

                // Re-enters the lock this method holds.
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

    /// <summary>Marks the given item groups as seen in the settings UI.</summary>
    /// <remarks>
    /// Idempotent, and it skips blanks rather than throwing: the keys come from server data while the
    /// window draws. <see cref="SetItemGroupEnabled"/> throws instead, because its key comes from a
    /// row the user clicked.
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
                // The set and the list are written together so they cannot disagree.
                if (!string.IsNullOrEmpty(groupKey) && seenItemGroupLookup.Add(groupKey))
                {
                    seenItemGroupKeys.Add(groupKey);
                }
            }
        }
    }

    /// <summary>
    /// One-time migration for a user who consented to Items before groups existed: enables the groups
    /// the server marks <c>legacy</c>, which that consent covered, and marks them seen.
    /// </summary>
    /// <remarks>
    /// Every other group starts off. The caller read <paramref name="itemsCategoryEnabled"/> before
    /// this takes the lock, so a click can change it in between, harmlessly: a disabled category
    /// uploads nothing, and re-enabling it ticks its groups again.
    /// </remarks>
    /// <param name="groups">The item manifest groups from the server; empty when it sends none.</param>
    /// <param name="itemsCategoryEnabled">
    /// Whether Items was enabled. Passed in so this method names no category.
    /// </param>
    /// <returns>True when the migration ran, so the caller saves.</returns>
    public bool MigrateItemGroupConsent(
        IReadOnlyList<ItemManifestGroup> groups, bool itemsCategoryEnabled)
    {
        // One lock for the whole migration, so a checkbox click cannot land between the run-once flag
        // and its writes.
        lock (gate)
        {
            return MigrateItemGroupConsentCore(groups, itemsCategoryEnabled);
        }
    }

    private bool MigrateItemGroupConsentCore(
        IReadOnlyList<ItemManifestGroup> groups, bool itemsCategoryEnabled)
    {
        if (ItemGroupConsentMigrated)
        {
            return false;
        }

        ItemGroupConsentMigrated = true;

        // Blank keys are skipped: SetItemGroupEnabled would throw, leaving the flag set with the
        // migration half done.
        var legacyGroupKeys = new List<string>();
        foreach (var group in groups)
        {
            if (group.Legacy && !string.IsNullOrEmpty(group.Key))
            {
                legacyGroupKeys.Add(group.Key);
            }
        }

        if (itemsCategoryEnabled)
        {
            foreach (var groupKey in legacyGroupKeys)
            {
                SetItemGroupEnabled(groupKey, true);
            }
        }

        // Always: a legacy group is never new to this install, whatever its Items consent.
        MarkItemGroupsSeen(legacyGroupKeys);

        return true;
    }

    /// <summary>
    /// Settles the item group migration for a user who chose groups by hand in the wizard, so it never
    /// runs for them.
    /// </summary>
    /// <remarks>
    /// The migration speaks for a user who never saw a group checkbox. A user who did may have left
    /// the legacy group unticked on purpose, and the migration would re-enable it. This writes only
    /// the run-once flag.
    /// </remarks>
    /// <param name="groupsWereShown">
    /// Whether the wizard drew group checkboxes, judged by what it drew, not by what the server sent.
    /// </param>
    /// <returns>True when the flag was set, so the caller saves.</returns>
    public bool SettleItemGroupConsent(bool groupsWereShown)
    {
        if (!groupsWereShown)
        {
            return false;
        }

        lock (gate)
        {
            // Already settled or already migrated: one flag covers both.
            if (ItemGroupConsentMigrated)
            {
                return false;
            }

            ItemGroupConsentMigrated = true;
            return true;
        }
    }

    /// <summary>
    /// Records the wizard's outcome for the live occult tracker, the one sharing setting that starts
    /// on.
    /// </summary>
    /// <remarks>
    /// While the server has the tracker switched off, the wizard draws its box disabled, so the user
    /// cannot decline it. A wizard that could not offer the choice records it as off, the same rule
    /// <see cref="ApplyUpgradeMigrations"/> applies to installs that predate the toggle.
    /// </remarks>
    /// <param name="toggleWasOfferable">
    /// Whether the wizard drew the checkbox in a state the user could act on, judged by what it drew.
    /// </param>
    /// <returns>True when the setting changed, so the caller saves.</returns>
    public bool SettleOccultConsent(bool toggleWasOfferable)
    {
        if (toggleWasOfferable)
        {
            return false;
        }

        lock (gate)
        {
            // Already off: the user unticked it before the server withdrew the tracker.
            if (!ShareOccultInstanceState)
            {
                return false;
            }

            ShareOccultInstanceState = false;
            return true;
        }
    }

    /// <summary>
    /// True when a token is present and has the shape the server issues. Only the server can say
    /// whether it is valid.
    /// </summary>
    // A method, not a property, so the serializer does not save it into the config file.
    public bool HasUsableToken() => TokenFormat.IsWellFormed(Token);
}
