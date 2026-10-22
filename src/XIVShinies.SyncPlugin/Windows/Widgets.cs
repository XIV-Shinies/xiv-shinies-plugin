using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The shared drawing primitives the plugin's windows are built from: icons, brand buttons, chips,
/// wrapped text, separators, and the toggle switch.
/// </summary>
/// <remarks>
/// Stateless: a primitive takes the caller's font and wrap edge (a card's inner right, or null for
/// the content region's edge). Colors come from Dalamud's palette and the active style, because
/// users pick themes, light ones included; only <see cref="Brand"/>'s accents and
/// <see cref="BrandToggle"/>'s textless gray off-state track and white knob are fixed. Muted text
/// (<c>ImGuiCol.TextDisabled</c>) is for incidental text such as relative timestamps, empty-state
/// filler, captions over what they label, and hints that restate a disabled control's state, and for
/// copy about a feature the website has switched off; anything else the user must read to decide
/// something or to understand what the plugin does, consent copy above all, draws at
/// <c>ImGuiCol.Text</c>.
/// </remarks>
internal static class Widgets
{
    /// <summary>
    /// The scale of every icon that sits beside a line of body text, such as a warning triangle. A
    /// card title's icon draws full size.
    /// </summary>
    /// <remarks>
    /// Shared so all status icons read as one family. At full size an icon outweighs the short line
    /// beside it.
    /// </remarks>
    internal const float InlineIconScale = 0.82f;

    /// <summary>The red for warnings the user should act on.</summary>
    /// <remarks>
    /// The palette's <c>DalamudRed</c> moved 40% toward the style's text color: pure red is hard to
    /// read at body-text size, and the mix stays readable on any theme (#FF6666 with the default white
    /// text, a darker red with a light theme's dark text). A property, so it reads the style each time
    /// it is drawn; every caller is draw code.
    /// </remarks>
    internal static Vector4 ErrorColor =>
        Vector4.Lerp(ImGuiColors.DalamudRed, ImGui.GetStyle().Colors[(int)ImGuiCol.Text], 0.4f);

    /// <summary>The green for good news, such as an accepted upload or a verified token.</summary>
    /// <remarks>
    /// A property because the palette's colors belong to the active style: read on each draw, it
    /// follows a theme the user changes while the game runs.
    /// </remarks>
    internal static Vector4 SuccessColor => ImGuiColors.HealerGreen;

    /// <summary>The yellow for "working, but worth knowing" states, such as a cached source note.</summary>
    /// <remarks>A property (see <see cref="SuccessColor"/>).</remarks>
    internal static Vector4 CautionColor => ImGuiColors.DalamudYellow;

    /// <summary>The vertical gap between sections.</summary>
    internal static void SectionGap() => ImGui.Dummy(new Vector2(0f, 10f * ImGuiHelpers.GlobalScale));

    /// <summary>
    /// Moves the cursor so a widget of the given width ends at the right edge: the content edge by
    /// default, or an explicit one such as a card's inner right.
    /// </summary>
    /// <remarks>
    /// Never moves the cursor left, so in a window too narrow for the widget the row runs long instead
    /// of overlapping what the line already holds.
    /// </remarks>
    internal static void AlignRight(float width, float? rightEdge = null)
    {
        var edge = rightEdge ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), edge - width));
    }

    /// <summary>A branded divider: a 2px teal rule instead of ImGui's gray separator.</summary>
    /// <param name="innerRight">
    /// A card's inner right edge in cursor space, or null to span the content region.
    /// </param>
    internal static void BrandSeparator(float? innerRight)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = innerRight is { } edge
            ? edge - ImGui.GetCursorPosX()
            : ImGui.GetContentRegionAvail().X;
        var thickness = 2f * ImGuiHelpers.GlobalScale;

        ImGui.GetWindowDrawList().AddRectFilled(
            start,
            start + new Vector2(width, thickness),
            ImGui.GetColorU32(Brand.TealRule),
            0f,
            ImDrawFlags.None);

        ImGui.Dummy(new Vector2(0f, thickness + (4f * ImGuiHelpers.GlobalScale)));
    }

    /// <summary>Draws wrapped text in a color, which <c>TextWrapped</c> cannot.</summary>
    /// <remarks>
    /// Lines break only at spaces, measured to the caller's edge (see <see cref="BreakForWrap"/>), and
    /// the wrap position pushed here (see <see cref="WrapPosFor"/>) wins over an enclosing one, such as
    /// a card's. A null <paramref name="wrapEdge"/> means the content region's right edge, which in a
    /// table is the cell's.
    /// </remarks>
    internal static void DrawWrapped(string text, Vector4 color, float? wrapEdge)
    {
        var broken = BreakForWrap(text, wrapEdge, out var everyLineFits);

        using (ImRaii.PushColor(ImGuiCol.Text, color))
        using (ImRaii.TextWrapPos(WrapPosFor(everyLineFits, wrapEdge)))
        {
            ImGui.TextUnformatted(broken);
        }
    }

    /// <summary>
    /// The wrap position to push for text <see cref="BreakForWrap"/> has already broken: none when
    /// every line fits, and otherwise the caller's edge.
    /// </summary>
    /// <remarks>
    /// A negative position turns ImGui's own wrapping off, so the breaks already made are the only
    /// ones (see <see cref="TextWrap"/> for why, and which text still needs that wrapping). 0 is the
    /// content region's right edge.
    /// </remarks>
    private static float WrapPosFor(bool everyLineFits, float? wrapEdge) =>
        everyLineFits ? -1f : (wrapEdge ?? 0f);

    /// <summary>Draws wrapped text in one of the current style's own colors.</summary>
    /// <remarks>
    /// Passes the stored color. <c>GetColorU32</c> would apply the style's alpha, which ImGui applies
    /// again when drawing, so text in a disabled scope would come out fainter than plain text.
    /// </remarks>
    internal static void DrawWrapped(string text, ImGuiCol styleColor, float? wrapEdge) =>
        DrawWrapped(text, ImGui.GetStyle().Colors[(int)styleColor], wrapEdge);

    /// <summary>
    /// Breaks text drawn from the cursor to a wrap edge at spaces only (see <see cref="TextWrap"/>).
    /// </summary>
    /// <param name="wrapEdge">
    /// The wrap position in cursor space, or null for the content region's right edge.
    /// </param>
    /// <param name="everyLineFits">
    /// Whether every line of the result fits, which decides the wrap position (see
    /// <see cref="WrapPosFor"/>).
    /// </param>
    private static string BreakForWrap(string text, float? wrapEdge, out bool everyLineFits)
    {
        var cursorX = ImGui.GetCursorPosX();
        var edge = wrapEdge ?? (cursorX + ImGui.GetContentRegionAvail().X);

        // One scaled pixel short, so a line that exactly fills the room keeps slack against ImGui's
        // own rounding where its wrapping is still on.
        return BreakAtSpacesForFont(text, edge - cursorX - ImGuiHelpers.GlobalScale, out everyLineFits);
    }

    /// <summary>Breaks text at spaces for a width, measured in the current font.</summary>
    private static string BreakAtSpacesForFont(string text, float width, out bool everyLineFits) =>
        TextWrap.BreakAtSpaces(text, width, static s => ImGui.CalcTextSize(s).X, out everyLineFits);

    /// <summary>
    /// A wrapped paragraph whose spans carry their own color (null for the normal text color). ImGui
    /// text has one color per call, so this flows the words by hand.
    /// </summary>
    /// <remarks>
    /// Words are joined by single spaces, so attach punctuation to a span's words rather than starting
    /// a span with it.
    /// </remarks>
    internal static void DrawWrappedSpans(params (string Text, Vector4? Color)[] spans)
    {
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var first = true;

        // Zero vertical spacing keeps lines as tight as one wrapped Text call. ImGui's wrapping is off, so a
        // card's wrap scope cannot split a word this flow measured as fitting.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemSpacing,
                   new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f)))
        using (ImRaii.TextWrapPos(-1f))
        {
            foreach (var (text, color) in spans)
            {
                foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!first)
                    {
                        // After SameLine, the available region is what remains of the line.
                        ImGui.SameLine(0f, spaceWidth);
                        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(word).X)
                            ImGui.NewLine();
                    }

                    if (color is { } spanColor)
                        ImGui.TextColored(spanColor, word);
                    else
                        ImGui.TextUnformatted(word);

                    first = false;
                }
            }
        }
    }

    /// <summary>Draws one FontAwesome glyph in a color, optionally shrunk.</summary>
    /// <param name="iconFont">The caller's icon font. Outside it the glyph draws as an empty box.</param>
    /// <param name="scale">
    /// A fraction of the icon font's size. Status icons pass <see cref="InlineIconScale"/>.
    /// </param>
    /// <remarks>
    /// Dalamud builds the icon font at the UI font's size, so a full-size glyph lines up with body
    /// text. A shrunk glyph is painted on the draw list, centered in a <c>Dummy</c> of the full-size
    /// box, so the cursor and any following <c>SameLine</c> behave as at full size.
    /// <c>SetWindowFontScale</c> is window-wide and not scoped by the font push, so each call is reset
    /// to 1 straight after use.
    /// </remarks>
    internal static void DrawIcon(IFontHandle iconFont, FontAwesomeIcon icon, Vector4 color, float scale = 1f)
    {
        using (iconFont.Push())
        {
            var glyph = icon.ToIconString();

            if (scale >= 1f)
            {
                ImGui.TextColored(color, glyph);
                return;
            }

            // The footprint reserved at any scale.
            var fullSize = ImGui.CalcTextSize(glyph);

            ImGui.SetWindowFontScale(scale);
            var scaledSize = ImGui.CalcTextSize(glyph);
            ImGui.SetWindowFontScale(1f);

            var topLeft = ImGui.GetCursorScreenPos();

            // Centers the shrunk glyph in the full-size box.
            var offset = (fullSize - scaledSize) / 2f;

            ImGui.SetWindowFontScale(scale);
            ImGui.GetWindowDrawList().AddText(topLeft + offset, ImGui.GetColorU32(color), glyph);
            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(fullSize);
        }
    }

    /// <summary>
    /// An icon beside wrapped text in one color: the layout of every icon-led line of body copy, such
    /// as red warnings and the "Reading from:" notes.
    /// </summary>
    /// <remarks>
    /// The text is grouped so its wrapped lines align under its first line, not under the icon.
    /// </remarks>
    /// <param name="wrapEdge">A card's inner right, or null for the window edge.</param>
    /// <param name="iconScale">
    /// Forwarded to <see cref="DrawIcon"/>. Status lines pass <see cref="InlineIconScale"/>.
    /// </param>
    internal static void DrawIconedText(
        IFontHandle iconFont, FontAwesomeIcon icon, Vector4 color, string text, float? wrapEdge,
        float iconScale = 1f)
    {
        DrawIcon(iconFont, icon, color, iconScale);
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            DrawWrapped(text, color, wrapEdge);
        }
    }

    /// <summary>
    /// A secondary button: bold label, theme colors at rest, teal hover (see
    /// <see cref="Brand.SecondaryHover"/>). Every non-primary button goes through here.
    /// </summary>
    internal static bool BoldButton(IFontHandle buttonFont, string label)
    {
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Brand.SecondaryHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Brand.SecondaryActive))
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.Button(label);
        }
    }

    /// <summary>A secondary button with an explicit size.</summary>
    internal static bool BoldButton(IFontHandle buttonFont, string label, Vector2 size)
    {
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Brand.SecondaryHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Brand.SecondaryActive))
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.Button(label, size);
        }
    }

    /// <summary>
    /// The branded primary button: dark bold text on teal, as on the website. One per screen, for the
    /// action that moves the user forward.
    /// </summary>
    /// <remarks>
    /// Not built on <see cref="BoldButton(IFontHandle, string)"/>, whose inner hover push would win
    /// over this one's. It owns its disabled state so look and behavior cannot disagree: every face
    /// color goes to <see cref="Brand.DisabledSurface"/>, and <c>ImRaii.Disabled</c> swallows the press.
    /// </remarks>
    /// <param name="enabled">False to draw it disabled and swallow the press.</param>
    internal static bool PrimaryButton(IFontHandle buttonFont, string label, Vector2 size, bool enabled = true)
    {
        var face = enabled ? Brand.Teal : Brand.DisabledSurface;
        var text = enabled ? Brand.TealForeground : Brand.DisabledForeground;

        using (ImRaii.PushColor(ImGuiCol.Button, face))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, enabled ? Brand.TealHover : face))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, enabled ? Brand.TealDark : face))
        using (ImRaii.PushColor(ImGuiCol.Text, text))
        using (ImRaii.Disabled(!enabled))
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.Button(label, size);
        }
    }

    /// <summary>
    /// The width of a padded button: the label in the bold font plus side padding. ImGui's own
    /// button width hugs the text.
    /// </summary>
    internal static float PaddedButtonWidth(IFontHandle buttonFont, string label)
    {
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.CalcTextSize(label).X + (28f * ImGuiHelpers.GlobalScale);
        }
    }

    /// <summary>The width of an icon-plus-label overlay button: both pieces plus button padding.</summary>
    internal static float IconButtonWidth(
        IFontHandle iconFont, IFontHandle buttonFont, FontAwesomeIcon icon, string label)
    {
        float iconWidth;
        using (iconFont.Push())
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;

        return PaddedButtonWidth(buttonFont, label) + iconWidth + (8f * ImGuiHelpers.GlobalScale);
    }

    /// <summary>
    /// Draws an icon and text centered over an already-drawn button with a blank label: a button's
    /// face, or feedback such as "Copied".
    /// </summary>
    /// <remarks>
    /// A button label has one font and this needs two. Text items take no clicks, so overlapping the
    /// button is harmless, and the cursor is restored afterwards.
    /// </remarks>
    internal static void DrawButtonFeedback(
        IFontHandle iconFont, IFontHandle buttonFont,
        Vector2 buttonPos, float buttonWidth, FontAwesomeIcon icon, Vector4 iconColor,
        string text, Vector4 textColor)
    {
        var resume = ImGui.GetCursorPos();

        float iconWidth;
        using (iconFont.Push())
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;

        float textWidth;
        using (buttonFont.Available ? buttonFont.Push() : null)
            textWidth = ImGui.CalcTextSize(text).X;

        var spacing = 8f * ImGuiHelpers.GlobalScale;
        var contentWidth = iconWidth + spacing + textWidth;
        ImGui.SetCursorPos(new Vector2(
            buttonPos.X + ((buttonWidth - contentWidth) / 2f),
            buttonPos.Y + ImGui.GetStyle().FramePadding.Y));

        // Never wrap: a card's wrap scope would fold a right-aligned button's label.
        using (ImRaii.TextWrapPos(-1f))
        {
            DrawIcon(iconFont, icon, iconColor);
            ImGui.SameLine(0f, spacing);
            using (buttonFont.Available ? buttonFont.Push() : null)
                ImGui.TextColored(textColor, text);
        }

        ImGui.SetCursorPos(resume);
    }

    /// <summary>The width of <see cref="BrandToggle"/>, for layout arithmetic.</summary>
    internal static float ToggleWidth => ImGui.GetFrameHeight() * 1.55f;

    /// <summary>
    /// An on/off switch whose hover color previews the click: green to turn it on, red to turn it
    /// off. Returns true on the frame the value flips.
    /// </summary>
    /// <remarks>
    /// Drawn by hand because Dalamud's ToggleButton takes its colors from the theme, with no per-state
    /// hover.
    /// </remarks>
    internal static bool BrandToggle(string id, ref bool value)
    {
        var topLeft = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var height = ImGui.GetFrameHeight();
        var width = ToggleWidth;
        var radius = height * 0.5f;

        // Supplies hit-testing and hover; the look is drawn over it.
        ImGui.InvisibleButton(id, new Vector2(width, height));
        var pressed = ImGui.IsItemClicked();
        if (pressed)
            value = !value;

        var track = ImGui.IsItemHovered()
            ? (value ? ErrorColor : SuccessColor)
            : (value ? Brand.TealDark : new Vector4(0.35f, 0.35f, 0.35f, 1f));

        drawList.AddRectFilled(
            topLeft,
            topLeft + new Vector2(width, height),
            ImGui.GetColorU32(track),
            radius,
            ImDrawFlags.None);

        var knobCenter = new Vector2(
            value ? topLeft.X + width - radius : topLeft.X + radius,
            topLeft.Y + radius);
        drawList.AddCircleFilled(
            knobCenter,
            radius - (2f * ImGuiHelpers.GlobalScale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));

        return pressed;
    }

    /// <summary>
    /// A muted section label followed by a thin rule to the row's right edge, so it reads as a
    /// divider rather than a caption.
    /// </summary>
    /// <remarks>
    /// The rule is painted on the draw list, so only the label advances the layout cursor, and it
    /// stops at <paramref name="innerRight"/> or the content edge. It takes the theme's disabled-text
    /// color, thinned, so it sits behind the label.
    /// </remarks>
    /// <param name="innerRight">A card's inner right edge in cursor space, or null outside any card.</param>
    internal static void DrawSectionLabel(string label, float? innerRight)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();

        // Cursor space, the space innerRight arrives in.
        var afterLabelX = ImGui.GetCursorPosX();
        var rightEdge = innerRight ?? (afterLabelX + ImGui.GetContentRegionAvail().X);

        var gap = 6f * ImGuiHelpers.GlobalScale;
        var width = Math.Max(0f, rightEdge - afterLabelX - gap);

        // The draw list works in screen space. The rule sits at the text line's middle.
        var centerY = ImGui.GetTextLineHeight() / 2f;
        var lineStart = ImGui.GetCursorScreenPos() + new Vector2(gap, centerY);
        var lineEnd = lineStart + new Vector2(width, 0f);

        var ruleColor = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        ruleColor.W *= 0.6f;

        var thickness = 1f * ImGuiHelpers.GlobalScale;
        ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd, ImGui.GetColorU32(ruleColor), thickness);

        // Leave the label's line, which SameLine kept the cursor on.
        ImGui.NewLine();
    }

    /// <summary>
    /// A chip's measurements and style constants, from <see cref="MeasureChip"/>. Both chip
    /// placements paint from one of these, so they stay pixel-identical.
    /// </summary>
    private readonly record struct ChipMetrics(
        string Glyph,
        string Text,
        Vector2 IconSize,
        Vector2 TextSize,
        Vector2 ChipSize,
        float PaddingX,
        float PaddingY,
        float IconTextGap,
        float Rounding,
        float Thickness,
        float ContentScale,
        float IconScale);

    /// <summary>
    /// Measures a chip without drawing it, so a caller can place it by its footprint first.
    /// </summary>
    /// <remarks>
    /// Each <c>SetWindowFontScale</c> is reset to 1 straight after use (see <see cref="DrawIcon"/>).
    /// </remarks>
    private static ChipMetrics MeasureChip(IFontHandle iconFont, FontAwesomeIcon icon, string text)
    {
        // Shrinks the whole badge, text and padding included, so it reads as a compact tag.
        const float ChipContentScale = 0.85f;

        // Smaller than the text: a glyph at text size carries more ink than a letter and competes
        // with the label.
        const float ChipIconScale = ChipContentScale * 0.7f;

        var paddingX = 6f * ChipContentScale * ImGuiHelpers.GlobalScale;
        var paddingY = 2f * ChipContentScale * ImGuiHelpers.GlobalScale;
        var thickness = 1f * ImGuiHelpers.GlobalScale;
        var iconTextGap = 3f * ImGuiHelpers.GlobalScale;

        // Both pieces are measured at the scales PaintChip draws them at, so the footprint matches
        // the ink.
        string glyph;
        Vector2 iconSize;
        using (iconFont.Push())
        {
            glyph = icon.ToIconString();
            ImGui.SetWindowFontScale(ChipIconScale);
            iconSize = ImGui.CalcTextSize(glyph);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.SetWindowFontScale(ChipContentScale);
        var textSize = ImGui.CalcTextSize(text);
        ImGui.SetWindowFontScale(1f);

        // Max, not the text's height: the two come from different fonts, and a glyph can still
        // measure taller than the label.
        var contentHeight = Math.Max(iconSize.Y, textSize.Y);
        var contentWidth = iconSize.X + iconTextGap + textSize.X;
        var chipSize = new Vector2(contentWidth + (2f * paddingX), contentHeight + (2f * paddingY));

        // A full pill: a slightly rounded rectangle would read as a button, which a chip is not.
        var rounding = chipSize.Y / 2f;

        return new ChipMetrics(
            glyph, text, iconSize, textSize, chipSize,
            paddingX, paddingY, iconTextGap, rounding, thickness,
            ChipContentScale, ChipIconScale);
    }

    /// <summary>
    /// Paints a measured chip at a screen position. Draw-list only: it never reads or moves the
    /// layout cursor.
    /// </summary>
    /// <param name="topLeft">The outline's top-left corner, in screen coordinates.</param>
    private static void PaintChip(
        IFontHandle iconFont, ChipMetrics metrics, Vector2 topLeft, Vector4 color, bool filled)
    {
        var contentOrigin = topLeft + new Vector2(metrics.PaddingX, metrics.PaddingY);
        var contentHeight = Math.Max(metrics.IconSize.Y, metrics.TextSize.Y);

        // Centered separately, since their heights can differ.
        var iconPos = contentOrigin + new Vector2(0f, (contentHeight - metrics.IconSize.Y) / 2f);
        var textPos = contentOrigin + new Vector2(
            metrics.IconSize.X + metrics.IconTextGap, (contentHeight - metrics.TextSize.Y) / 2f);

        var drawList = ImGui.GetWindowDrawList();
        var colorU32 = ImGui.GetColorU32(color);

        // An accent chip stays unfilled, or it would compete with the copy around it.
        if (filled)
        {
            var wash = color with { W = color.W * 0.16f };
            drawList.AddRectFilled(
                topLeft, topLeft + metrics.ChipSize, ImGui.GetColorU32(wash), metrics.Rounding);
        }

        drawList.AddRect(
            topLeft, topLeft + metrics.ChipSize, colorU32, metrics.Rounding, ImDrawFlags.None,
            metrics.Thickness);

        // AddText draws in the active font. The scales must match MeasureChip's, or the reserved
        // footprint stops describing what was drawn.
        using (iconFont.Push())
        {
            ImGui.SetWindowFontScale(metrics.IconScale);
            drawList.AddText(iconPos, colorU32, metrics.Glyph);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.SetWindowFontScale(metrics.ContentScale);
        drawList.AddText(textPos, colorU32, metrics.Text);
        ImGui.SetWindowFontScale(1f);
    }

    /// <summary>
    /// The width <see cref="DrawChip"/> will reserve, for deciding whether a chip fits before it is
    /// drawn.
    /// </summary>
    internal static float ChipWidth(IFontHandle iconFont, FontAwesomeIcon icon, string text) =>
        MeasureChip(iconFont, icon, text).ChipSize.X;

    /// <summary>
    /// Shows a tooltip with wrapped text. Call right after the hovered item, inside an
    /// <c>ImGui.IsItemHovered()</c> check.
    /// </summary>
    /// <remarks>
    /// A tooltip has no window edge to wrap at, so it wraps at a multiple of the font size. Lines
    /// break only at spaces.
    /// </remarks>
    internal static void DrawTooltip(string text)
    {
        ImGui.BeginTooltip();
        var wrapEdge = ImGui.GetFontSize() * 24f;
        var broken = BreakForWrap(text, wrapEdge, out var everyLineFits);
        ImGui.PushTextWrapPos(WrapPosFor(everyLineFits, wrapEdge));
        ImGui.TextUnformatted(broken);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// A small outlined chip, an icon then a label in a pill, flowing at the cursor and centered on a
    /// checkbox-height row.
    /// </summary>
    /// <remarks>
    /// The draw list does not move the cursor, so a <c>Dummy</c> reserves the painted footprint. It is
    /// a real item, so <c>ImGui.IsItemHovered()</c> right after this call answers for the chip.
    /// </remarks>
    /// <param name="filled">
    /// True to wash the chip with a faint tint of its own color, for a deliberately muted chip that
    /// would otherwise be easy to skim past.
    /// </param>
    internal static void DrawChip(
        IFontHandle iconFont, FontAwesomeIcon icon, string text, Vector4 color, bool filled = false)
    {
        var metrics = MeasureChip(iconFont, icon, text);

        // A checkbox row is frame-height whatever its font, so the shorter chip is centered in it.
        var rowHeight = ImGui.GetFrameHeight();

        var topLeft = ImGui.GetCursorScreenPos() + new Vector2(0f, (rowHeight - metrics.ChipSize.Y) / 2f);
        PaintChip(iconFont, metrics, topLeft, color, filled);

        // The full row height, so the next row starts where it would without the chip.
        ImGui.Dummy(new Vector2(metrics.ChipSize.X, rowHeight));
    }

    /// <summary>
    /// Draws a chip against a fixed right edge, over the full-width widget drawn just before it, such
    /// as a <c>CollapsingHeader</c>, beside which <c>SameLine</c> would push the chip off the window.
    /// </summary>
    /// <remarks>
    /// Reads the previous item's rectangle, so nothing may be drawn in between. The chip is draw-list
    /// only, so it takes none of the header's clicks and leaves the layout cursor where it was.
    /// </remarks>
    /// <param name="rightEdgeScreenX">
    /// The screen X the chip's right edge lands just inside: the card's inner edge, or the window's
    /// content edge outside a card.
    /// </param>
    internal static void DrawHeaderRightChip(
        IFontHandle iconFont, FontAwesomeIcon icon, string text, Vector4 color, float rightEdgeScreenX)
    {
        var metrics = MeasureChip(iconFont, icon, text);

        // Centered on the header's measured height.
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        var rowHeight = rowMax.Y - rowMin.Y;

        var margin = 8f * ImGuiHelpers.GlobalScale;

        var topLeft = new Vector2(
            rightEdgeScreenX - margin - metrics.ChipSize.X,
            rowMin.Y + ((rowHeight - metrics.ChipSize.Y) / 2f));

        PaintChip(iconFont, metrics, topLeft, color, filled: false);
    }
}
