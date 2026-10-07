using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using XIVShinies.SyncPlugin.Beastmaster;
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
    private const string ItemsSection = "Items & relics";
    private const string LimitedJobsSection = "Limited jobs";
    private const string GlamourSection = "Glamour";

    /// <summary>
    /// The Occult Crescent section's heading. Public, unlike its siblings, because the wizard's
    /// disclosure list places the live tracker — occult data, but not a collection — under the
    /// same heading, so the title needs one home: the collectors' declared section and the
    /// wizard's placement of the tracker cannot drift apart by a typo.
    /// </summary>
    public const string OccultSection = "The Occult Crescent";

    private static readonly CategoryInfo Quests = new()
    {
        Key = CategoryKeys.Quests,
        DisplayName = "Quests",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of quests you have completed.",

        // The game answers for every row in the Quest sheet, and the catalog is a pruned subset of
        // that same sheet — the safe direction, since a sweep of the whole sheet cannot miss a
        // cataloged quest.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo QuestSequences = new()
    {
        Key = CategoryKeys.QuestSequences,
        DisplayName = "Quest progress",
        Section = CollectionLogSection,

        // Scoped twice over, and the visible line says both halves: only quests the server named
        // are looked at, and only the step position leaves the machine.
        //
        // The game calls the quest log the Journal, and XIV Shinies has a Journal feature of its
        // own, so this copy says "in game" instead: the word would read as a fact about the site
        // rather than about a multi-part quest the user is part-way through — a relic chain,
        // typically.
        WhatGetsSent =
            "For the specific quests XIV Shinies asks about, how far through that quest you have " +
            "got in game.",

        // What the step position is NOT: the game also tracks objective text and map locations
        // against an active quest, and a reader has no way to know those stay behind unless it is
        // said.
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

        // The plain case: the game answers for every Mount sheet row the catalog can hold, so an
        // exhaustive walk is an exhaustive answer.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo Minions = new()
    {
        Key = CategoryKeys.Minions,
        DisplayName = "Minions",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of minions you have unlocked.",

        // The plain case, exactly as for mounts: the game answers for every Companion sheet row the
        // catalog can hold.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo Achievements = new()
    {
        Key = CategoryKeys.Achievements,
        DisplayName = "Achievements",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of achievements you have earned.",

        // Once the game will answer at all, it answers for every row in the Achievement sheet, so
        // the sweep covers everything the catalog can hold. Whether it will answer is a separate
        // question, handled by this collector's precondition (see its construction below), which
        // skips the pass rather than reporting a list the game had not filled in.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo OrchestrionRolls = new()
    {
        Key = CategoryKeys.OrchestrionRolls,
        DisplayName = "Orchestrion rolls",
        Section = CollectionLogSection,
        WhatGetsSent = "The ID numbers of the orchestrion rolls you have unlocked.",

        // Owning the roll item and having unlocked the tune are separate states, and only the
        // unlock is readable. Without this sentence a user with unplayed rolls in their bags would
        // read the missing entries as a broken sync. It is elaboration rather than a kind of data,
        // so it belongs in the hover instead of the visible line.
        Details =
            "A roll counts once you have used it and it is playable from your orchestrion list. " +
            "An unused roll still sitting in your inventory is not yet unlocked, so it is not " +
            "reported until you use it.",

        // The game answers for every Orchestrion row, and the catalog holds a subset of those same
        // rows, so the sweep covers it.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo TripleTriadCards = new()
    {
        Key = CategoryKeys.TripleTriadCards,
        DisplayName = "Triple Triad cards",
        Section = TripleTriadSection,
        WhatGetsSent = "The ID numbers of Triple Triad cards you have collected.",

        // The game answers for every row in the card sheet, so the sweep covers every card the
        // catalog can hold.
        EnumeratesCompleteDomain = true,
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

        // The game keeps no beaten flag for some opponents the server's catalog lists, so an absent
        // id here can mean "beaten but unreadable". See TripleTriadNpcCollector's class remarks.
        EnumeratesCompleteDomain = false,
    };

    private static readonly CategoryInfo Items = new()
    {
        Key = CategoryKeys.Items,
        // Named without any kind of item: the manifest's set is the server's to change at any
        // time (see UsesItemManifest below), and a name that named one would go stale.
        DisplayName = "Tracked items",
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
        // than silence. Both make the count trustworthy; neither adds a kind of data to it.
        //
        // The group clause is hedged because the groups are optional: the server decides whether to
        // send the item manifest in groups at all, and when it sends a flat list no group
        // checkboxes are drawn and there is no per-group choice.
        Details =
            "Counts are checked across your inventory, Armoire, Glamour Dresser, Saddlebag, and " +
            "retainers. Having none of an item is reported too. When these items are offered in " +
            "groups below, you choose which groups to share and nothing outside them is looked at.",

        // The only collection whose scope comes from the server's item manifest rather than being
        // fixed at compile time, so it is the one that gets per-group consent rows in settings.
        UsesItemManifest = true,

        // Counts are read out of the storage containers, and each pass reports their scan state,
        // so the settings panel shows the container lines while this is on.
        ReadsStorage = true,
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
        // hover names the NPC and the menu option that produce it, and says its capture time
        // travels with it. Otherwise a level that lags behind the game looks like a broken sync
        // rather than an old sighting.
        Details =
            "All 24 support jobs travel together, updating each time you visit the Crescent. " +
            "The knowledge level is captured only when you choose \"Review your knowledge " +
            "level and currencies\" at Jeffroy in Phantom Village, and is sent with the time " +
            "you opened it.",
    };

    private static readonly CategoryInfo OccultRecords = new()
    {
        Key = CategoryKeys.OccultRecords,
        DisplayName = "Occult records",
        Section = OccultSection,
        WhatGetsSent = "The ID numbers of the occult records you have discovered.",

        // Not a sheet sweep: the seen-set is a client-persisted list the game appends every
        // discovery to, and reading it is reading the whole domain. See OccultRecordsCollector's
        // class remarks.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo TamedBeasts = new()
    {
        Key = CategoryKeys.TamedBeasts,
        DisplayName = "Tamed beasts",
        Section = LimitedJobsSection,

        // The job is named here because no other surface names it: the section header groups every
        // limited job together, the display name is the collection, and the hover text calls the
        // window by its in-game title. A player who wants to know whether their Beastmaster
        // progress syncs is looking for that word, and this is the line they read without hovering.
        WhatGetsSent = "The ID numbers of the beasts you have tamed as Beastmaster.",

        // The one action the player has to take. The bestiary is only readable while it is on
        // screen, so a player who never opens it sees nothing arrive and would read that as a
        // broken sync rather than as the single step it is. It is elaboration rather than a kind of
        // data, so it belongs in the hover instead of the visible line.
        Details =
            "This reads your Master's Bestiary whenever you open it, so page through it once with " +
            "no filter applied and every beast you have tamed is recorded. Nothing is read while " +
            "the window is closed, and other players are never involved.",

        // The entitlement to claim completeness at all. A collection whose domain is always
        // readable passes this straight through to the CollectResult; this one's becomes readable
        // only when the player opens the window, so its collector additionally requires the pass to
        // have earned it — see TamedBeastLedger.IsComplete for what has to agree.
        EnumeratesCompleteDomain = true,
    };

    private static readonly CategoryInfo Glamour = new()
    {
        Key = CategoryKeys.Glamour,
        DisplayName = "Gear & glamour storage",
        Section = GlamourSection,

        // Every kind of data on the visible line, and nothing more. The copy counts are named
        // because "the gear you hold" alone would not tell a reader that how many of each piece they
        // hold travels too. Where each piece is kept, its quality and its dyes are facts about the
        // piece beyond its id: a loose dresser piece carries `hq: true` when it is high quality, and
        // its two dye channels when they could be read. "Down to which retainer" says the location
        // goes as far as naming the retainer that holds a piece. The storage clause names the source
        // notes that travel beside these facts too, worded as the items category's is and for the
        // reason given there; the retainers' names close it, because a name is a kind of data no
        // other part of the sentence implies.
        WhatGetsSent =
            "The gear you hold, with copy counts, where each piece is kept (down to which " +
            "retainer), its quality and its dyes, plus which storage locations could be read, how " +
            "many retainers you have and their names.",

        // Which locations are searched, named one by one as the items category names its own, and
        // grouped by when each is read, which the player cannot see happen. The live containers are
        // read on every pass; the dresser, Armoire and saddlebag only once their window has been
        // opened and closed this session; the retainers from the game's saved copy, which outlasts
        // the session, plus the live market listings of the retainer summoned most recently, and
        // their names only once the game has loaded its retainer list at a summoning bell. Without
        // that timing, a dresser never opened this session simply does not arrive and reads as a
        // broken sync. All of it is elaboration: the fact that storage locations travel at all is
        // disclosure and sits on the visible line.
        //
        // Written as "where: when" pairs so a hover can be scanned rather than read; the README
        // carries the longer account. "Armoury chest" is the game's own spelling, and the inventory
        // chip's hover (see SourceNoteText) uses it too, so one screen never shows two spellings of
        // the same window. The storage-window clause matches the skip hint the settings window
        // shows, and the closing clause is the reassurance that no other player's gear is involved.
        Details =
            "Bags, equipped gear and armoury chest: every login, scheduled or manual sync. Glamour " +
            "Dresser, Armoire and saddlebag: once you've opened and closed each this session. " +
            "Retainers: the game's saved copy, plus the market listings of the one summoned last, " +
            "and their names once you've used a summoning bell this session. Nothing is read while " +
            "a storage window is open, and only your own character is read.",

        // Held back until the server names it. The snapshot is large, and the server reads it as
        // current holdings, so it is only worth reading and sending to a server that knows it.
        RequiresServerSupport = true,

        // The pieces are read out of the same storage containers the items category reads, and each
        // pass reports their scan state. A dresser or saddlebag that has not been opened this session
        // is simply missing from the snapshot, so the container lines telling the player which
        // window to open matter here at least as much as they do for the item counts.
        ReadsStorage = true,
    };

    private static readonly CategoryInfo Appearance = new()
    {
        Key = CategoryKeys.Appearance,
        DisplayName = "Character appearance",
        Section = GlamourSection,

        // Three kinds of data, each named on the visible line: the appearance chosen in the character
        // creator, the glasses worn (the wire carries which glasses, not merely whether any are),
        // and the display settings that decide what of the character's gear is shown.
        WhatGetsSent =
            "Your character's appearance from the character creator, the glasses you wear, and " +
            "your display settings.",

        // What each of those covers, spelled out: the creator's choices in a reader's terms rather
        // than as the game's customization bytes, and every display setting that travels by name.
        // Then why it is read and how often, and the reassurance that no other player is involved.
        // All of it details the three kinds the visible line names, so it belongs in the hover.
        //
        // "How often" names the full syncs only: login, scheduled and manual. The small upload an
        // unlock triggers carries only the unlocked collection, so the appearance never rides on it.
        Details =
            "Race, clan, gender, face, hair, eyes, colors and body as set in the character " +
            "creator, and whether your weapon, headgear, visor, Viera ears and Free Company crest " +
            "are shown. Read from your own character at login and on each scheduled or manual " +
            "sync, so XIV Shinies can draw you as you are; nothing about any other player is read.",

        // Held back until the server names it: a record sent to a server that does not store
        // appearances would be a disclosure for nothing.
        RequiresServerSupport = true,

        // One record about the character, not a collection of things, so the upload log names it
        // without a count (see CategoryInfo.IsSingleRecord).
        IsSingleRecord = true,
    };

    /// <summary>Creates every collector, in the order they will be run.</summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="unlockState">Dalamud's local-player unlock state.</param>
    /// <param name="framework">Used by each collector to verify it is on the framework thread.</param>
    /// <param name="knowledgeObserver">The passive knowledge-level capture the phantom jobs collector reads.</param>
    /// <param name="tamedBeastObserver">The passive bestiary capture the tamed beasts collector reads.</param>
    /// <param name="gameGui">
    /// Dalamud's access to the game's windows, used by the glamour collector to check, before it
    /// reads, whether a storage window is open.
    /// </param>
    /// <param name="condition">
    /// The game's condition flags, used by the glamour collector to check whether a summoning bell
    /// is in use before it reads.
    /// </param>
    public static IReadOnlyList<ICollector> Create(
        IDataManager dataManager,
        IUnlockState unlockState,
        IFramework framework,
        KnowledgeObserver knowledgeObserver,
        TamedBeastObserver tamedBeastObserver,
        IGameGui gameGui,
        ICondition condition) =>
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

            // The tunes, not the items: `Orchestrion` row ids are what the game's unlock state
            // answers for. The "… Orchestrion Roll" items are a separate Item-sheet id space the
            // server stores apart, so an item id sent here is dropped as unknown.
            new ExcelUnlockCollector<Orchestrion>(
                OrchestrionRolls,
                dataManager,
                framework,
                row => row.RowId,
                unlockState.IsOrchestrionUnlocked),

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

            // Reads no game state during a pass: its facts are captured as the player opens their
            // bestiary, and the pass hands over whatever has accumulated. See TamedBeastCollector.
            new TamedBeastCollector(TamedBeasts, framework, tamedBeastObserver),

            // The odd one out: it reports possession counts rather than IDs, and it only looks at
            // the items the server named in its manifest. The runner treats it like any other.
            new ItemCollector(Items, dataManager, framework),

            // A snapshot of current holdings rather than a list that only grows: the dresser, the
            // outfit glamours, the Armoire and the gear held elsewhere. It reads the same storage as
            // the items collector; GlamourCollector.Collect and its class remarks say when it skips a pass.
            new GlamourCollector(Glamour, dataManager, framework, gameGui, condition),

            // One record about the local character's look, read from the character itself rather
            // than from any container. A transformed character is skipped, never reported.
            new AppearanceCollector(Appearance, framework),
        };
}
