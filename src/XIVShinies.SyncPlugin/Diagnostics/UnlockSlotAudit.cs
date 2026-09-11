// The whole file is a development-build diagnostic: a Release compile contains none of it, so its
// usings are guarded alongside it.
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
/// A collection may only declare itself complete when the game answers for every row the server's
/// catalog may hold. This is how that claim is checked rather than assumed — see the mount and
/// minion id-space bullet in <c>docs/api-contract.md</c> for what the answer means on the wire.
/// Reached from <c>/shinies dumpslots</c>; the answer goes to the plugin log, not the screen.
/// </remarks>
internal static class UnlockSlotAudit
{
    /// <summary>
    /// Reports how much of the <c>Mount</c> sheet the unlock bitmask can answer for, and names
    /// every row it cannot.
    /// </summary>
    /// <remarks>
    /// Evidence for whether mounts may honestly declare completeness — see the mount and minion
    /// id-space bullet in <c>docs/api-contract.md</c> for what the answer means on the wire.
    /// </remarks>
    internal static void Run(IClientState clientState, IDataManager dataManager, IUnlockState unlockState, IPluginLog log)
    {
        // Without a character the unlock check answers false for every row rather than throwing,
        // and the sheets read fine — so a title-screen run completes and prints the sentence that
        // reads as confirmation. Refusing is the only way a reader can tell a run that measured
        // everything from one that measured nothing.
        if (!clientState.IsLoggedIn)
        {
            log.Information(
                "dumpslots: not logged in. Unlock state answers false for everything, so nothing " +
                "here would mean anything. Log in and run it again.");
            return;
        }

        // Mounts only — the minion bitmask has no slotless case. `Mount.Order` is the bit index a
        // negative value marks the absence of; see the mount and minion id-space bullet in
        // docs/api-contract.md for both mechanisms.
        Report<Mount>(
            dataManager, log,
            "Mount", row => row.Order < 0, row => row.Singular.ExtractText(),
            unlockState.IsMountUnlocked);

        // A local function — a method declared inside another method, visible only there. `static`
        // on it means it captures nothing from the enclosing scope, so the compiler can rule out an
        // accidental capture rather than leaving it to a reader to check. That is why the services
        // it needs are handed to it rather than reached for.
        static void Report<T>(
            IDataManager dataManager,
            IPluginLog log,
            string label, Func<T, bool> isSlotless, Func<T, string> name, Func<T, bool> isUnlocked)
            where T : struct, IExcelRow<T>
        {
            // GetExcelSheet throws rather than answering null when a sheet is missing or its
            // columns have moved, which for a diagnostic is the right shape: Dalamud's command
            // dispatch catches and logs it, and a half-answer would be worse than none.
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

                // A slotless row that answers "unlocked" would disprove the reading of this dump
                // outright. The converse proves less than it looks: rows nobody owns answer false
                // whatever field indexes the bitmask, so a zero here is consistent with the slot
                // field being the index AND with it not being. Only the game's own collection
                // window settles whether a named row is obtainable.
                if (isUnlocked(row))
                    slotlessUnlocked++;
            }

            log.Information(
                $"{label}: {total} rows, {named} named, {slotless} slotless, " +
                $"{slotlessNamed.Count} slotless AND named.");

            // The "nothing matched" case gets its own sentence rather than falling into the
            // reassuring one below. A predicate that cannot match its column produces the same
            // zero as a sheet with genuinely no slotless rows, and the reassuring wording would
            // let the first read as the second — which is how an unexamined sheet gets recorded
            // as evidence.
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
