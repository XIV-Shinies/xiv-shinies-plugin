using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests;

// Configuration implements a Dalamud interface and cannot be built outside the game, so these tests
// cover PluginSettings, which holds every setting.
public class PluginSettingsTests
{
    // The collection pass walks the enabled groups on the framework thread while the user ticks boxes on
    // the draw thread, so it gets a copy: sharing the live list would throw when the two meet.
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

    // Configuration.Save serializes inside this lock. From outside, only the run itself is observable.
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

        // The one default-on setting still sends nothing, because everything above gates it (see
        // OccultGateTests). The wizard shows its box ticked before any upload.
        Assert.True(settings.ShareOccultInstanceState);

        // See PluginSettings.AutoEnableNewFeatures for why this starts off.
        Assert.False(settings.AutoEnableNewFeatures);

        // Only the user's own tick enables Crucible sharing, and its card starts unseen.
        Assert.False(settings.ShareCrucibleRuns);
        Assert.False(settings.CrucibleSharingSeen);
    }

    // An onboarded version-0 user never saw the occult toggle in the wizard, so leaving it on would be
    // consent by omission; they opt in on the settings screen. The version-3 rule clears the standing
    // answer too.
    [Fact]
    public void Migrating_an_onboarded_version0_config_switches_the_new_sharing_defaults_off()
    {
        // Seeded true so the assertion can fail: otherwise narrowing the version-3 rule's lower bound
        // would pass every test here.
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };

        Assert.True(settings.ApplyUpgradeMigrations(fromVersion: 0));

        Assert.False(settings.ShareOccultInstanceState);
        Assert.False(settings.AutoEnableNewFeatures);

        // The version-2 rule fires too. This is the only test where both fire, so these lines catch
        // that rule drifting to `fromVersion >= 1 && fromVersion < 2`.
        Assert.True(settings.SeenCategoriesInitialized);
        Assert.True(settings.IsCategorySeen(CategoryKeys.Quests));

        // No migration marks the Crucible card seen: its badge exists to announce it to existing
        // installs.
        Assert.False(settings.CrucibleSharingSeen);
    }

    // The wizard is about to show the box, which is the consent the migration stands in for.
    [Fact]
    public void Migrating_a_config_that_never_finished_onboarding_keeps_the_defaults()
    {
        var settings = new PluginSettings();

        Assert.False(settings.ApplyUpgradeMigrations(fromVersion: 0));

        Assert.True(settings.ShareOccultInstanceState);
    }

    // Reads the constant, not a literal, so a schema bump cannot leave this asserting that an old version
    // is not migrated.
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
        // An unknown key reads as not opted in, so a future collector needs no settings migration.
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
        // An unknown key reads as not opted in, so a future item group needs no settings migration.
        Assert.False(settings.IsItemGroupEnabled("future-category"));
    }

    [Fact]
    public void Unknown_group_key_reads_disabled_without_throwing_on_null_or_empty()
    {
        var settings = new PluginSettings();

        // Reads tolerate a blank key; a write with one is a caller bug.
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

        Assert.Single(settings.EnabledItemGroupKeys, "cosmetics");
    }

    [Fact]
    public void SetItemGroupEnabled_rejects_blank_key()
    {
        var settings = new PluginSettings();

        Assert.Throws<ArgumentException>(() => settings.SetItemGroupEnabled("", true));

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

        // A legacy group is never new to this user, whatever their Items consent, so it never wears a
        // "New" badge.
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

        var changed = settings.MigrateItemGroupConsent(groups, itemsCategoryEnabled: true);
        Assert.True(changed);
        Assert.True(settings.ItemGroupConsentMigrated);

        var groups2 = new[]
        {
            new ItemManifestGroup { Key = "different", Label = "y", Ids = Array.Empty<uint>(), Legacy = true },
        };
        var changed2 = settings.MigrateItemGroupConsent(groups2, itemsCategoryEnabled: true);
        Assert.False(changed2);
        Assert.True(settings.IsItemGroupEnabled("old"));
        Assert.False(settings.IsItemGroupEnabled("different"));
        // The early return touches nothing, the seen list included.
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

        Assert.True(settings.IsItemGroupSeen("old"));
        Assert.False(settings.IsItemGroupSeen("new"));
    }

    // The settings window badges a collection this install has never shown.
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

        // An unknown or blank key reads unseen, so an unknown collection gets its badge rather than
        // missing it.
        Assert.False(settings.IsCategorySeen(""));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));
    }

    // Version 2: an onboarded config from before the seen-set gets that era's collections marked seen,
    // so only later additions wear a badge.
    [Fact]
    public void Migrating_an_onboarded_pre_seen_tracking_config_marks_the_collections_of_that_era()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };

        Assert.True(settings.ApplyUpgradeMigrations(fromVersion: 1));

        // The version-3 rule fires here too.
        Assert.False(settings.AutoEnableNewFeatures);

        Assert.True(settings.SeenCategoriesInitialized);
        Assert.All(
            PluginSettings.CategoriesPresentBeforeSeenTracking,
            key => Assert.True(settings.IsCategorySeen(key)));

        // A collection added after the baseline is new to this user.
        Assert.False(settings.IsCategorySeen(CategoryKeys.OrchestrionRolls));
    }

    // The wizard marks what it shows, so pre-marking here would retire badges it is about to display.
    [Fact]
    public void Migrating_a_pre_seen_tracking_config_that_never_onboarded_marks_nothing()
    {
        var settings = new PluginSettings();

        Assert.False(settings.ApplyUpgradeMigrations(fromVersion: 1));

        Assert.False(settings.SeenCategoriesInitialized);
        Assert.Empty(settings.SeenCategoryKeys);
    }

    // Guards the freeze documented on CategoriesPresentBeforeSeenTracking. If this fails because a
    // collection was added, leave the list alone; do not update it.
    [Fact]
    public void The_pre_seen_tracking_baseline_never_grows()
    {
        var expected = new[]
        {
            "achievements", "items", "minions", "mounts", "occultProgression",
            "occultRecords", "questSequences", "quests", "tripleTriadCards", "tripleTriadNpcs",
        };

        // Order means nothing to MarkCategoriesSeen, so a re-sort must not fail this. The count comes
        // first so an addition fails as a plain count mismatch.
        Assert.Equal(expected.Length, PluginSettings.CategoriesPresentBeforeSeenTracking.Count);
        Assert.Equal(
            expected.OrderBy(key => key, StringComparer.Ordinal),
            PluginSettings.CategoriesPresentBeforeSeenTracking.OrderBy(key => key, StringComparer.Ordinal));
    }

    // The wizard is about to show every collection and marks each as it draws, so pre-marking would
    // claim they were shown before they were.
    [Fact]
    public void The_seen_baseline_marks_nothing_before_onboarding()
    {
        var settings = new PluginSettings { OnboardingComplete = false };

        Assert.True(settings.InitializeSeenCategories(new[] { "quests", "mounts" }));

        Assert.False(settings.IsCategorySeen("quests"));
        Assert.False(settings.IsCategorySeen("mounts"));
    }

    // Nothing records which categories the wizard showed, so today's list is the only baseline
    // available.
    [Fact]
    public void The_seen_baseline_takes_todays_categories_as_seen_after_onboarding()
    {
        var settings = new PluginSettings { OnboardingComplete = true };

        Assert.True(settings.InitializeSeenCategories(new[] { "quests", "mounts" }));

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.True(settings.IsCategorySeen("mounts"));

        // The baseline covers collections only; the Crucible card keeps its own record.
        Assert.False(settings.CrucibleSharingSeen);
    }

    // A re-run would mark every collection added since as seen, swallowing the badges it exists to
    // show.
    [Fact]
    public void The_seen_baseline_is_established_only_once()
    {
        var settings = new PluginSettings { OnboardingComplete = true };
        settings.InitializeSeenCategories(new[] { "quests" });

        Assert.False(settings.InitializeSeenCategories(new[] { "quests", "orchestrionRolls" }));

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));
    }

    // A fresh install's real sequence: baseline before onboarding, the wizard marking rows, then
    // onboarding completing. A later launch must not re-baseline, or no addition would ever badge for
    // a fresh install.
    [Fact]
    public void The_seen_baseline_survives_onboarding_completing_after_it_ran()
    {
        var settings = new PluginSettings { OnboardingComplete = false };
        Assert.True(settings.InitializeSeenCategories(new[] { "quests" }));

        // The wizard marks what it draws.
        settings.MarkCategoriesSeen(new[] { "quests" });
        settings.OnboardingComplete = true;

        // A later launch of a build that added a collection.
        Assert.False(settings.InitializeSeenCategories(new[] { "quests", "orchestrionRolls" }));

        Assert.True(settings.IsCategorySeen("quests"));
        Assert.False(settings.IsCategorySeen("orchestrionRolls"));
    }

    // --- Auto-enabling a collection the install has never shown ---

    [Fact]
    public void An_unseen_category_is_switched_on_for_a_user_who_asked_for_that()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };
        settings.MarkCategoriesSeen(new[] { "quests", "mounts" });
        settings.SetCategoryEnabled("mounts", false);

        var enabled = settings.AutoEnableUnseenCategories(new[] { "quests", "mounts", "orchestrionRolls" });

        Assert.Equal(new[] { "orchestrionRolls" }, enabled);
        Assert.True(settings.IsCategoryEnabled("orchestrionRolls"));

        // Shown and switched off stays off: once seen, the answer is the user's.
        Assert.False(settings.IsCategoryEnabled("mounts"));
    }

    // The standing answer is the only license to switch anything on without a click.
    [Fact]
    public void Nothing_is_switched_on_when_the_user_did_not_ask_for_that()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = false };

        var enabled = settings.AutoEnableUnseenCategories(new[] { "orchestrionRolls" });

        Assert.Empty(enabled);
        Assert.False(settings.IsCategoryEnabled("orchestrionRolls"));
    }

    // Ticking the box in the wizard persists it at once, so a load before onboarding finishes sees the
    // flag set. The wizard, which shows every collection, is still that user's consent surface.
    [Fact]
    public void Nothing_is_switched_on_before_onboarding_is_complete()
    {
        var settings = new PluginSettings { OnboardingComplete = false, AutoEnableNewFeatures = true };

        var enabled = settings.AutoEnableUnseenCategories(new[] { "quests", "orchestrionRolls" });

        Assert.Empty(enabled);
        Assert.Empty(settings.EnabledCategories);
    }

    // A collection already on was the user's doing; reporting it would log a change that never
    // happened.
    [Fact]
    public void A_category_already_switched_on_is_not_reported_as_changed()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };

        // A baseline without orchestrionRolls, so the already-on skip is what returns nothing.
        // Without it the empty-baseline floor would return first.
        settings.MarkCategoriesSeen(new[] { "quests" });
        settings.SetCategoryEnabled("orchestrionRolls", true);

        Assert.Empty(settings.AutoEnableUnseenCategories(new[] { "orchestrionRolls" }));
        Assert.True(settings.IsCategoryEnabled("orchestrionRolls"));
    }

    // The guard reads the stored answer itself, so it holds even when the seen-set does not list the
    // category: a stored "no" is an answer no later load may reverse.
    [Fact]
    public void A_category_answered_no_is_never_switched_back_on()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };
        settings.MarkCategoriesSeen(new[] { "quests" });

        // Answered no but never announced, so absent from the seen-set.
        settings.SetCategoryEnabled("orchestrionRolls", false);

        Assert.Empty(settings.AutoEnableUnseenCategories(new[] { "orchestrionRolls" }));
        Assert.False(settings.IsCategoryEnabled("orchestrionRolls"));
    }

    // Called at load, so it skips what it cannot use rather than throw into the plugin's constructor.
    [Fact]
    public void Auto_enabling_tolerates_a_null_sequence_and_skips_blanks()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };

        // Without a baseline nothing counts as new (see
        // Nothing_is_switched_on_when_the_seen_baseline_is_missing).
        settings.MarkCategoriesSeen(new[] { "quests" });

        Assert.Empty(settings.AutoEnableUnseenCategories(null!));

        Assert.Equal(
            new[] { "orchestrionRolls" },
            settings.AutoEnableUnseenCategories(new[] { "", "orchestrionRolls" }));
    }

    // Drawing a collection marks it seen, so the user's switch-off sticks across later loads.
    [Fact]
    public void Switching_an_auto_enabled_category_back_off_survives_the_next_load()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };

        // A baseline from an earlier load, so the empty-baseline floor does not stop the first call.
        settings.MarkCategoriesSeen(new[] { "quests" });

        Assert.Equal(
            new[] { "orchestrionRolls" },
            settings.AutoEnableUnseenCategories(new[] { "orchestrionRolls" }));

        // Drawing the settings list is what marks it seen; the user then unticks it.
        settings.MarkCategoriesSeen(new[] { "orchestrionRolls" });
        settings.SetCategoryEnabled("orchestrionRolls", false);

        Assert.Empty(settings.AutoEnableUnseenCategories(new[] { "orchestrionRolls" }));
        Assert.False(settings.IsCategoryEnabled("orchestrionRolls"));
    }

    // Version 3 clears the standing answer, so the user re-answers against copy that names collections.
    // ApplyUpgradeMigrations says why.
    [Fact]
    public void Migrating_an_onboarded_pre_collection_config_switches_the_standing_answer_off()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };

        Assert.True(settings.ApplyUpgradeMigrations(fromVersion: 2));

        Assert.False(settings.AutoEnableNewFeatures);
    }

    // Mid-wizard too: this rule is not gated on OnboardingComplete, while the occult rule is.
    [Fact]
    public void Migrating_a_pre_collection_config_clears_the_standing_answer_mid_wizard_too()
    {
        var settings = new PluginSettings { OnboardingComplete = false, AutoEnableNewFeatures = true };

        Assert.True(settings.ApplyUpgradeMigrations(fromVersion: 2));

        Assert.False(settings.AutoEnableNewFeatures);

        Assert.True(settings.ShareOccultInstanceState);
    }

    // The caller saves anyway to persist the version bump, but the return value must not claim a write
    // that did not happen.
    [Fact]
    public void Migrating_a_config_whose_standing_answer_is_already_off_reports_no_change()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = false };

        Assert.False(settings.ApplyUpgradeMigrations(fromVersion: 2));
    }

    // AutoEnableUnseenCategories says why an empty seen-set means the record was lost.
    [Fact]
    public void Nothing_is_switched_on_when_the_seen_baseline_is_missing()
    {
        var settings = new PluginSettings
        {
            OnboardingComplete = true,
            AutoEnableNewFeatures = true,
            SeenCategoriesInitialized = true,
        };
        settings.SetCategoryEnabled("mounts", false);

        Assert.Empty(settings.AutoEnableUnseenCategories(new[] { "quests", "mounts" }));
        Assert.False(settings.IsCategoryEnabled("quests"));
        Assert.False(settings.IsCategoryEnabled("mounts"));
    }

    // Plugin.cs's load order: the baseline first, so auto-enable finds every collection already seen.
    [Fact]
    public void The_load_sequence_switches_nothing_on_for_an_install_with_no_new_collections()
    {
        var settings = new PluginSettings { OnboardingComplete = true, AutoEnableNewFeatures = true };
        var registered = new[] { "quests", "mounts" };

        settings.InitializeSeenCategories(registered);

        Assert.Empty(settings.AutoEnableUnseenCategories(registered));
        Assert.Empty(settings.EnabledCategories);
    }

    // The full load order: migrate, baseline, auto-enable.
    [Fact]
    public void The_upgrade_sequence_switches_on_only_the_collection_added_since_the_baseline()
    {
        var settings = new PluginSettings { OnboardingComplete = true };
        var registered = new List<string>(PluginSettings.CategoriesPresentBeforeSeenTracking)
        {
            CategoryKeys.OrchestrionRolls,
        };

        settings.ApplyUpgradeMigrations(fromVersion: 1);

        // The version-3 rule cleared it; the user re-opts in on the settings screen.
        settings.AutoEnableNewFeatures = true;
        settings.InitializeSeenCategories(registered);

        Assert.Equal(
            new[] { CategoryKeys.OrchestrionRolls },
            settings.AutoEnableUnseenCategories(registered));
        Assert.All(
            PluginSettings.CategoriesPresentBeforeSeenTracking,
            key => Assert.False(settings.IsCategoryEnabled(key)));
    }

    // The baseline runs at every load with no user present, and breaking Dalamud's consent rule can
    // mean rejection or a ban, so a test guards that it touches only the seen-set.
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

        settings.MarkItemGroupsSeen(new[] { "a" });
        Assert.Single(settings.SeenItemGroupKeys, "a");
    }

    [Fact]
    public void Seen_tracking_is_best_effort_about_malformed_input()
    {
        var settings = new PluginSettings();

        // Seen keys come from server data while the window draws, so bad input must not throw.
        settings.MarkItemGroupsSeen(null!);
        Assert.Empty(settings.SeenItemGroupKeys);

        settings.MarkItemGroupsSeen(new[] { "a", "", "b" });
        Assert.True(settings.IsItemGroupSeen("a"));
        Assert.True(settings.IsItemGroupSeen("b"));
        Assert.Equal(2, settings.SeenItemGroupKeys.Count);
    }

    [Fact]
    public void Seen_reads_tolerate_blank_keys()
    {
        var settings = new PluginSettings();

        Assert.False(settings.IsItemGroupSeen(null!));
        Assert.False(settings.IsItemGroupSeen(""));
    }

    [Fact]
    public void Migration_skips_a_blank_legacy_group_key_and_keeps_going()
    {
        var settings = new PluginSettings();

        // Group data comes from the server. Without the skip, SetItemGroupEnabled would throw
        // mid-migration and leave the run-once flag set with the work half done.
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

        // Settling never grants consent; the user's choices are what they ticked in the wizard.
        Assert.Empty(settings.EnabledItemGroupKeys);
    }

    [Fact]
    public void Settling_group_consent_does_nothing_when_the_wizard_showed_no_groups()
    {
        var settings = new PluginSettings();

        // No group checkbox was drawn, because the server sent no groups or the poll failed. The user
        // made no group choice, so the migration must keep its run-once flag to speak for them later.
        Assert.False(settings.SettleItemGroupConsent(groupsWereShown: false));
        Assert.False(settings.ItemGroupConsentMigrated);
    }

    [Fact]
    public void A_settled_install_never_migrates_afterwards()
    {
        var settings = new PluginSettings();

        // The user saw the groups and left the legacy one off, while opting Items in.
        var shown = new[]
        {
            new ItemManifestGroup { Key = "old", Label = "x", Ids = Array.Empty<uint>(), Legacy = true },
        };
        settings.SetCategoryEnabled("items", true);
        settings.SettleItemGroupConsent(groupsWereShown: true);

        // The migration carries a pre-group user's Items consent onto the legacy group. This user
        // chose explicitly, so the first /config poll must not re-enable the group they turned down.
        var migrated = settings.MigrateItemGroupConsent(shown, itemsCategoryEnabled: true);

        Assert.False(migrated);
        Assert.False(settings.IsItemGroupEnabled("old"));
    }

    // A user shown no group checkboxes settled nothing, so the first poll carrying groups migrates their
    // Items consent onto the legacy group.
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

        // Nothing to write, so the caller is told not to save.
        Assert.False(settings.SettleItemGroupConsent(groupsWereShown: true));
        Assert.True(settings.ItemGroupConsentMigrated);
    }

    // The tracker starts on, so a wizard that could not offer the choice must record it off: the user
    // never got to decline it.
    [Fact]
    public void A_wizard_that_could_not_offer_the_tracker_records_it_off()
    {
        var settings = new PluginSettings();
        Assert.True(settings.ShareOccultInstanceState);

        Assert.True(settings.SettleOccultConsent(toggleWasOfferable: false));
        Assert.False(settings.ShareOccultInstanceState);
    }

    // A box shown ticked and left alone is the user's answer.
    [Fact]
    public void A_wizard_that_offered_the_tracker_leaves_the_users_answer_alone()
    {
        var settings = new PluginSettings();

        Assert.False(settings.SettleOccultConsent(toggleWasOfferable: true));
        Assert.True(settings.ShareOccultInstanceState);
    }

    // The user unticked the box before the server withdrew it, so there is nothing to save.
    [Fact]
    public void Settling_tracker_consent_that_is_already_off_reports_no_change()
    {
        var settings = new PluginSettings {ShareOccultInstanceState = false};

        Assert.False(settings.SettleOccultConsent(toggleWasOfferable: false));
        Assert.False(settings.ShareOccultInstanceState);
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

        // Migrating and settling share one flag.
        Assert.False(settings.SettleItemGroupConsent(groupsWereShown: true));
    }

    // With A_current_version_config_is_not_migrated, this brackets Configuration.CurrentVersion: one
    // version below it, the newest arm fires. Whoever adds an arm retargets this test at that arm's
    // state, so the pair keeps a new arm from shipping dead behind an unbumped constant.
    [Fact]
    public void The_newest_migration_fires_one_version_below_the_constant()
    {
        var settings = new PluginSettings {AutoEnableNewFeatures = true};

        Assert.True(settings.ApplyUpgradeMigrations(Configuration.CurrentVersion - 1));
        Assert.False(settings.AutoEnableNewFeatures);
    }
}
