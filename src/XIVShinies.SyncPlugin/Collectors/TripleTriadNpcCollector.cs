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
/// <para>
/// This is not an <see cref="ExcelUnlockCollector{TRow}"/> because Dalamud's <c>IUnlockState</c>
/// has no "NPC beaten" method — cards are covered there, opponents are not. So this collector asks
/// the game directly, through FFXIVClientStructs' <c>UIState</c>, which exposes the same beaten
/// bit array the game itself consults.
/// </para>
/// <para>
/// The IDs sent are <c>TripleTriadResident</c> sheet row ids, passed to the game <b>exactly as the
/// sheet reports them</b> (they live in the game's event-handler id range, so they look large —
/// 2293762 and up). The game maps them onto its bit array internally; rebasing them here would
/// break that lookup. The server stores them as <c>TripleTriad</c> row ids, which share the same
/// key space.
/// </para>
/// <para>
/// Rows whose <c>Order</c> is 65535 are ones the game does not track — they have no slot in the
/// beaten bit array, so asking about them can never produce a fact. They are skipped, which also
/// excludes the sheet's dummy rows.
/// </para>
/// <para>
/// Those skipped rows are why this collector never claims a complete enumeration (see
/// <see cref="CollectResult.CompleteEnumeration"/>). "Untracked" does not mean "not a real
/// opponent": Lewena at the Gold Saucer is challengeable and drops cards, and the game still gives
/// her no beaten flag because she counts toward no Triple Triad achievement. XIV Shinies lists such
/// opponents, so a player can legitimately hold one this collector is structurally unable to
/// report — and declaring the list complete would turn that gap into evidence against their own
/// manual mark. Reporting what was found, and claiming nothing beyond it, is the honest answer.
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and cannot
/// be unit-tested; it is verified by in-game QA. Reads only the <b>local</b> player's battle
/// results — never the object table or any other character, which is a hard Dalamud rule.
/// </para>
/// </remarks>
// `unsafe` allows raw pointers. FFXIVClientStructs maps the game's own memory layout, so its
// Instance() methods hand back pointers into the live game rather than managed objects. C# normally
// forbids this; the keyword is the explicit opt-in. There is no JS equivalent whatsoever.
public sealed unsafe class TripleTriadNpcCollector : ICollector
{
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy. Passed in from the registry rather than
    /// hardcoded here, so that every category is described in exactly one file.
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
    // This collector needs nothing from the context; every tracked opponent in the sheet is a
    // candidate, with no server manifest narrowing the scope.
    public CollectResult Collect(CollectContext context)
    {
        // Reading game memory off the framework thread races the game's own writes, and the
        // resulting access violation cannot be caught — so refuse.
        GameThread.EnsureFrameworkThread(framework, nameof(TripleTriadNpcCollector));

        // See CollectSkipReasons.SheetUnavailable for why this is a catch rather than a null check.
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
            // Untracked placeholder — no slot in the beaten bit array (see the class remarks).
            if (row.Order == ushort.MaxValue)
                continue;

            // `->` dereferences a pointer to reach a member, the pointer version of `.` — one of
            // the constructs the `unsafe` keyword on this class permits.
            if (uiState->IsTripleTriadNpcBeaten(row.RowId))
                ids.Add(row.RowId);
        }

        // An empty list is a legitimate result ("we read the tracked opponents; none is beaten"),
        // and is deliberately different from a skip. No completeness claim: the untracked rows the
        // loop skipped include opponents a player can genuinely have beaten (see the class
        // remarks), so this list speaks only for what it found.
        return CollectResult.Ids(ids);
    }
}
