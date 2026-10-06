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
/// <para>
/// Several collections read the same physical storage, and the runner merges every collector's
/// notes into one set for the upload, keeping one note per source. If two collectors worked out a
/// source's state separately they could disagree, and the one note that reached the wire could
/// describe a read the other collector's facts never came from. So every collector that reads one
/// of these sources asks the same two questions here: an <c>Is…</c>/<c>Has…</c> predicate gates
/// its read, and the matching note method, which is built on that very predicate, describes it.
/// A gate and its note cannot disagree, and neither can two collectors; <see cref="Saddlebag"/>
/// describes the one note that can say "read" without its gate opening.
/// </para>
/// <para>
/// Each method reads the game's flag at the moment it is called. A collector calls the gate and the
/// note in the same pass, inside one framework update, where the game's own code cannot run in
/// between, so both see the same value.
/// </para>
/// <para>
/// Reads game memory, so it runs on the framework thread like the collectors that call it, and is
/// verified in game rather than by unit tests; the two notes with rules beyond a single flag hand
/// their decisions to the tested <see cref="RetainerNote"/> and <see cref="SaddlebagNote"/>. Only
/// the local player's own storage and quest progress are consulted.
/// </para>
/// </remarks>
// `unsafe` allows raw pointers, which FFXIVClientStructs uses to hand back the game's own memory.
// C# normally forbids them; the keyword is the explicit opt-in. `T*` is "a pointer to a T", and
// `->` reads a member through a pointer, the pointer version of `.`. There is no JS equivalent.
internal static unsafe class StorageSources
{
    // The quest "My Feisty Little Chocobo", whose completion unlocks the chocobo companion and its
    // saddlebag. The game's unlock-or-quest check takes quests by this id, which is the Quest sheet
    // row id.
    private const uint ChocoboCompanionQuestId = 66698;

    /// <summary>The live containers: bags, equipped gear and the armory chest.</summary>
    /// <remarks>
    /// Always <see cref="SourceStates.Live"/>: these containers are read straight from current game
    /// memory, with no flag to consult. A collector reports this only once it has walked them this
    /// pass, so the note is a statement that the walk happened.
    /// </remarks>
    public static ItemSourceStatus Inventory() => new() { State = SourceStates.Live };

    /// <summary>True when the game has the Armoire's contents loaded right now.</summary>
    /// <param name="uiState">The game's UI state, or null when it is not available.</param>
    /// <remarks>
    /// The Armoire is reached through <c>UIState</c>, independently of the item finder. The game
    /// fetches its contents from the server the first time the player opens it each session, and
    /// re-requests them at times while the Armoire window is open; whenever the contents are not
    /// loaded, the game genuinely cannot answer and asking would return a confident "no".
    /// </remarks>
    public static bool IsArmoireLoaded(UIState* uiState) =>
        uiState is not null && uiState->Cabinet.IsCabinetLoaded();

    /// <summary>The Armoire's note: loaded, or unscanned.</summary>
    /// <param name="uiState">The game's UI state, or null when it is not available.</param>
    /// <remarks>
    /// <see cref="SourceStates.Loaded"/> when <see cref="IsArmoireLoaded"/> is true, otherwise
    /// <see cref="SourceStates.Unscanned"/>, the honest status for "not read".
    /// </remarks>
    // `cond ? a : b` is the same conditional expression as in TypeScript.
    public static ItemSourceStatus Armoire(UIState* uiState) => new()
    {
        State = IsArmoireLoaded(uiState) ? SourceStates.Loaded : SourceStates.Unscanned,
    };

    /// <summary>True when the item finder's copy of the glamour dresser was refreshed this session.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// The item finder keeps its own copy of the dresser and refreshes it each time the dresser
    /// window closes; <c>IsGlamourDresserCached</c> turns true once that has happened this session.
    /// At login the copy already holds ids loaded from disk while the flag is still false, so the
    /// flag, not whether the copy holds anything, decides whether it was read this session.
    /// </remarks>
    public static bool IsDresserCached(ItemFinderModule* finder) =>
        finder is not null && finder->IsGlamourDresserCached;

    /// <summary>The glamour dresser's note: cached, or unscanned.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// <see cref="SourceStates.Cached"/> when <see cref="IsDresserCached"/> is true, otherwise
    /// <see cref="SourceStates.Unscanned"/>, the honest status for "not read" rather than an empty
    /// read.
    /// </remarks>
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
    /// The same arrangement as <see cref="IsDresserCached"/>: the item finder's copy refreshes each
    /// time the saddlebag window closes, <c>IsSaddleBagCached</c> turns true once that has happened
    /// this session, and ids loaded from disk at login do not count as a read. Its ids use the item
    /// finder's high-quality encoding (see <see cref="GameItemId"/>).
    /// </remarks>
    public static bool IsSaddlebagCached(ItemFinderModule* finder) =>
        finder is not null && finder->IsSaddleBagCached;

    /// <summary>The saddlebag's note: cached, or unscanned.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <param name="uiState">The game's UI state, or null when it is not available.</param>
    /// <remarks>
    /// Reads three flags and hands them to <see cref="SaddlebagNote.Build"/>, which documents the
    /// rules: <see cref="IsSaddlebagCached"/>, whether the player's state has loaded, and the
    /// game's own unlock-or-quest check for the chocobo companion's quest. One case reports the
    /// source as read without its gate having opened: a character with no companion has no
    /// saddlebag, so there is nothing to read and the note says so, while the collectors, gated on
    /// <see cref="IsSaddlebagCached"/>, add nothing from it.
    /// </remarks>
    // Named arguments (`isCached: ...`) say which parameter each flag fills, so the three booleans
    // cannot be swapped unnoticed. With no UI state the player's state counts as not loaded, which
    // makes the quest answer irrelevant.
    public static ItemSourceStatus Saddlebag(ItemFinderModule* finder, UIState* uiState) =>
        SaddlebagNote.Build(
            isCached: IsSaddlebagCached(finder),
            playerStateLoaded: uiState is not null && uiState->PlayerState.IsLoaded,
            companionQuestComplete: uiState is not null
                && uiState->IsUnlockLinkUnlockedOrQuestCompleted(ChocoboCompanionQuestId));

    /// <summary>True when the item finder remembers at least one retainer's contents.</summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// <c>RetainerInventories.Count</c> is the number of retainers whose contents the item finder
    /// remembers. Reading it does not touch the map's keys, which are retainer ids and are never
    /// read. The rule applied to the count is <see cref="RetainerNote.HasRemembered"/>, the same one
    /// the note uses.
    /// </remarks>
    public static bool HasRememberedRetainers(ItemFinderModule* finder) =>
        finder is not null && RetainerNote.HasRemembered(finder->RetainerInventories.Count);

    /// <summary>
    /// The retainers' note: cached once any retainer's contents are remembered, with how many are
    /// remembered and, when the game knows it, how many the character has.
    /// </summary>
    /// <param name="finder">The game's item finder, or null when it is not available.</param>
    /// <remarks>
    /// <para>
    /// This reads the two counts out of the game and hands them to <see cref="RetainerNote.Build"/>,
    /// which decides the note and documents its rules. The remembered count is the one
    /// <see cref="HasRememberedRetainers"/> gates a read on. The total comes from
    /// <c>RetainerManager.GetRetainerCount</c>, a count only — the manager's per-retainer entries
    /// (ids, names) are never read.
    /// </para>
    /// <para>
    /// With no item finder at all nothing can be read, so the source is unscanned and no total is
    /// given.
    /// </para>
    /// </remarks>
    public static ItemSourceStatus Retainers(ItemFinderModule* finder)
    {
        if (finder is null)
            return new ItemSourceStatus { State = SourceStates.Unscanned };

        var retainerManager = RetainerManager.Instance();
        var managerTotal = retainerManager is not null ? (int)retainerManager->GetRetainerCount() : 0;

        // Named arguments (`rememberedCount: ...`) say which parameter each count fills, so the two
        // integers cannot be swapped unnoticed.
        return RetainerNote.Build(
            rememberedCount: finder->RetainerInventories.Count,
            managerTotal: managerTotal);
    }
}
