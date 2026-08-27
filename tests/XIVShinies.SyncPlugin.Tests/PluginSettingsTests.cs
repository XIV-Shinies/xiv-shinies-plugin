using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests;

// PluginSettings holds every user-facing setting and is deliberately Dalamud-free, so the suite
// can construct it directly. Its persistence shell (Configuration) implements a Dalamud interface
// and therefore cannot be instantiated outside the game — that part is covered by in-game QA.
public class PluginSettingsTests
{
    // The collection pass reads the enabled groups on the framework thread while the user can be ticking
    // a checkbox on the draw thread, and a list cannot be walked and added to at once — so what the pass
    // gets is a COPY, taken while the settings are held still. Handing back the live list instead would
    // leave the two threads sharing one collection, and the pass would throw the moment they met.
    [Fact]
    public void The_enabled_group_snapshot_does_not_change_underneath_its_reader()
    {
        var settings = new PluginSettings();
        settings.SetItemGroupEnabled("proofs", true);

        var snapshot = settings.SnapshotEnabledItemGroupKeys();

        settings.SetItemGroupEnabled("materials", true);
        settings.SetItemGroupEnabled("proofs", false);

        Assert.Single(snapshot);
        Assert.Contains("proofs", snapshot);
        Assert.DoesNotContain("materials", snapshot);
    }

    // The hook the config save uses to serialize these collections without another thread writing to them
    // mid-walk. Nothing to observe from outside but that the work runs, which is what this pins.
    [Fact]
    public void Running_locked_runs_the_work()
    {
        var settings = new PluginSettings();
        var ran = false;

        settings.RunLocked(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Defaults_send_nothing_until_the_user_opts_in()
    {
        var settings = new PluginSettings();

        // Explicit opt-in is a Dalamud compliance rule: a fresh install must upload nothing.
        Assert.False(settings.MasterEnabled);
        Assert.False(settings.OnboardingComplete);
        Assert.False(settings.CustomBackendAcknowledged);
        Assert.Equal(BackendUrl.Default, settings.BaseUrl);
        Assert.Equal(string.Empty, settings.Token);

        // The two defaults that ARE true — the occult toggle and the future-features choice —
        // still send nothing on a fresh install, because everything above gates them (see
        // OccultGateTests); both boxes are visible and ticked on the wizard's consent step
        // before any upload can happen.
        Assert.True(settings.ShareOccultInstanceState);
        Assert.True(settings.AutoEnableNewFeatures);
    }

    // The upgrade migration: a version-0 config whose onboarding already ran belongs to a user
    // the wizard will never show the new toggles to, so defaulting them on would be consent by
    // omission — they start OFF and the settings screen is where that user opts in.
    [Fact]
    public void Migrating_an_onboarded_version0_config_switches_the_new_sharing_defaults_off()
    {
        var settings = new PluginSettings { OnboardingComplete = true };

        Assert.True(settings.ApplyUpgradeMigrations(fromVersion: 0));

        Assert.False(settings.ShareOccultInstanceState);
        Assert.False(settings.AutoEnableNewFeatures);

        // A version-0 config predates the seen-set too, so the later rule applies as well. This is
        // the only case where both fire, so without these two lines a rule that had drifted to
        // `fromVersion >= 1 && fromVersion < 2` would pass every test in the file.
        Assert.True(settings.SeenCategoriesInitialized);
        Assert.True(settings.IsCategorySeen(CategoryKeys.Quests));
    }

    // An install still ahead of its wizard keeps the defaults: the wizard is about to put both
    // boxes in front of them, which is exactly the consent surface the migration exists to
    // substitute for.
    [Fact]
    public void Migrating_a_config_that_never_finished_onboarding_keeps_the_defaults()
    {
        var settings = new PluginSettings();

        Assert.False(settings.ApplyUpgradeMigrations(fromVersion: 0));

        Assert.True(settings.ShareOccultInstanceState);
        Assert.True(settings.AutoEnableNewFeatures);
    }

    // Read from the constant rather than a literal, so bumping the schema cannot leave this test
    // quietly asserting that an OLD version is not migrated — which is the opposite of its point.
    [Fact]
    public void A_current_version_config_is_not_migrated()
    {
        var settings = new PluginSettings { OnboardingComplete = true };

        Assert.False(settings.ApplyUpgradeMigrations(Configuration.CurrentVersion));

        Assert.True(settings.ShareOccultInstanceState);
        Assert.Empty(settings.SeenCategoryKeys);
    }

    [Fact]
    public void No_category_is_enabled_until_it_is_explicitly_chosen()
    {
        var settings = new PluginSettings();

        Assert.False(settings.IsCategoryEnabled("quests"));
        // An unknown key is simply "not opted in" — a future collector needs no settings migration.
        Assert.False(settings.IsCategoryEnabled("facewear"));
    }

    [Fact]
    public void Categories_are_toggled_by_key_so_new_collectors_need_no_settings_change()
    {
        var settings = new PluginSettings();

        settings.SetCategoryEnabled("quests", true);
        Assert.True(settings.IsCategoryEnabled("quests"));
        Assert.False(settings.IsCategoryEnabled("mounts"));

        settings.SetCategoryEnabled("quests", false);
        Assert.False(settings.IsCategoryEnabled("quests"));
    }

    [Fact]
    public void A_token_is_usable_only_when_it_is_well_formed()
    {
        var settings = new PluginSettings();
        Assert.False(settings.HasUsableToken());

        settings.Token = "nonsense";
        Assert.False(settings.HasUsableToken());

        settings.Token = "xvs_" + new string('a', 43);
        Assert.True(settings.HasUsableToken());
    }

    [Fact]
    public void No_item_group_is_enabled_until_it_is_explicitly_chosen()
    {
        var settings = new PluginSettings();

        Assert.False(settings.IsItemGroupEnabled("never-seen"));
        // An unknown key is simply "not opted in" — a future item group needs no settings migration.
        Assert.False(settings.IsItemGroupEnabled("future-category"));
    }

    [Fact]
    public void Unknown_group_key_reads_disabled_without_throwing_on_null_or_empty()
    {
        var settings = new PluginSettings();

        // Reading tolerates blank keys (returns false); writing one is always a caller bug.
        Assert.False(settings.IsItemGroupEnabled(null!));
        Assert.False(settings.IsItemGroupEnabled(""));
    }

    [Fact]
    public void Item_groups_are_toggled_by_key_so_new_groups_need_no_settings_change()
    {
        var settings = new PluginSettings();

        settings.SetItemGroupEnabled("cosmetics", true);
        Assert.True(settings.IsItemGroupEnabled("cosmetics"));
        Assert.False(settings.IsItemGroupEnabled("weapons"));

        settings.SetItemGroupEnabled("cosmetics", false);
        Assert.False(settings.IsItemGroupEnabled("cosmetics"));
    }

    [Fact]
    public void Setting_item_group_enabled_twice_does_not_duplicate_list_entry()
    {
        var settings = new PluginSettings();

        settings.SetItemGroupEnabled("cosmetics", true);
        settings.SetItemGroupEnabled("cosmetics", true);

        // The list should contain exactly one entry for "cosmetics".
        Assert.Single(settings.EnabledItemGroupKeys, "cosmetics");
    }

    [Fact]
    public void SetItemGroupEnabled_rejects_blank_key()
    {
        var settings = new PluginSettings();

        // Empty string should throw ArgumentException.
        Assert.Throws<ArgumentException>(() => settings.SetItemGroupEnabled("", true));

        // Null should throw ArgumentNullException, which derives from ArgumentException.
        Assert.Throws<ArgumentNullException>(() => settings.SetItemGroupEnabled(null!, true));
    }

    [Fact]
    public void Migration_with_items_consent_on_enables_exactly_legacy_groups()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "new", Label = "y", Ids = Array.Empty<uint>(), Legacy = false },
        };

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        Assert.True(changed);
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("new"));
        Assert.True(settings.ItemGroupConsentMigrated);
    }

    [Fact]
    public void Migration_with_items_consent_off_enables_nothing_but_still_completes()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "new", Label = "y", Ids = Array.Empty<uint>(), Legacy = false },
        };

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: false);

        Assert.True(changed);
        Assert.False(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("new"));
        Assert.True(settings.ItemGroupConsentMigrated);

        // Seen-marking is unconditional: a legacy group is not new to this user even when their
        // items consent was off, so it must never earn a "New" badge. The non-legacy group IS
        // new and stays unseen.
        Assert.True(settings.IsItemGroupSeen("old"));
        Assert.False(settings.IsItemGroupSeen("new"));
    }

    [Fact]
    public void Migration_runs_only_once()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };

        // First call should return true and mark the flag.
        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);
        Assert.True(changed);
        Assert.True(settings.ItemGroupConsentMigrated);

        // Second call should return false and change nothing.
        var groups2 = new[]
        {
            new ItemManifestGroup { Key = "different", Label = "y", Ids = Array.Empty<uint>(), Legacy = true },
        };
        var changed2 = settings.MigrateItemGroupConsent(groups2, itemsCategoryEnabled: true);
        Assert.False(changed2);
        // The original group should still be enabled, the new one should not have been added.
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("different"));
        // The early return must touch nothing — the seen list included.
        Assert.False(settings.IsItemGroupSeen("different"));
    }

    [Fact]
    public void Legacy_groups_are_marked_seen_by_migration()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "new", Label = "y", Ids = Array.Empty<uint>(), Legacy = false },
        };

        settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        // Legacy groups should be marked as seen — they are not new to this user.
        Assert.True(settings.IsItemGroupSeen("old"));
        // Non-legacy groups arriving at migration time are new — not seen.
        Assert.False(settings.IsItemGroupSeen("new"));
    }

    // The category-level twin of the group seen-tracking below. Same contract, one level up: the
    // settings badge a collection the install has never shown.
    [Fact]
    public void Seen_tracking_marks_categories_and_tolerates_duplicates()
    {
        var settings = new PluginSettings();

        settings.MarkCategoriesSeen(new[] { "quests", "mounts" });

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.True(settings.IsCategorySeen("mounts"));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));

        settings.MarkCategoriesSeen(new[] { "quests" });
        Assert.Equal(2, settings.SeenCategoryKeys.Count);
    }

    [Fact]
    public void Seen_category_tracking_is_best_effort_about_malformed_input()
    {
        var settings = new PluginSettings();

        // Called from the draw loop, so one malformed entry must not take the frame down.
        settings.MarkCategoriesSeen(null!);
        Assert.Empty(settings.SeenCategoryKeys);

        settings.MarkCategoriesSeen(new[] { "quests", "", "mounts" });
        Assert.True(settings.IsCategorySeen("quests"));
        Assert.True(settings.IsCategorySeen("mounts"));
        Assert.Equal(2, settings.SeenCategoryKeys.Count);

        // An unrecorded or blank key reads as unseen, the same fail-safe direction
        // IsCategoryEnabled takes — an unknown collection gets its badge rather than missing one.
        Assert.False(settings.IsCategorySeen(""));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));
    }

    // The version-2 migration. An onboarded config written before the seen-set gets the era's
    // collections marked seen, so anything added since is the first thing to wear a badge.
    [Fact]
    public void Migrating_an_onboarded_pre_seen_tracking_config_marks_the_collections_of_that_era()
    {
        var settings = new PluginSettings { OnboardingComplete = true };

        Assert.True(settings.ApplyUpgradeMigrations(fromVersion: 1));

        Assert.True(settings.SeenCategoriesInitialized);
        Assert.All(
            PluginSettings.CategoriesPresentBeforeSeenTracking,
            key => Assert.True(settings.IsCategorySeen(key)));

        // The whole point: a collection added after that baseline is new to this user.
        Assert.False(settings.IsCategorySeen(CategoryKeys.OrchestrionRolls));
    }

    // An install still ahead of its wizard is left alone — the wizard marks what it shows, so
    // pre-marking here would retire badges for collections it is about to display.
    [Fact]
    public void Migrating_a_pre_seen_tracking_config_that_never_onboarded_marks_nothing()
    {
        var settings = new PluginSettings();

        Assert.False(settings.ApplyUpgradeMigrations(fromVersion: 1));

        Assert.False(settings.SeenCategoriesInitialized);
        Assert.Empty(settings.SeenCategoryKeys);
    }

    // Guards the freeze documented on CategoriesPresentBeforeSeenTracking. If this fails because a
    // collection was added, the fix is to leave the list alone, not to update it.
    [Fact]
    public void The_pre_seen_tracking_baseline_never_grows()
    {
        var expected = new[]
        {
            "achievements", "items", "minions", "mounts", "occultProgression",
            "occultRecords", "questSequences", "quests", "tripleTriadCards", "tripleTriadNpcs",
        };

        // Compared order-insensitively: order means nothing to MarkCategoriesSeen, so a re-sort
        // should not fail here and send the next reader hunting for a problem that is not there.
        // The count is asserted first so an addition fails as a plain count mismatch rather than a
        // sequence diff.
        Assert.Equal(expected.Length, PluginSettings.CategoriesPresentBeforeSeenTracking.Count);
        Assert.Equal(
            expected.OrderBy(key => key, StringComparer.Ordinal),
            PluginSettings.CategoriesPresentBeforeSeenTracking.OrderBy(key => key, StringComparer.Ordinal));
    }

    // A fresh install pre-marks nothing: the wizard is about to show every collection and marks
    // each one as it draws. Pre-marking would claim they were shown before they were.
    [Fact]
    public void The_seen_baseline_marks_nothing_before_onboarding()
    {
        var settings = new PluginSettings { OnboardingComplete = false };

        Assert.True(settings.InitializeSeenCategories(new[] { "quests", "mounts" }));

        Assert.False(settings.IsCategorySeen("quests"));
        Assert.False(settings.IsCategorySeen("mounts"));
    }

    // Onboarding complete with no baseline recorded: nothing says which categories the wizard
    // showed, so today's list is the only baseline this entry point can build from.
    [Fact]
    public void The_seen_baseline_takes_todays_categories_as_seen_after_onboarding()
    {
        var settings = new PluginSettings { OnboardingComplete = true };

        Assert.True(settings.InitializeSeenCategories(new[] { "quests", "mounts" }));

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.True(settings.IsCategorySeen("mounts"));
    }

    // The baseline is one-shot. Were it to re-run, it would swallow every collection added since —
    // exactly the badge the mechanism exists to show.
    [Fact]
    public void The_seen_baseline_is_established_only_once()
    {
        var settings = new PluginSettings { OnboardingComplete = true };
        settings.InitializeSeenCategories(new[] { "quests" });

        Assert.False(settings.InitializeSeenCategories(new[] { "quests", "orchestrionRolls" }));

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));
    }

    // The sequence a fresh install actually walks, across the one transition the guard exists to
    // survive: baseline before onboarding, the wizard marking rows as it draws them, then
    // onboarding completing. A later launch must NOT re-baseline over a collection added since —
    // if it did, no addition would ever badge for anyone who installed fresh.
    [Fact]
    public void The_seen_baseline_survives_onboarding_completing_after_it_ran()
    {
        var settings = new PluginSettings { OnboardingComplete = false };
        Assert.True(settings.InitializeSeenCategories(new[] { "quests" }));

        // The wizard draws the collections it offers, which is what marks them.
        settings.MarkCategoriesSeen(new[] { "quests" });
        settings.OnboardingComplete = true;

        // A later launch of a build that added a collection.
        Assert.False(settings.InitializeSeenCategories(new[] { "quests", "orchestrionRolls" }));

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));
    }

    // The baseline runs unconditionally at plugin load, with no user present. Consent is
    // ban-enforced by Dalamud, so "it only touches the seen-set" gets a guard rather than resting
    // on a reading of the code.
    [Fact]
    public void The_seen_baseline_grants_no_consent()
    {
        var settings = new PluginSettings { OnboardingComplete = true };

        settings.InitializeSeenCategories(new[] { "quests", "orchestrionRolls" });

        Assert.Empty(settings.EnabledCategories);
        Assert.Empty(settings.EnabledItemGroupKeys);
        Assert.False(settings.MasterEnabled);
        Assert.False(settings.IsCategoryEnabled("orchestrionRolls"));
    }

    [Fact]
    public void Seen_tracking_marks_groups_and_tolerates_duplicates()
    {
        var settings = new PluginSettings();

        settings.MarkItemGroupsSeen(new[] { "a", "b" });

        Assert.True(settings.IsItemGroupSeen("a"));
        Assert.True(settings.IsItemGroupSeen("b"));
        Assert.False(settings.IsItemGroupSeen("c"));

        // Calling again with "a" should not duplicate the list entry.
        settings.MarkItemGroupsSeen(new[] { "a" });
        Assert.Single(settings.SeenItemGroupKeys, "a");
    }

    [Fact]
    public void Seen_tracking_is_best_effort_about_malformed_input()
    {
        var settings = new PluginSettings();

        // A null sequence is a no-op — seen keys come from server-supplied group data during UI
        // rendering, so malformed input must degrade gracefully rather than throw mid-draw.
        settings.MarkItemGroupsSeen(null!);
        Assert.Empty(settings.SeenItemGroupKeys);

        // A blank key in the middle of a batch is skipped; its valid neighbors are still marked.
        settings.MarkItemGroupsSeen(new[] { "a", "", "b" });
        Assert.True(settings.IsItemGroupSeen("a"));
        Assert.True(settings.IsItemGroupSeen("b"));
        Assert.Equal(2, settings.SeenItemGroupKeys.Count);
    }

    [Fact]
    public void Seen_reads_tolerate_blank_keys()
    {
        var settings = new PluginSettings();

        // Reading tolerates blank keys (returns false), mirroring IsItemGroupEnabled.
        Assert.False(settings.IsItemGroupSeen(null!));
        Assert.False(settings.IsItemGroupSeen(""));
    }

    [Fact]
    public void Migration_skips_a_blank_legacy_group_key_and_keeps_going()
    {
        var settings = new PluginSettings();

        // Group data comes from the server, so a malformed group must degrade gracefully: the
        // blank key is skipped while its valid sibling is still enabled and marked seen. Without
        // the skip, SetItemGroupEnabled would throw mid-migration and strand the run-once flag
        // with only part of the work done.
        var groups = new[]
        {
            new ItemManifestGroup { Key = "", Label = "broken", Ids = Array.Empty<uint>(), Legacy = true },
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        Assert.True(changed);
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.True(settings.IsItemGroupSeen("old"));
        Assert.Single(settings.EnabledItemGroupKeys, "old");
        Assert.Single(settings.SeenItemGroupKeys, "old");
    }

    [Fact]
    public void Migration_with_no_groups_still_completes_and_touches_nothing()
    {
        var settings = new PluginSettings();

        // An empty (non-null) groups list is a valid migration: the flag flips so it never runs
        // again, but there is nothing to enable or mark seen.
        var changed = settings.MigrateItemGroupConsent(
            Array.Empty<ItemManifestGroup>(), itemsCategoryEnabled: true);

        Assert.True(changed);
        Assert.True(settings.ItemGroupConsentMigrated);
        Assert.Empty(settings.EnabledItemGroupKeys);
        Assert.Empty(settings.SeenItemGroupKeys);
    }

    [Fact]
    public void Settling_group_consent_marks_the_migration_done_when_the_wizard_showed_groups()
    {
        var settings = new PluginSettings();

        var changed = settings.SettleItemGroupConsent(groupsWereShown: true);

        Assert.True(changed);
        Assert.True(settings.ItemGroupConsentMigrated);

        // Settling records that there is nothing to carry over — it never grants consent of its
        // own. The user's choices are exactly what they ticked in the wizard.
        Assert.Empty(settings.EnabledItemGroupKeys);
    }

    [Fact]
    public void Settling_group_consent_does_nothing_when_the_wizard_showed_no_groups()
    {
        var settings = new PluginSettings();

        // The wizard never drew a group checkbox: the config it waited for carried no groups, whether
        // because the server sent none or because the poll failed outright. Either way the user made no
        // group-level choice, so the migration is still the only thing that can speak for them — and
        // burning the run-once flag here would silence it forever.
        Assert.False(settings.SettleItemGroupConsent(groupsWereShown: false));
        Assert.False(settings.ItemGroupConsentMigrated);
    }

    [Fact]
    public void A_settled_install_never_migrates_afterwards()
    {
        var settings = new PluginSettings();

        // The user saw the groups in the wizard and deliberately left the legacy one off, while
        // opting the items category itself in.
        var shown = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };
        settings.SetCategoryEnabled("items", true);
        settings.SettleItemGroupConsent(groupsWereShown: true);

        // The first /config poll after onboarding must NOT resurrect the group they turned down:
        // the migration exists to carry a PRE-GROUP user's category consent onto the legacy group,
        // and this user's choice was explicit.
        var migrated = settings.MigrateItemGroupConsent(shown, itemsCategoryEnabled: true);

        Assert.False(migrated);
        Assert.False(settings.IsItemGroupEnabled("old"));
    }

    // The other half of that rule, and the one that keeps a user who was shown no group checkboxes from
    // being stranded: nothing was settled, so the first poll to arrive carrying groups migrates their
    // category-level items consent onto the legacy group.
    [Fact]
    public void An_unsettled_install_still_migrates_afterwards()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };

        settings.SetCategoryEnabled("items", true);
        settings.SettleItemGroupConsent(groupsWereShown: false);

        var migrated = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        Assert.True(migrated);
        Assert.True(settings.IsItemGroupEnabled("old"));
    }

    [Fact]
    public void Settling_group_consent_twice_reports_no_second_change()
    {
        var settings = new PluginSettings();

        Assert.True(settings.SettleItemGroupConsent(groupsWereShown: true));

        // Already settled — the second call has nothing to write, so the caller is told not to save.
        Assert.False(settings.SettleItemGroupConsent(groupsWereShown: true));
        Assert.True(settings.ItemGroupConsentMigrated);
    }

    [Fact]
    public void A_migrated_install_is_already_settled()
    {
        var settings = new PluginSettings();
        var groups = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };

        settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);

        // The two share one flag, so a migration that has already run leaves nothing to settle.
        Assert.False(settings.SettleItemGroupConsent(groupsWereShown: true));
    }
}
