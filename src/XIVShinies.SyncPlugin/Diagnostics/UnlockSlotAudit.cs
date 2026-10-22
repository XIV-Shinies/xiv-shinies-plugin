// Debug builds only. The usings sit inside the guard, so a Release compile contains none of this file.
#if DEBUG
using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace XIVShinies.SyncPlugin.Diagnostics;

/// <summary>
/// Audits how much of an unlockable sheet the game's unlock bitmask can actually answer for.
/// </summary>
/// <remarks>
/// A collection may declare itself complete only when the game answers for every row the server's
/// catalog may hold; this checks that claim rather than assuming it. The mount and minion id-space
/// bullet in <c>docs/api-contract.md</c> says what the answer means on the wire. Reached from
/// <c>/shinies dumpslots</c>; the answer goes to the plugin log, not the screen.
/// </remarks>
internal static class UnlockSlotAudit
{
    /// <summary>
    /// Reports how much of the <c>Mount</c> sheet the unlock bitmask can answer for, and names
    /// every row it cannot.
    /// </summary>
    internal static void Run(IClientState clientState, IDataManager dataManager, IUnlockState unlockState, IPluginLog log)
    {
        // Logged out, the unlock check answers false for every row without throwing, so a run would
        // print a result that reads as confirmation but measured nothing.
        if (!clientState.IsLoggedIn)
        {
            log.Information(
                "dumpslots: not logged in. Unlock state answers false for everything, so nothing " +
                "here would mean anything. Log in and run it again.");
            return;
        }

        // Mounts only: the minion bitmask has no slotless case. A negative `Mount.Order` marks a row
        // with no bit index; docs/api-contract.md describes both mechanisms.
        Report<Mount>(
            dataManager, log,
            "Mount", row => row.Order < 0, row => row.Singular.ExtractText(),
            unlockState.IsMountUnlocked);

        static void Report<T>(
            IDataManager dataManager,
            IPluginLog log,
            string label, Func<T, bool> isSlotless, Func<T, string> name, Func<T, bool> isUnlocked)
            where T : struct, IExcelRow<T>
        {
            // Throws for a missing sheet or moved columns. Dalamud's command dispatch logs it, and a
            // half-answer would be worse than none.
            var sheet = dataManager.GetExcelSheet<T>();

            var total = 0;
            var named = 0;
            var slotless = 0;
            var slotlessUnlocked = 0;
            var slotlessNamed = new List<string>();

            foreach (var row in sheet)
            {
                total++;

                var rowName = name(row);
                var hasName = !string.IsNullOrWhiteSpace(rowName);
                if (hasName)
                    named++;

                if (!isSlotless(row))
                    continue;

                slotless++;
                if (hasName)
                    slotlessNamed.Add(rowName);

                // A slotless row that answers "unlocked" disproves this dump's reading outright. A zero
                // proves less: unowned rows answer false whatever field indexes the bitmask, so only the
                // game's own collection window settles whether a named row is obtainable.
                if (isUnlocked(row))
                    slotlessUnlocked++;
            }

            log.Information(
                $"{label}: {total} rows, {named} named, {slotless} slotless, " +
                $"{slotlessNamed.Count} slotless AND named.");

            // A predicate that cannot match its column gives the same zero as a sheet with no slotless
            // rows, so this case gets its own sentence rather than the reassuring one below.
            if (slotless == 0)
            {
                log.Information(
                    $"{label}: no row matched the slotless test, so nothing was examined. Either " +
                    "the sheet has no such rows or the predicate cannot match this column.");
                return;
            }

            log.Information(
                slotlessUnlocked == 0
                    ? $"{label}: no slotless row answers unlocked. Consistent with them being " +
                      "unreportable; check each name below against the game's collection window."
                    : $"{label}: {slotlessUnlocked} slotless rows answer UNLOCKED, so the slot field " +
                      "is not what the unlock check indexes by and this dump proves nothing.");

            if (slotlessNamed.Count > 0)
                log.Information($"{label} slotless but named: {string.Join(" | ", slotlessNamed)}");
        }
    }
}
#endif
