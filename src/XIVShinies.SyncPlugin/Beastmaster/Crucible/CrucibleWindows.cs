using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Beastmaster.Crucible;

/// <summary>
/// Makes a snapshot of one window reading, at a given moment and closed or not. The same reading makes
/// an open snapshot when its window is read and a closed one when the window closes.
/// </summary>
/// <param name="at">The moment the snapshot stands for.</param>
/// <param name="closed">Whether it is the window's closing snapshot.</param>
// A `delegate` declares the shape of a function value, like the TypeScript function type
// `(at: Date, closed: boolean) => CrucibleObservation`.
public delegate CrucibleObservation CrucibleSnapshot(DateTimeOffset at, bool closed);

/// <summary>
/// The Crucible windows the sharing reads: their internal names, which events carry their values, and
/// the reader each one's values go to.
/// </summary>
/// <remarks>
/// The results screen's Degree, rank and bonus names go up as the ids <see cref="CrucibleNames"/>
/// resolves them to, or as null when a name does not resolve; the drawn text is never sent.
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
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
    // `IReadOnlyList<T>` is a list its holder cannot change, like `readonly T[]`; `[a, b]` builds it.
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
    // `new() { [key] = value, ... }` builds the dictionary with these entries, like an object literal.
    // `(values, names) => ...` is a lambda, like an arrow function: each one reads the values and, when
    // its reader recognized them, hands back a second lambda that builds a snapshot from that reading.
    // Only the results screen's reader uses `names`; the others take it so every reader has one
    // shape. `_`, as in `(at, _)`, stands for a parameter a lambda does not use.
    // `x is { } reading` matches when x is not null and names it `reading`.
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
        // `names.BonusId` is passed as a function value, not called: the builder calls it once per
        // bonus.
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
    /// <param name="values">Every value the window was handed, in order.</param>
    /// <param name="names">Resolves the results screen's drawn names to ids.</param>
    public static CrucibleSnapshot? Read(
        string windowName, IReadOnlyList<AddonValue> values, CrucibleNames names) =>
        // `TryGetValue` returns whether the key is there and, when it is, writes its value into
        // `reader`.
        Readers.TryGetValue(windowName, out var reader) ? reader(values, names) : null;

    /// <summary>True for a window this sharing reads.</summary>
    /// <param name="windowName">The window's internal name.</param>
    public static bool IsRead(string windowName) => Readers.ContainsKey(windowName);

    /// <summary>True for a window that sends a closing snapshot.</summary>
    /// <param name="windowName">The window's internal name.</param>
    public static bool Closes(string windowName)
    {
        // A plain loop rather than `Contains`, which an `IReadOnlyList` does not offer by itself.
        foreach (var closing in Closing)
        {
            if (closing == windowName)
                return true;
        }

        return false;
    }
}
