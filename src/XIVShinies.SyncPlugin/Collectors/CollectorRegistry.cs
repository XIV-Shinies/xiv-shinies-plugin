using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Builds the list of collectors the plugin runs.
/// </summary>
/// <remarks>
/// <b>This is the only place a collection is registered.</b> Adding one means adding a single
/// entry here — no change to the runner, the payload, the settings UI, or the API client. The
/// collectors are handed to <see cref="CollectorRunner"/> rather than constructed inside it,
/// because they hold Dalamud services and would otherwise make the runner impossible to test.
/// </remarks>
public static class CollectorRegistry
{
    // The user-facing copy for each collection, alongside its wire key. `WhatGetsSent` is shown next
    // to the opt-in toggle before the user consents, so it is a compliance surface: it must stay a
    // true description of what the matching collector actually uploads.

    // The section headings the consent surfaces group by (see CategoryInfo.Section). Named as
    // constants so two collections meaning the same section cannot drift apart by a typo.
    private const string CollectionLogSection = "Collection log";
    private const string TripleTriadSection = "Triple Triad";
    private const string OccultSection = "The Occult Crescent";
    private const string ItemsSection = "Items & relics";

    private static readonly CategoryInfo Quests = new()
    {
        Key = CategoryKeys.Quests,
        DisplayName = "Quests",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of quests you have completed.",
    };

    private static readonly CategoryInfo QuestSequences = new()
    {
        Key = CategoryKeys.QuestSequences,
        DisplayName = "Quest progress",
        Section = CollectionLogSection,

        // Scoped twice over, and the visible line says both halves: only quests the server named
        // are looked at, and only the journal's step position leaves the machine.
        WhatGetsSent =
            "For the specific quests XIV Shinies asks about, which step of that quest your " +
            "journal is currently on.",

        // What the step position is NOT: the journal also holds objective text and map locations,
        // and a reader has no way to know those stay behind unless it is said.
        Details =
            "Nothing is sent about any other quest, and nothing else from your journal — no " +
            "objective text, no locations.",
    };

    private static readonly CategoryInfo Mounts = new()
    {
        Key = CategoryKeys.Mounts,
        DisplayName = "Mounts",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of mounts you have unlocked.",
    };

    private static readonly CategoryInfo Minions = new()
    {
        Key = CategoryKeys.Minions,
        DisplayName = "Minions",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of minions you have unlocked.",
    };

    private static readonly CategoryInfo Achievements = new()
    {
        Key = CategoryKeys.Achievements,
        DisplayName = "Achievements",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of achievements you have earned.",
    };

    private static readonly CategoryInfo TripleTriadCards = new()
    {
        Key = CategoryKeys.TripleTriadCards,
        DisplayName = "Triple Triad cards",
        Section = TripleTriadSection,
        WhatGetsSent = "The ID numbers of Triple Triad cards you have collected.",
    };

    private static readonly CategoryInfo TripleTriadNpcs = new()
    {
        Key = CategoryKeys.TripleTriadNpcs,
        DisplayName = "Triple Triad NPCs",
        Section = TripleTriadSection,

        // The game records a per-NPC beaten flag, so the copy names that exact fact: an opponent
        // defeated at least once.
        WhatGetsSent = "The ID numbers of the Triple Triad NPCs you have defeated.",

        // "Never collect data about other characters" is the one Dalamud rule that carries a ban,
        // and the word "opponent" alone leaves a reader wondering. The reassurance belongs here
        // rather than on the visible line: no player data is sent either way, so this makes the
        // line trustworthy without changing what it discloses.
        //
        // The second sentence sets an expectation the plugin would otherwise disappoint silently.
        // The game keeps no record of defeating certain opponents — ones that count toward no
        // Triple Triad achievement — so no amount of syncing can ever report them, and a user who
        // defeated one would be left concluding the plugin is broken. Deliberately unnamed and
        // uncounted: which opponents those are is the game's business and can change with a patch,
        // and naming them would put catalog knowledge in a plugin that is meant to hold none.
        Details =
            "These are the game's computer opponents, never other players. The game keeps no " +
            "record of defeating a few of them, so those can never be reported — they stay yours " +
            "to mark by hand.",
    };

    private static readonly CategoryInfo Items = new()
    {
        Key = CategoryKeys.Items,
        DisplayName = "Relic items",
        Section = ItemsSection,

        // Currencies are named on the visible line, with gil spelled out: a balance is wealth data,
        // and "items" alone would not tell a reader that consenting to a currency group sends how
        // much gil they hold. That is a KIND of data, so it can never be demoted to the hover text.
        // Phrased conditionally because whether any currency is asked about is the server's
        // manifest choice — the sentence is true both before and after such a group exists.
        //
        // The storage clause is on the visible line for the same reason. An items upload carries
        // `itemSources` beside the counts: a per-location scan state, and for retainers both how
        // many were readable and how many the account holds. That headcount is a fact about the
        // account rather than a count of any manifest item — and it travels even when no retainer
        // was scanned — so "counts of the items XIV Shinies asks about" does not cover it, and a
        // reader would have no way to infer it. Naming the locations themselves stays in the hover:
        // that is elaboration, whereas the fact they travel at all is disclosure.
        WhatGetsSent =
            "Counts of the specific items XIV Shinies asks about, including your currency balances " +
            "(gil included) when it asks about those, plus which of your storage locations could be " +
            "read and how many retainers you have.",

        // Where the plugin looked, and that "none of this item" is itself a reported fact rather
        // than silence. Both make the count trustworthy; neither adds a kind of data to it. The
        // per-group choice sits here too, because the group checkboxes are drawn directly beneath
        // this row — the UI already shows what the sentence would be describing.
        Details =
            "Counts are checked across your inventory, Armoire, Glamour Dresser, Saddlebag, and " +
            "retainers. Having none of an item is reported too. You choose which groups to share, " +
            "and nothing outside them is looked at.",

        // The only collection whose scope comes from the server's item manifest rather than being
        // fixed at compile time, so it is the one that gets per-group consent rows in settings.
        UsesItemManifest = true,
    };

    private static readonly CategoryInfo OccultProgression = new()
    {
        Key = CategoryKeys.OccultProgression,
        DisplayName = "Phantom jobs",
        Section = OccultSection,

        // Both halves of the payload on the visible line: the per-job progress, and the
        // knowledge level with the condition under which it is captured — a window the user
        // opens themselves, never something the plugin asks the game for.
        WhatGetsSent =
            "Your phantom job levels and experience, read while you are inside the Occult " +
            "Crescent, and your knowledge level when you open the review window yourself.",

        // The knowledge level is the one fact here the plugin cannot refresh on its own, so the
        // hover says where it comes from and that its capture time travels with it — otherwise a
        // level that lags behind the game looks like a broken sync rather than an old sighting.
        Details =
            "All 24 support jobs travel together, updating each time you visit the Crescent. " +
            "The knowledge level is captured only from the review window, and is sent with the " +
            "time you opened it.",
    };

    private static readonly CategoryInfo OccultRecords = new()
    {
        Key = CategoryKeys.OccultRecords,
        DisplayName = "Occult records",
        Section = OccultSection,
        WhatGetsSent = "The ID numbers of the occult records you have discovered.",
    };

    /// <summary>Creates every collector, in the order they will be run.</summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="unlockState">Dalamud's local-player unlock state.</param>
    /// <param name="framework">Used by each collector to verify it is on the framework thread.</param>
    /// <param name="knowledgeObserver">The passive knowledge-level capture the phantom jobs collector reads.</param>
    public static IReadOnlyList<ICollector> Create(
        IDataManager dataManager,
        IUnlockState unlockState,
        IFramework framework,
        KnowledgeObserver knowledgeObserver) =>
        new ICollector[]
        {
            // `unlockState.IsQuestCompleted` is a "method group": the method is passed as a value
            // where a `Func<Quest, bool>` is expected, and C# binds the receiver (`unlockState`)
            // along with it. This is unlike JS, where passing `obj.method` bare loses `this`.
            // Nothing is invoked here — the delegate is called later, during collection.

            // Quest Excel row IDs are what the server stores, so no mapping is needed.
            new ExcelUnlockCollector<Quest>(
                Quests, dataManager, framework, row => row.RowId, unlockState.IsQuestCompleted),

            // The journal positions of the quests the server's manifest asks about. Scope comes
            // from the manifest each pass, so this needs the context rather than a sheet.
            new QuestSequenceCollector(QuestSequences, framework),

            new ExcelUnlockCollector<Mount>(
                Mounts, dataManager, framework, row => row.RowId, unlockState.IsMountUnlocked),

            // The game calls minions "Companions".
            new ExcelUnlockCollector<Companion>(
                Minions, dataManager, framework, row => row.RowId, unlockState.IsCompanionUnlocked),

            // Achievements are the one sheet the game cannot answer for until the player has opened
            // their Achievements window at least once this session. Until then we skip the category
            // rather than report an empty list, which would be a lie the server must not act on.
            new ExcelUnlockCollector<Achievement>(
                Achievements,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsAchievementComplete,
                precondition: () => unlockState.IsAchievementListLoaded
                    ? null
                    : CollectSkipReasons.AchievementListNotLoaded),

            // Cards are sheet-backed unlocks, structurally identical to mounts and minions. The
            // sheet's row 0 is a dummy; the game never marks it unlocked, and even if it did, the
            // id-zero filter in CollectResult.Ids keeps it off the wire.
            new ExcelUnlockCollector<TripleTriadCard>(
                TripleTriadCards,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsTripleTriadCardUnlocked),

            // Defeated opponents have no IUnlockState method, so this collector reads the game's
            // UIState directly — see its class remarks for the id space it reports.
            new TripleTriadNpcCollector(TripleTriadNpcs, dataManager, framework),

            // Phantom job progress lives in the occult instance director, so this one reads only
            // inside the Crescent; the knowledge sighting rides along from the observer.
            new OccultProgressionCollector(OccultProgression, framework, knowledgeObserver),

            // Discovered occult records — a client-persisted list readable anywhere, so it needs
            // no instance visit and no sheet walk: the saved list IS the seen-set.
            new OccultRecordsCollector(OccultRecords, framework),

            // The odd one out: it reports possession counts rather than IDs, and it only looks at
            // the items the server named in its manifest. The runner treats it like any other.
            new ItemCollector(Items, dataManager, framework),
        };
}
