using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Collects the IDs of every row in a game sheet the local player has unlocked or completed, for any
/// sheet <c>IUnlockState</c> answers for, such as quests, mounts, minions, achievements, orchestrion
/// rolls and Triple Triad cards.
/// </summary>
/// <typeparam name="TRow">The sheet's row type, such as <c>Quest</c> or <c>Mount</c>.</typeparam>
/// <remarks>
/// Reads only the local player's unlock state, never the object table (a Dalamud rule).
/// <see cref="CollectorGate"/> holds the gating rule, <see cref="CollectorRunner"/> applies it and
/// handles skips, and <see cref="Sync.SyncPayloadBuilder"/> assembles the request body.
/// </remarks>
public sealed class ExcelUnlockCollector<TRow> : ICollector, IUnlockAware
    where TRow : struct, IExcelRow<TRow>
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    // Lumina's row interface does not expose RowId, so the caller supplies a reader.
    private readonly Func<TRow, uint> rowId;

    private readonly Func<TRow, bool> isUnlocked;

    private readonly Func<string?>? precondition;

    private readonly CategoryInfo info;

    /// <summary>Creates a collector for one sheet.</summary>
    /// <param name="info">The category's wire key and its user-facing copy.</param>
    /// <param name="dataManager">Dalamud's game data accessor.</param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    /// <param name="rowId">Reads a row's ID (usually <c>row =&gt; row.RowId</c>).</param>
    /// <param name="isUnlocked">True when the local player has unlocked or completed this row.</param>
    /// <param name="precondition">
    /// Returns a skip reason while the source is unreadable, such as an achievements list never
    /// requested from the server, or null. A skip omits the category, which the server reads as "not
    /// read this time".
    /// </param>
    public ExcelUnlockCollector(
        CategoryInfo info,
        IDataManager dataManager,
        IFramework framework,
        Func<TRow, uint> rowId,
        Func<TRow, bool> isUnlocked,
        Func<string?>? precondition = null)
    {
        this.info = info;
        this.dataManager = dataManager;
        this.framework = framework;
        this.rowId = rowId;
        this.isUnlocked = isUnlocked;
        this.precondition = precondition;
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
    // From the category: not every sheet this class reads is announced by the game.
    public bool UploadsOnUnlock => info.UploadsOnUnlock;

    /// <inheritdoc/>
    // Each collector recognizes only its own sheet, so routing an unlock needs no category branch.
    public bool Handles(RowRef rowRef) => rowRef.Is<TRow>();

    /// <inheritdoc/>
    // Ignores the context: no server manifest scopes a sheet.
    public CollectResult Collect(CollectContext context)
    {
        GameThread.EnsureFrameworkThread(framework, nameof(ExcelUnlockCollector<TRow>));

        var skipReason = precondition?.Invoke();
        if (skipReason is not null)
            return CollectResult.Skipped(skipReason);

        // The fetch alone: a throw from the loop is a different failure and must not be relabeled.
        // A catch, not a null check: see CollectSkipReasons.SheetUnavailable.
        ExcelSheet<TRow> sheet;
        try
        {
            sheet = dataManager.GetExcelSheet<TRow>();
        }
        catch (Exception)
        {
            return CollectResult.Skipped(CollectSkipReasons.SheetUnavailable);
        }

        var ids = new List<uint>();
        foreach (var row in sheet)
        {
            if (isUnlocked(row))
                ids.Add(rowId(row));
        }

        // The completeness declaration is the category's (see CategoryInfo.EnumeratesCompleteDomain);
        // CollectResult.Ids applies its own floor to it.
        return CollectResult.Ids(ids, info.EnumeratesCompleteDomain);
    }
}
