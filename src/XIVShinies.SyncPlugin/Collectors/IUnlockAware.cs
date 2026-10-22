using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Implemented by collectors that recognize the game's real-time <c>Unlock</c> event as their own, so
/// an unlock uploads only the one affected category.
/// </summary>
/// <remarks>
/// Kept off <see cref="ICollector"/> so that interface and the runner stay free of <c>Lumina.dll</c>,
/// which does not load outside the game. An <c>unlock</c> upload dates every category it carries to
/// that moment and no later upload revises a date, so sweeping everything on an unlock would
/// permanently misdate rows first uploaded alongside it; quests suffer most, since snapshot uploads
/// leave their date null. A missed unlock only waits for the next full sweep.
/// </remarks>
internal interface IUnlockAware
{
    /// <summary>True when the unlocked row belongs to this collector's category.</summary>
    /// <remarks>
    /// An untyped <see cref="RowRef"/> belongs to no collector, so that unlock is not uploaded early.
    /// </remarks>
    bool Handles(RowRef rowRef);
}
