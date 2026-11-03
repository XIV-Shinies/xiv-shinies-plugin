using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XIVShinies.SyncPlugin.Appearance;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports how the local character looks: the choices made in the character creator, the display
/// toggles for weapon, headgear, visor and Viera ears, the glasses worn, and where the Free Company
/// crest is shown.
/// </summary>
/// <remarks>
/// Reads the local player alone, through the game's <c>Control</c> object, never the object table
/// (a ban-enforced Dalamud rule). The bytes are the game object's copy, which a tool restyling only
/// the drawn model leaves alone, but a look a tool writes into this memory is reported as the
/// player's own. A transformation swaps the model, so that pass is skipped and the server keeps the
/// last untransformed look.
/// </remarks>
// Unsafe because FFXIVClientStructs returns pointers into live game memory.
public sealed unsafe class AppearanceCollector : ICollector
{
    private readonly IFramework framework;

    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy, from the registry, which describes every
    /// category in one file.
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

    // Ignores the context: no server manifest scopes this collection.
    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(AppearanceCollector));

        // Null at the title screen and between logout and the next login.
        var player = Control.GetLocalPlayer();
        if (player is null)
            return CollectResult.Skipped(CollectSkipReasons.LocalPlayerUnavailable);

        var character = &player->Character;

        // A non-zero model id means the character is transformed (see the class remarks).
        if (character->ModelContainer.ModelCharaId != 0)
            return CollectResult.Skipped(CollectSkipReasons.Transformed);

        var drawData = &character->DrawData;

        // Copied out, so nothing handed on points into game memory.
        var customize = drawData->CustomizeData.Data.ToArray();
        var glasses = drawData->GlassesIds.ToArray();

        var crestBits = drawData->FreeCompanyCrestBitfield;

        var facts = AppearanceSnapshot.Build(
            customize,
            new AppearanceToggles(
                WeaponHidden: drawData->IsWeaponHidden,
                HatHidden: drawData->IsHatHidden,
                VisorToggled: drawData->IsVisorToggled,
                VieraEarsHidden: drawData->VieraEarsHidden),
            crestBits,
            glasses);

        if (facts is null)
            return CollectResult.Skipped(CollectSkipReasons.UnexpectedLayout);

        return CollectResult.Appearance(facts);
    }
}
