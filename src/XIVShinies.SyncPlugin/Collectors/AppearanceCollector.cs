using Dalamud.Plugin.Services;
// Control, the game's own object that knows which character is the local player.
using FFXIVClientStructs.FFXIV.Client.Game.Control;
// AppearanceSnapshot and AppearanceToggles, the tested builder this collector hands its reads to.
using XIVShinies.SyncPlugin.Appearance;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports how the local character looks: the choices made in the character creator, the display
/// toggles for weapon, headgear, visor and Viera ears, the glasses worn, and where the Free Company
/// crest is shown.
/// </summary>
/// <remarks>
/// <para>
/// <b>The local player only.</b> The character is reached through the game's <c>Control</c>
/// object, which answers for the local player alone. The object table, where every character
/// nearby lives, is never touched: Dalamud's rule against collecting other players' data is
/// enforced with a ban, and reading only the one character that rule allows is the simplest way
/// to keep to it.
/// </para>
/// <para>
/// <b>The game object's copy.</b> A character in the game is two things: the game object, which
/// holds what the character is (including the appearance chosen in the character creator), and the
/// draw object, the model the renderer builds from it. The values read here are the game object's
/// own copy. A tool that only restyles the drawn model leaves this copy alone, but one that writes a
/// character's appearance into this memory changes it too, and a pass that runs meanwhile reads the
/// edited look. Nothing here can tell an edited look from the player's own: the bytes are reported
/// as the game holds them.
/// </para>
/// <para>
/// <b>Skipped while transformed.</b> A transformation (being turned into a toad, for one) swaps
/// the character's model for another one, and the bytes read during it describe the
/// transformation rather than the character. The pass is skipped instead
/// (<see cref="CollectSkipReasons.Transformed"/>), so the server keeps the last appearance read
/// while the character was not transformed.
/// </para>
/// <para>
/// <b>Reads, then copies, then hands over.</b> Reads game memory through FFXIVClientStructs, so it
/// must run on the framework thread and is verified by in-game QA rather than by unit tests. It
/// copies every value out of the game before handing it on, so nothing pointing into game memory
/// outlives the call. The rules live in pure, tested classes: <see cref="AppearanceSnapshot"/>
/// assembles the facts and rejects a read of the wrong size, and <see cref="CustomizeFields"/>
/// names each customization byte.
/// </para>
/// </remarks>
// `unsafe` allows raw pointers. FFXIVClientStructs maps the game's own memory layout, so its methods
// hand back pointers into the live game rather than managed objects. C# normally forbids this; the
// keyword is the explicit opt-in. `T*` is "a pointer to a T", `->` reads a member through a pointer
// (the pointer version of `.`), and `&` takes the address of something, giving a pointer to it.
// There is no JS equivalent whatsoever.
public sealed unsafe class AppearanceCollector : ICollector
{
    private readonly IFramework framework;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy. Passed in from the registry rather than
    /// hardcoded here, so that every category is described in exactly one file.
    /// </param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public AppearanceCollector(CategoryInfo info, IFramework framework)
    {
        this.info = info;
        this.framework = framework;
    }

    /// <inheritdoc/>
    public string CategoryKey => info.Key;

    /// <inheritdoc/>
    public string DisplayName => info.DisplayName;

    /// <inheritdoc/>
    public string Section => info.Section;

    /// <inheritdoc/>
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public string? Details => info.Details;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    /// <inheritdoc/>
    public bool RequiresServerSupport => info.RequiresServerSupport;

    /// <inheritdoc/>
    public bool IsSingleRecord => info.IsSingleRecord;

    /// <inheritdoc/>
    public bool ReadsStorage => info.ReadsStorage;

    // This collector needs nothing from the context: the character's appearance is read whole, with
    // no server manifest narrowing the scope.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        // Everything below dereferences raw game memory. Reading it off the framework thread races
        // the game's own writes, and the resulting access violation cannot be caught — so refuse.
        GameThread.EnsureFrameworkThread(framework, nameof(AppearanceCollector));

        // The local player's character, or null when no character is loaded: at the title screen,
        // or between logging out and the next login. A static method, so it is called on the type
        // itself, like a static method on a TypeScript class.
        var player = Control.GetLocalPlayer();
        if (player is null)
            return CollectResult.Skipped(CollectSkipReasons.LocalPlayerUnavailable);

        // FFXIVClientStructs mirrors the game's C++ inheritance by placing the parent struct inside
        // the child as a field, so the player's battle character holds its Character in place.
        // `player->Character` names that field through the pointer, and `&` in front takes its
        // address: the result is a pointer to the Character where it already sits in game memory,
        // rather than a copy of the whole struct.
        var character = &player->Character;

        // A non-zero model id means the character is drawn as something else, which is what a
        // transformation is, so the pass is skipped (see the class remarks).
        if (character->ModelContainer.ModelCharaId != 0)
            return CollectResult.Skipped(CollectSkipReasons.Transformed);

        // The draw data holds the appearance values read below. A pointer again, for the same
        // reason: it is read where it sits rather than copied whole.
        var drawData = &character->DrawData;

        // Both arrays are copied out with ToArray(). `Data` and `GlassesIds` are spans, windows onto
        // the game's own memory; the copies are ordinary arrays the game cannot change or free, so
        // nothing handed on from here points into game memory.
        var customize = drawData->CustomizeData.Data.ToArray();
        var glasses = drawData->GlassesIds.ToArray();

        // One bit per gear slot that shows the free company crest. AppearanceSnapshot decides which
        // bit means which slot.
        var crestBits = drawData->FreeCompanyCrestBitfield;

        // Named arguments (`WeaponHidden: ...`) say which parameter each value fills, so the four
        // similar-looking booleans cannot be passed in the wrong order unnoticed.
        var facts = AppearanceSnapshot.Build(
            customize,
            new AppearanceToggles(
                WeaponHidden: drawData->IsWeaponHidden,
                HatHidden: drawData->IsHatHidden,
                VisorToggled: drawData->IsVisorToggled,
                VieraEarsHidden: drawData->VieraEarsHidden),
            crestBits,
            glasses);

        // Null means the customization read was not the size the byte table expects, so which byte
        // means what is unknown and nothing from the read can be trusted.
        if (facts is null)
            return CollectResult.Skipped(CollectSkipReasons.UnexpectedLayout);

        return CollectResult.Appearance(facts);
    }
}
