using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Makes a snapshot of one window reading, at a given moment and closed or not. A reading makes an
/// open snapshot when its window is read, and a closed one when the window closes.
/// </summary>
/// <param name="at">The moment the snapshot stands for.</param>
/// <param name="closed">Whether it is the window's closing snapshot.</param>
public delegate CrucibleObservation CrucibleSnapshot(DateTimeOffset at, bool closed);

/// <summary>
/// The Crucible windows the sharing reads: their internal names, which events carry their values, and
/// the reader each one's values go to.
/// </summary>
/// <remarks>
/// The results screen's Degree, rank and bonus names go up as the ids <see cref="CrucibleNames"/>
/// resolves them to, or as null when a name does not resolve; the drawn text is never sent.
/// </remarks>
public static class CrucibleWindows
{
    /// <summary>The board window's internal name.</summary>
    public const string Board = "XBMStageDetailList";

    /// <summary>The team window's internal name.</summary>
    public const string Team = "XBMPetParty";

    /// <summary>The run HUD's internal name: the token balance and the bag.</summary>
    public const string Hud = "XBMContentsMainHUD";

    /// <summary>The treasure coffer's internal name.</summary>
    public const string Treasure = "XBMContentsTreasure";

    /// <summary>The fight's loot window's internal name.</summary>
    public const string Loot = "XBMContentsBooty";

    /// <summary>The shop's internal name.</summary>
    public const string Shop = "XBMContentsItemShop";

    /// <summary>The results screen's internal name.</summary>
    public const string Results = "XBMResult";

    /// <summary>The windows whose values arrive when they are redrawn.</summary>
    public static readonly IReadOnlyList<string> Redrawn = [Board, Team, Hud, Treasure, Loot, Shop, Results];

    /// <summary>The windows whose values are already complete when they open.</summary>
    public static readonly IReadOnlyList<string> CompleteAtOpen = [Results];

    /// <summary>The windows whose last state is sent once more, marked closed, when they close.</summary>
    public static readonly IReadOnlyList<string> Closing = [Board, Team, Treasure, Loot, Shop];

    /// <summary>
    /// Reads one window's values, or returns null when they are not a layout its reader knows.
    /// </summary>
    private delegate CrucibleSnapshot? WindowReader(IReadOnlyList<AddonValue> values, CrucibleNames names);

    /// <summary>Each window's reader, by the window's internal name.</summary>
    // Only the results screen's reader uses `names`; the others take it so every reader has one shape.
    private static readonly Dictionary<string, WindowReader> Readers = new()
    {
        [Board] = (values, names) => CrucibleBoard.Read(values) is { } reading
            ? (at, closed) => CrucibleUploadBuilder.Board(reading, at, closed)
            : null,
        [Team] = (values, names) => CrucibleTeam.Read(values) is { } reading
            ? (at, closed) => CrucibleUploadBuilder.Team(reading, at, closed)
            : null,
        [Hud] = (values, names) => CrucibleHud.Read(values) is { } reading
            ? (at, _) => CrucibleUploadBuilder.Bag(reading, at)
            : null,
        [Treasure] = (values, names) => CrucibleOffers.ReadTreasure(values) is { } reading
            ? (at, closed) => CrucibleUploadBuilder.Offer(reading, at, closed)
            : null,
        [Loot] = (values, names) => CrucibleOffers.ReadLoot(values) is { } reading
            ? (at, closed) => CrucibleUploadBuilder.Offer(reading, at, closed)
            : null,
        [Shop] = (values, names) => CrucibleOffers.ReadShop(values) is { } reading
            ? (at, closed) => CrucibleUploadBuilder.Offer(reading, at, closed)
            : null,
        [Results] = (values, names) => CrucibleResults.Read(values) is { } reading
            ? (at, _) => CrucibleUploadBuilder.Results(
                reading,
                at,
                names.Degree(reading.ModeText),
                names.RankIndex(reading.RankText),
                names.BonusId)
            : null,
    };

    /// <summary>
    /// Reads a window's values through its reader, or returns null for a window this sharing does not
    /// read, or values that are not a layout its reader knows.
    /// </summary>
    /// <param name="windowName">The window's internal name.</param>
    /// <param name="names">Resolves the results screen's drawn names to ids.</param>
    public static CrucibleSnapshot? Read(
        string windowName, IReadOnlyList<AddonValue> values, CrucibleNames names) =>
        Readers.TryGetValue(windowName, out var reader) ? reader(values, names) : null;

    /// <summary>True for a window this sharing reads.</summary>
    public static bool IsRead(string windowName) => Readers.ContainsKey(windowName);

    /// <summary>True for a window that sends a closing snapshot.</summary>
    public static bool Closes(string windowName)
    {
        foreach (var closing in Closing)
        {
            if (closing == windowName)
                return true;
        }

        return false;
    }
}
