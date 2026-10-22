using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the IDs of every Triple Triad NPC the local player has defeated at least once.
/// </summary>
/// <remarks>
/// <c>IUnlockState</c> covers cards but not opponents, so this reads the game's own beaten bit array
/// through <c>UIState</c>, for the local player only and never the object table (a Dalamud rule). It
/// never claims a complete enumeration: the rows the game does not track include real opponents that
/// XIV Shinies lists, such as Lewena at the Gold Saucer, who drops cards but has no beaten flag
/// because she counts toward no Triple Triad achievement. A complete claim would turn that gap into
/// evidence against the player's own manual mark.
/// </remarks>
// Unsafe because FFXIVClientStructs' Instance() methods return pointers into live game memory.
public sealed unsafe class TripleTriadNpcCollector : ICollector
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy, from the registry, which describes every
    /// category in one file.
    /// </param>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public TripleTriadNpcCollector(CategoryInfo info, IDataManager dataManager, IFramework framework)
    {
        this.info = info;
        this.dataManager = dataManager;
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
    // Ignores the context: every tracked opponent in the sheet is a candidate.
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(TripleTriadNpcCollector));

        // A catch, not a null check: see CollectSkipReasons.SheetUnavailable.
        ExcelSheet<TripleTriadResident> sheet;
        try
        {
            sheet = dataManager.GetExcelSheet<TripleTriadResident>();
        }
        catch (Exception)
        {
            return CollectResult.Skipped(CollectSkipReasons.SheetUnavailable);
        }

        var uiState = UIState.Instance();
        if (uiState is null)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        var ids = new List<uint>();
        foreach (var row in sheet)
        {
            // Order 65535 marks a row the game does not track, dummy rows included. It has no slot in
            // the beaten bit array, so asking about it can never produce a fact.
            if (row.Order == ushort.MaxValue)
                continue;

            // The sheet's own id, in the event-handler range (2293762 and up). The game maps it onto
            // its bit array, so it must not be rebased; the server stores it as a TripleTriad row id.
            if (uiState->IsTripleTriadNpcBeaten(row.RowId))
                ids.Add(row.RowId);
        }

        // The category declines completeness (see the class remarks); an empty list is still a read.
        return CollectResult.Ids(ids, info.EnumeratesCompleteDomain);
    }
}
