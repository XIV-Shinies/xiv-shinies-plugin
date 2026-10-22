using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The upload log's table body: one row per logged upload, newest first — when, what triggered
/// each, the outcome, and what was sent (per category for a sync row).
/// </summary>
/// <remarks>
/// Drawn from <see cref="UploadLogEntry"/> data and the collectors' display names, with no
/// category-name branches. Its strings are built per frame, affordable only because the section
/// draws while expanded, never on an always-visible path. Wrapped calls pass a null edge, which in a
/// table is the cell's; a card's edge would let a cell run under its neighbor.
/// </remarks>
internal sealed class UploadLogTable
{
    // Category key to display name. The collector list is fixed for the plugin's lifetime.
    private readonly Dictionary<string, string> categoryNames = new();

    public UploadLogTable(IReadOnlyList<ICollector> collectors)
    {
        foreach (var collector in collectors)
            categoryNames[collector.CategoryKey] = collector.DisplayName;
    }

    /// <summary>Draws the table.</summary>
    /// <param name="history">The uploads to render, newest first.</param>
    /// <param name="innerRight">The enclosing card's inner right edge, where the table stops.</param>
    /// <param name="host">The configured website's address, which some outcomes name.</param>
    public void Draw(IReadOnlyList<UploadLogEntry> history, float innerRight, string host)
    {
        // When and Trigger stack by explicit lines when narrow, since ImGui wrapping would break
        // inside "7/11/2026". Seeded with the headers, which a narrower column would clip.
        var whenOneLine = ImGui.CalcTextSize("When").X;
        var whenStacked = whenOneLine;
        var triggerOneLine = ImGui.CalcTextSize("Trigger").X;
        var triggerStacked = triggerOneLine;

        // Outcome's widest word, the website's address included: a wider word would be left to
        // ImGui's own wrapping (see TextWrap).
        var outcomeWord = ImGui.CalcTextSize("Outcome").X;
        foreach (var entry in history)
        {
            // Short date and short time, in the user's locale.
            var at = entry.At.ToLocalTime();
            var date = at.ToString("d");
            var time = at.ToString("t");
            whenOneLine = Math.Max(whenOneLine, ImGui.CalcTextSize($"{date} {time}").X);
            whenStacked = Math.Max(
                whenStacked, Math.Max(ImGui.CalcTextSize(date).X, ImGui.CalcTextSize(time).X));

            var trigger = UploadLogText.TriggerText(entry);
            triggerOneLine = Math.Max(triggerOneLine, ImGui.CalcTextSize(trigger).X);
            foreach (var word in trigger.Split(' '))
                triggerStacked = Math.Max(triggerStacked, ImGui.CalcTextSize(word).X);

            foreach (var word in UploadLogText.OutcomeText(entry, host).Split(' '))
                outcomeWord = Math.Max(outcomeWord, ImGui.CalcTextSize(word).X);
        }

        // Compact when one-line metadata would leave too little room for Outcome and Sent.
        var tableWidth = innerRight - ImGui.GetCursorPosX();
        var compact =
            tableWidth < whenOneLine + triggerOneLine + (360f * ImGuiHelpers.GlobalScale);

        var whenWidth = (compact ? whenStacked : whenOneLine) + (2f * ImGuiHelpers.GlobalScale);
        var triggerWidth =
            (compact ? triggerStacked : triggerOneLine) + (2f * ImGuiHelpers.GlobalScale);

        // Roomier cells than ImGui's default.
        var padding = new Vector2(8f * ImGuiHelpers.GlobalScale, 6f * ImGuiHelpers.GlobalScale);

        // Outcome gets 0.8 of every 3 units left and Sent 2.2, since the counts are the dense part.
        // With no vertical borders, each gap between columns takes two paddings and the outer edges none.
        var stretchRoom = tableWidth - whenWidth - triggerWidth - (3 * 2f * padding.X);

        // Two scaled pixels over the widest word: one because BreakForWrap breaks a scaled pixel short
        // of the edge, and one because ImGui rounds a column's width down to a whole pixel.
        var outcomeWidth =
            Math.Max(outcomeWord + (2f * ImGuiHelpers.GlobalScale), stretchRoom * (0.8f / 3f));

        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, padding);

        // The explicit width stops the table at the card's inner edge rather than the window's.
        using var table = ImRaii.Table(
            "uploadLogTable",
            4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
            new Vector2(innerRight - ImGui.GetCursorPosX(), 0f));

        if (!table.Success)
            return;

        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, whenWidth);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthFixed, triggerWidth);
        ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthFixed, outcomeWidth);
        ImGui.TableSetupColumn("Sent", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        // The cells are data to read, so full contrast; only the "·" between categories is muted.
        var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var muted = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

        for (var index = 0; index < history.Count; index++)
        {
            var entry = history[index];

            // A category that moved since its last appearance lights up gold (see
            // UploadLogDiff.ChangedCategories).
            var changed = UploadLogDiff.ChangedCategories(history, index);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var at = entry.At.ToLocalTime();
            if (compact)
            {
                ImGui.TextUnformatted(at.ToString("d"));
                ImGui.TextUnformatted(at.ToString("t"));
            }
            else
            {
                ImGui.TextUnformatted($"{at.ToString("d")} {at.ToString("t")}");
            }

            ImGui.TableNextColumn();
            var triggerText = UploadLogText.TriggerText(entry);
            if (compact)
            {
                foreach (var word in triggerText.Split(' '))
                    ImGui.TextUnformatted(word);
            }
            else
            {
                ImGui.TextUnformatted(triggerText);
            }

            // Green when accepted, red for anything to look at, full contrast for a self-healing
            // deferral. A validation rejection's detail follows, so the row explains the failure.
            ImGui.TableNextColumn();
            var statusColor = UploadLogText.IsSuccess(entry.Status) ? Widgets.SuccessColor
                : UploadLogText.IsDeferral(entry.Status)
                    ? textColor
                    : Widgets.ErrorColor;
            Widgets.DrawWrapped(UploadLogText.OutcomeText(entry, host), statusColor, null);

            if (entry.Detail is { } detail)
                Widgets.DrawWrapped(detail, ImGuiCol.Text, null);

            // UploadLogText.SentSpans decides each category's text and highlights; this maps a
            // highlight to gold.
            ImGui.TableNextColumn();

            // A live tracker or Crucible sharing row sent one kind of thing and has no categories to
            // list.
            if (UploadLogText.LiveSentText(entry.Source) is { } liveSent)
            {
                Widgets.DrawWrapped(liveSent, ImGuiCol.Text, null);
                continue;
            }

            // The server proves per upload, not per category, so one note serves each manifest
            // category.
            var proof = UploadLogText.ProofText(entry);
            var stepsProven = entry.ProvenSteps > 0;
            var sent = new List<(string Text, Vector4? Color)>(entry.Categories.Count * 3);

            // By display name, like the consent list (see UploadLogText.InDisplayOrder).
            foreach (var category in UploadLogText.InDisplayOrder(entry.Categories, DisplayNameFor))
            {
                if (sent.Count > 0)
                    sent.Add(("·", muted));

                var spans = UploadLogText.SentSpans(
                    DisplayNameFor(category.Key),
                    category,
                    changed.Contains(category.Key),
                    proof,
                    stepsProven);
                foreach (var (text, highlight) in spans)
                    sent.Add((text, highlight ? Brand.Gold : null));
            }

            Widgets.DrawWrappedSpans(sent.ToArray());

            // Only failures: a collection the user switched off was skipped by choice (see
            // UploadLogEntry.UnreadableCategoryKeys).
            var unreadable = entry.UnreadableCategoryKeys;
            if (unreadable.Count > 0)
            {
                // By display name, since a key and its name need not sort alike (see
                // UploadLogText.KeysInDisplayOrder).
                var skipped = new List<string>(unreadable.Count);
                foreach (var key in UploadLogText.KeysInDisplayOrder(unreadable, DisplayNameFor))
                    skipped.Add(DisplayNameFor(key));
                Widgets.DrawWrapped(
                    $"Could not read: {string.Join(", ", skipped)}", ImGuiCol.Text, null);
            }
        }
    }

    /// <summary>The display name a category's collector declared, or the raw key as a fallback.</summary>
    private string DisplayNameFor(string categoryKey) =>
        categoryNames.TryGetValue(categoryKey, out var name) ? name : categoryKey;
}
