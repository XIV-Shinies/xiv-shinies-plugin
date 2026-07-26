using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

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

    private static readonly CategoryInfo Quests = new()
    {
        Key = CategoryKeys.Quests,
        DisplayName = "Quests",
        WhatGetsSent = "The ID numbers of quests you have completed.",
    };

    private static readonly CategoryInfo QuestSequences = new()
    {
        Key = CategoryKeys.QuestSequences,
        DisplayName = "Quest progress",

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
        WhatGetsSent = "The ID numbers of mounts you have unlocked.",
    };

    private static readonly CategoryInfo Minions = new()
    {
        Key = CategoryKeys.Minions,
        DisplayName = "Minions",
        WhatGetsSent = "The ID numbers of minions you have unlocked.",
    };

    private static readonly CategoryInfo Achievements = new()
    {
        Key = CategoryKeys.Achievements,
        DisplayName = "Achievements",
        WhatGetsSent = "The ID numbers of achievements you have earned.",
    };

    private static readonly CategoryInfo TripleTriadCards = new()
    {
        Key = CategoryKeys.TripleTriadCards,
        DisplayName = "Triple Triad cards",
        WhatGetsSent = "The ID numbers of Triple Triad cards you have collected.",
    };

    private static readonly CategoryInfo TripleTriadNpcs = new()
    {
        Key = CategoryKeys.TripleTriadNpcs,
        DisplayName = "Triple Triad NPCs",

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

        // Currencies are named on the visible line, with gil spelled out: a balance is wealth data,
        // and "items" alone would not tell a reader that consenting to a currency group sends how
        // much gil they hold. That is a KIND of data, so it can never be demoted to the hover text.
        // Phrased conditionally because whether any currency is asked about is the server's
        // manifest choice — the sentence is true both before and after such a group exists.
        WhatGetsSent =
            "Counts of the specific items XIV Shinies asks about, including your currency balances " +
            "(gil included) when it asks about those.",

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

    /// <summary>Creates every collector, in the order they will be run.</summary>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="unlockState">Dalamud's local-player unlock state.</param>
    /// <param name="framework">Used by each collector to verify it is on the framework thread.</param>
    public static IReadOnlyList<ICollector> Create(
        IDataManager dataManager, IUnlockState unlockState, IFramework framework) =>
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

            // The odd one out: it reports possession counts rather than IDs, and it only looks at
            // the items the server named in its manifest. The runner treats it like any other.
            new ItemCollector(Items, dataManager, framework),
        };
}
