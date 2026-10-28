using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XIVShinies.SyncPlugin.Achievements;
using XIVShinies.SyncPlugin.Beastmaster;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Builds the list of collectors the plugin runs.
/// </summary>
/// <remarks>
/// The only place a collection is registered: adding one adds an entry here and changes nothing in
/// the runner, payload, settings UI or API client. Collectors are built here, not in
/// <see cref="CollectorRunner"/>, because they hold Dalamud services and the runner holds none.
/// </remarks>
public static class CollectorRegistry
{
    // Each collection's user-facing copy. WhatGetsSent is a compliance surface (see
    // ICollector.WhatGetsSent): it must stay true to what the matching collector uploads.

    // The headings the consent surfaces group by (see CategoryInfo.Section), named once so a typo
    // cannot split a section.
    private const string CollectionLogSection = "Collection log";
    private const string TripleTriadSection = "Triple Triad";
    private const string ItemsSection = "Items & relics";
    private const string LimitedJobsSection = "Limited jobs";

    /// <summary>
    /// The Occult Crescent section's heading. Public because the wizard files the live tracker, which
    /// is occult data but not a collection, under the same heading.
    /// </summary>
    public const string OccultSection = "The Occult Crescent";

    private static readonly CategoryInfo Quests = new()
    {
        Key = CategoryKeys.Quests,
        DisplayName = "Quests",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of quests you have completed.",

        // The game answers for every Quest row, and the catalog is a pruned subset of that sheet, so
        // the sweep cannot miss a cataloged quest.
        EnumeratesCompleteDomain = true,

        UploadsOnUnlock = true,
    };

    private static readonly CategoryInfo QuestSequences = new()
    {
        Key = CategoryKeys.QuestSequences,
        DisplayName = "Quest progress",
        Section = CollectionLogSection,

        // Scoped twice, and the visible line says both: only quests the server names, and only the
        // step position. "In game" stands in for the game's word "Journal", which would read as the
        // site's own Journal feature. HostPlaceholder.Token is the site's address (see HostPlaceholder).
        WhatGetsSent =
            "For the specific quests " + HostPlaceholder.Token + " asks about, how far through that " +
            "quest you have got in game.",

        // The game also tracks objective text and map locations for an active quest, so the hover
        // says those stay behind.
        Details =
            "Nothing is sent about any other quest, and nothing else about the ones it does ask " +
            "about — no objective text, no locations.",
    };

    private static readonly CategoryInfo Mounts = new()
    {
        Key = CategoryKeys.Mounts,
        DisplayName = "Mounts",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of mounts you have unlocked.",

        // The game answers for every Mount row the catalog can hold.
        EnumeratesCompleteDomain = true,

        UploadsOnUnlock = true,
    };

    private static readonly CategoryInfo Minions = new()
    {
        Key = CategoryKeys.Minions,
        DisplayName = "Minions",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of minions you have unlocked.",

        // The game answers for every Companion row the catalog can hold.
        EnumeratesCompleteDomain = true,

        UploadsOnUnlock = true,
    };

    private static readonly CategoryInfo Achievements = new()
    {
        Key = CategoryKeys.Achievements,
        DisplayName = "Achievements",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of achievements you have earned.",

        // Once the game answers at all, it answers for every Achievement row. Whether it answers is
        // the precondition's job (see the collector's construction below).
        EnumeratesCompleteDomain = true,

        UploadsOnUnlock = true,
    };

    private static readonly CategoryInfo AchievementProgress = new()
    {
        Key = CategoryKeys.AchievementProgress,
        DisplayName = "Achievement progress",
        Section = CollectionLogSection,

        // Dropped once stored: a re-sent reading would bring back progress the player deleted on the
        // website.
        StoredResponseKey = "storedAchievementProgress",

        // The consent disclosure, so it names every field a reading sends.
        WhatGetsSent =
            "The progress the game receives while your Achievements window is open, normally for " +
            "each achievement you open there, and when it arrived.",

        // What triggers a read, and that the plugin never asks for progress itself: without the first,
        // an achievement never opened looks like a broken sync.
        Details =
            "Opening an achievement in game is what reads its progress, one achievement at a time, " +
            "and it goes up within five minutes, or when you log out. The plugin never asks the game " +
            "for progress itself.",
    };

    private static readonly CategoryInfo OrchestrionRolls = new()
    {
        Key = CategoryKeys.OrchestrionRolls,
        DisplayName = "Orchestrion rolls",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of the orchestrion rolls you have unlocked.",

        // Only the unlock is readable, not owning the roll item; without this, unused rolls in the
        // bags read as a broken sync. It is elaboration, not a kind of data, so it belongs in the hover.
        Details =
            "A roll counts once you have used it and it is playable from your orchestrion list. " +
            "An unused roll still sitting in your inventory is not yet unlocked, so it is not " +
            "reported until you use it.",

        // The game answers for every Orchestrion row, and the catalog is a subset of those rows.
        EnumeratesCompleteDomain = true,

        UploadsOnUnlock = true,
    };

    private static readonly CategoryInfo TripleTriadCards = new()
    {
        Key = CategoryKeys.TripleTriadCards,
        DisplayName = "Triple Triad cards",
        Section = TripleTriadSection,
        WhatGetsSent = "The ID numbers of Triple Triad cards you have collected.",

        // The game answers for every card row, so the sweep covers the catalog.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo TripleTriadNpcs = new()
    {
        Key = CategoryKeys.TripleTriadNpcs,
        DisplayName = "Triple Triad NPCs",
        Section = TripleTriadSection,

        // Names the exact fact the game records: a per-NPC flag for an opponent beaten at least once.
        WhatGetsSent = "The ID numbers of the Triple Triad NPCs you have defeated.",

        // "Never other players" answers the doubt "opponent" raises about Dalamud's one ban-carrying
        // rule; it adds no kind of data, so it is hover text. The game keeps no flag for opponents that
        // count toward no Triple Triad achievement, so the copy warns a user who beat one, without naming
        // them: which ones is the game's to change, and the plugin holds no catalog knowledge.
        Details =
            "These are the game's computer opponents, never other players. The game keeps no " +
            "record of defeating a few of them, so those can never be reported — they stay yours " +
            "to mark by hand.",

        // The game keeps no beaten flag for some opponents the server's catalog lists, so an absent
        // id can mean "beaten but unreadable". See TripleTriadNpcCollector's class remarks.
        EnumeratesCompleteDomain = false,
    };

    private static readonly CategoryInfo Items = new()
    {
        Key = CategoryKeys.Items,
        // Names no kind of item: the manifest is the server's to change (see UsesItemManifest below).
        DisplayName = "Tracked items",
        Section = ItemsSection,

        // Every kind of data is on the visible line. Gil is spelled out: a balance is wealth data
        // that "items" would not disclose, and "when it asks" holds because currency groups are the
        // server's choice. The storage clause covers `itemSources`, sent beside the counts: each
        // location's scan state, plus how many retainers were readable and how many the account has,
        // even when none was scanned. The locations' names are elaboration, so they stay in the hover.
        WhatGetsSent =
            "Counts of the specific items " + HostPlaceholder.Token + " asks about, including your " +
            "currency balances (gil included) when it asks about those, plus which of your storage " +
            "locations could be read and how many retainers you have.",

        // Where the plugin looked, and that "none" is itself reported: trust, not a new kind of data.
        // The group clause is hedged because the server may send a flat list, with no group choice.
        Details =
            "Counts are checked across your inventory, Armoire, Glamour Dresser, Saddlebag, and " +
            "retainers. Having none of an item is reported too. When these items are offered in " +
            "groups, you choose which groups to share and nothing outside them is looked at.",

        // The only collection scoped by the server's item manifest, so it gets per-group consent rows.
        UsesItemManifest = true,
    };

    private static readonly CategoryInfo OccultProgression = new()
    {
        Key = CategoryKeys.OccultProgression,
        DisplayName = "Phantom jobs",
        Section = OccultSection,

        // Both halves of the payload: per-job progress, and the knowledge level with when it is
        // captured, from a window the user opens, never one the plugin asks the game for.
        WhatGetsSent =
            "Your phantom job levels and experience, read while you are inside the Occult " +
            "Crescent, and your knowledge level when you open the review window yourself.",

        // The plugin cannot refresh the knowledge level, so the hover names what captures it and that
        // its time travels with it; otherwise a lagging level looks like a broken sync.
        Details =
            "All 24 support jobs travel together, read by the automatic sync or Sync now while " +
            "you are inside the Crescent. The knowledge level is captured only when you choose " +
            "\"Review your knowledge level and currencies\" at Jeffroy in Phantom Village, and " +
            "goes up within five minutes, or when you log out, with the time you opened it.",
    };

    private static readonly CategoryInfo OccultRecords = new()
    {
        Key = CategoryKeys.OccultRecords,
        DisplayName = "Occult records",
        Section = OccultSection,
        WhatGetsSent = "The ID numbers of the occult records you have discovered.",

        // Not a sheet sweep: the game appends every discovery to a client-persisted seen-set, so
        // reading it reads the whole domain. See OccultRecordsCollector's class remarks.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo TamedBeasts = new()
    {
        Key = CategoryKeys.TamedBeasts,
        DisplayName = "Tamed beasts",
        Section = LimitedJobsSection,

        // Names the job, which no other surface does: the section groups every limited job and the
        // hover names only the window. A player asking whether Beastmaster syncs looks for the word.
        WhatGetsSent = "The ID numbers of the beasts you have tamed as Beastmaster, and each one's rank.",

        // Names the actions that record this collection: ranks need a talk with the Crucible's NPC,
        // and the bestiary must be on screen. Without them, silence reads as a broken sync.
        Details =
            "Talking to the NPC at the Crucible of the Unbroken's entrance records your beasts and " +
            "each one's rank. Paging through your Master's Bestiary once with no filter applied " +
            "confirms the whole set and adds any beast the NPC's record could not: one still at " +
            "rank 1 with no experience, or every beast if a game update moves the record. Nothing " +
            "else about you is read for this beyond the zone you are in, and other players are " +
            "never involved.",

        // Only the entitlement: the bestiary is readable only once the player opens it, so the
        // collector also requires the pass to earn the claim (see TamedBeastLedger.IsComplete).
        EnumeratesCompleteDomain = true,
    };

    /// <summary>Creates every collector, in the order they will be run.</summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="unlockState">Dalamud's local-player unlock state.</param>
    /// <param name="framework">Used by each collector to verify it is on the framework thread.</param>
    /// <param name="knowledgeObserver">
    /// The passive knowledge-level capture the phantom jobs collector reads.
    /// </param>
    /// <param name="tamedBeastObserver">
    /// The passive bestiary capture the tamed beasts collector reads.
    /// </param>
    /// <param name="achievementProgressObserver">
    /// The passive progress capture the achievement progress collector reads.
    /// </param>
    public static IReadOnlyList<ICollector> Create(
        IDataManager dataManager,
        IUnlockState unlockState,
        IFramework framework,
        KnowledgeObserver knowledgeObserver,
        TamedBeastObserver tamedBeastObserver,
        AchievementProgressObserver achievementProgressObserver) =>
        new ICollector[]
        {
            // The server stores Quest row ids, so no mapping is needed.
            new ExcelUnlockCollector<Quest>(
                Quests, dataManager, framework, row => row.RowId, unlockState.IsQuestCompleted),

            // Scoped by the server's manifest each pass, so it reads the context, not a sheet.
            new QuestSequenceCollector(QuestSequences, framework),

            new ExcelUnlockCollector<Mount>(
                Mounts, dataManager, framework, row => row.RowId, unlockState.IsMountUnlocked),

            // The game calls minions "Companions".
            new ExcelUnlockCollector<Companion>(
                Minions, dataManager, framework, row => row.RowId, unlockState.IsCompanionUnlocked),

            // The game cannot answer for achievements until the player opens the Achievements window
            // once this session. Until then skip: an empty list would be a lie the server must not act on.
            new ExcelUnlockCollector<Achievement>(
                Achievements,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsAchievementComplete,
                precondition: () => unlockState.IsAchievementListLoaded
                    ? null
                    : CollectSkipReasons.AchievementListNotLoaded),

            // Reads no game state during a pass: the observer takes each reading as the player opens
            // an achievement. See AchievementProgressCollector.
            new AchievementProgressCollector(AchievementProgress, framework, achievementProgressObserver),

            // Orchestrion row ids, which the unlock state answers for. The "… Orchestrion Roll" items
            // are a separate Item-sheet id space, which the server drops as unknown here.
            new ExcelUnlockCollector<Orchestrion>(
                OrchestrionRolls,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsOrchestrionUnlocked),

            // Row 0 is a dummy the game never marks unlocked; CollectResult.Ids drops id zero anyway.
            new ExcelUnlockCollector<TripleTriadCard>(
                TripleTriadCards,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsTripleTriadCardUnlocked),

            // No IUnlockState method covers defeated opponents, so this reads UIState directly; see
            // its class remarks for the id space it reports.
            new TripleTriadNpcCollector(TripleTriadNpcs, dataManager, framework),

            // Phantom job progress lives in the occult instance director, so this reads only inside
            // the Crescent; the knowledge sighting comes from the observer.
            new OccultProgressionCollector(OccultProgression, framework, knowledgeObserver),

            // Readable anywhere: the client-persisted list is the seen-set, so no visit or sheet walk.
            new OccultRecordsCollector(OccultRecords, framework),

            // Reads no game state during a pass: the observer captures facts as the player opens the
            // bestiary. See TamedBeastCollector.
            new TamedBeastCollector(TamedBeasts, framework, tamedBeastObserver),

            // Counts rather than ids, for the items the server's manifest names; the runner treats
            // it like any other.
            new ItemCollector(Items, dataManager, framework),
        };
}
