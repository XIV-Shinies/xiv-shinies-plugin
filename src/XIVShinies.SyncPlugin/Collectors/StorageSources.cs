using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Whether each storage source can be read right now, and the source note a collector reports for
/// it.
/// </summary>
/// <remarks>
/// The runner keeps one note per source across all collectors, so a collector gates its read on the
/// <c>Is…</c>/<c>Has…</c> predicate here and takes the note built on that same predicate: a gate and
/// its note cannot disagree, and neither can two collectors (<see cref="Saddlebag"/> is the one note
/// that can say "read" with its gate shut). Each method reads the game's flag when called, so a
/// collector calls gate and note in one framework update, where the flag cannot change between them.
/// Only the local player's own storage and quest progress are consulted.
/// </remarks>
// Unsafe because FFXIVClientStructs returns pointers into live game memory.
internal static unsafe class StorageSources
{
    // "My Feisty Little Chocobo", whose completion unlocks the chocobo companion and its saddlebag.
    // A Quest sheet row id, the form the game's unlock-or-quest check takes.
    private const uint ChocoboCompanionQuestId = 66698;

    /// <summary>The live containers: bags, equipped gear and the armory chest.</summary>
    /// <remarks>
    /// Always live, with no flag to consult. Report it only after walking them this pass: the note
    /// says the walk happened.
    /// </remarks>
    public static ItemSourceStatus Inventory() => new() { State = SourceStates.Live };

    /// <summary>True when the game has the Armoire's contents loaded right now.</summary>
    /// <param name="uiState">The game's UI state, or null when it is not available.</param>
    /// <remarks>
    /// Read from <c>UIState</c>, not the item finder. The game fetches the contents on the first open
    /// each session and at times while the window is open; until they load, asking returns a
    /// confident "no".
    /// </remarks>
    public static bool IsArmoireLoaded(UIState* uiState) =>
        uiState is not null && uiState->Cabinet.IsCabinetLoaded();

    /// <summary>The Armoire's note: loaded, or unscanned.</summary>
    /// <param name="uiState">The game's UI state, or null when it is not available.</param>
    public static ItemSourceStatus Armoire(UIState* uiState) => new()
    {
        State = IsArmoireLoaded(uiState) ? SourceStates.Loaded : SourceStates.Unscanned,
    };

    /// <summary>
    /// True when the item finder's copy of the glamour dresser was refreshed this session.
    /// </summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// The item finder refreshes its copy each time the dresser window closes, and the flag turns true
    /// then. At login the copy already holds ids loaded from disk, so the flag, not the copy's
    /// contents, says whether it was read this session.
    /// </remarks>
    public static bool IsDresserCached(ItemFinderModule* finder) =>
        finder is not null && finder->IsGlamourDresserCached;

    /// <summary>The glamour dresser's note: cached, or unscanned.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    public static ItemSourceStatus GlamourDresser(ItemFinderModule* finder) => new()
    {
        State = IsDresserCached(finder) ? SourceStates.Cached : SourceStates.Unscanned,
    };

    /// <summary>
    /// True when the item finder's copy of the saddlebag, premium half included, was refreshed this
    /// session.
    /// </summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// Works like <see cref="IsDresserCached"/>, refreshed when the saddlebag window closes. Its ids
    /// use the item finder's high-quality encoding (see <see cref="GameItemId"/>).
    /// </remarks>
    public static bool IsSaddlebagCached(ItemFinderModule* finder) =>
        finder is not null && finder->IsSaddleBagCached;

    /// <summary>The saddlebag's note: cached, or unscanned.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <param name="uiState">The game's UI state, or null when it is not available.</param>
    /// <remarks>
    /// The rules are <see cref="SaddlebagNote.Build"/>'s. A character without the chocobo companion
    /// has no saddlebag, so the note reports it read while the gate stays shut and the collectors add
    /// nothing from it.
    /// </remarks>
    // With no UI state the player's state counts as not loaded, which makes the quest answer moot.
    public static ItemSourceStatus Saddlebag(ItemFinderModule* finder, UIState* uiState) =>
        SaddlebagNote.Build(
            isCached: IsSaddlebagCached(finder),
            playerStateLoaded: uiState is not null && uiState->PlayerState.IsLoaded,
            companionQuestComplete: uiState is not null
                && uiState->IsUnlockLinkUnlockedOrQuestCompleted(ChocoboCompanionQuestId));

    /// <summary>True when the item finder remembers at least one retainer's contents.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>Reads only the count, never the map's keys, which are retainer ids.</remarks>
    public static bool HasRememberedRetainers(ItemFinderModule* finder) =>
        finder is not null && RetainerNote.HasRemembered(finder->RetainerInventories.Count);

    /// <summary>
    /// The retainers' note: cached once any retainer's contents are remembered, with how many are
    /// remembered and, when the game knows it, how many the character has.
    /// </summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// The rules are <see cref="RetainerNote.Build"/>'s. Both reads are counts, so neither touches a
    /// retainer id or name. With no item finder the source is unscanned and no total is given.
    /// </remarks>
    public static ItemSourceStatus Retainers(ItemFinderModule* finder)
    {
        if (finder is null)
            return new ItemSourceStatus { State = SourceStates.Unscanned };

        var retainerManager = RetainerManager.Instance();
        var managerTotal = retainerManager is not null ? (int)retainerManager->GetRetainerCount() : 0;

        return RetainerNote.Build(
            rememberedCount: finder->RetainerInventories.Count,
            managerTotal: managerTotal);
    }
}
