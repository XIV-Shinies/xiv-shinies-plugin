using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Onboarding;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>The plugin window opened by the <c>/shinies</c> command.</summary>
/// <remarks>
/// Shows the first-run wizard until the user has consented, then the settings. Dalamud forbids
/// unprompted windows, so it opens only from <c>/shinies</c> or the installer's open and settings
/// buttons. It only draws: wizard state lives in <see cref="OnboardingState"/>, the rows in
/// <see cref="CategorySettingsView"/>, and what a probe means in <see cref="Onboarding.TokenCheck"/>.
/// </remarks>
// Split by concern: this file holds the window state, the lifecycle, the card system and the
// widget bindings; each MainWindow.*.cs sibling holds one screen or panel.
internal sealed partial class MainWindow : Window, IDisposable
{
    /// <summary>The longest token string the input box will accept, comfortably above the real 47.</summary>
    private const int TokenInputCapacity = 128;

    private readonly Configuration configuration;
    private readonly SyncManager syncManager;
    private readonly IReadOnlyList<ICollector> collectors;
    private readonly TokenVerifier verifier;

    // Owned here. The atlas builds asynchronously, so pushes check Available; until then headings
    // draw at body size.
    private readonly IFontHandle headingFont;

    // Borrowed from the UiBuilder, so Dispose leaves it alone.
    private readonly IFontHandle iconFont;

    // Bold Axis for button labels, owned here. ImGui has no font weights, so bold is its own font.
    private readonly IFontHandle buttonFont;

    // Asked for the mascot at each draw (see DrawSettingsHeader); the textures it hands out are
    // Dalamud's, never disposed here.
    private readonly ITextureProvider textureProvider;

    // The masthead's version. Cosmetic: uploads and the pasted diagnostic read the real one.
#if DEBUG
    // Writable for screenshots (see OverrideVersionForScreenshots). The seeding command writes it on
    // the framework thread while the draw thread reads it; at worst one frame is stale.
    private volatile string pluginVersion;
#else
    private readonly string pluginVersion;
#endif

    // The running version, for the pasted diagnostic. Kept apart from the masthead's copy so the
    // screenshot override can never reach a bug report.
    private readonly string diagnosticVersion;

    private readonly UploadLogTable uploadLogTable;

    // The masthead's link row. Static so the always-visible draw path allocates nothing per frame.
    // Id is the stable ImGui identity (`###`).
    private static readonly (FontAwesomeIcon Icon, Vector4 IconColor, string Label, string Id, string Url)[]
        LinkButtons =
        {
            // The project's website, not the configured backend: these are brand links.
            (FontAwesomeIcon.Globe, Brand.Teal, "xiv-shinies.com", "###linkSite", BackendUrl.Default),
            (FontAwesomeIcon.Comments, Brand.Teal, "Discord", "###linkDiscord", PluginMeta.DiscordUrl),
            (FontAwesomeIcon.Code, Brand.Teal, "Source code", "###linkSource", PluginMeta.SourceUrl),
            (FontAwesomeIcon.Heart, Brand.Gold, "Sponsor", "###linkSponsor", PluginMeta.SponsorUrl),
        };

    private readonly OnboardingState onboarding = new();

    // Group keys badged "New" while the window is open. Drawing a group saves it as seen at once, so
    // this keeps the badge up until the window closes (see OnClose).
    private readonly HashSet<string> groupsBadgedThisSession = new();

    // The same, for whole collections. A separate set because category keys (ours) and group keys
    // (the server's) can collide.
    private readonly HashSet<string> categoriesBadgedThisSession = new();

    // The same, for the Crucible sharing card's "New" chip (see CrucibleBadge).
    private bool crucibleBadgedThisSession;

    // The stored choices the wizard's consent step opened with (see DrawChooseCategoriesStep). Null
    // until the step draws; cleared when the user leaves the step or closes the window.
    private (bool AnythingSwitchedOn, bool TrackerOn)? chooseStepOpening;

    // Whether this wizard frame drew a group checkbox, not whether the server sent any. Finish draws
    // after the rows and reads it (see PluginSettings.SettleItemGroupConsent).
    private bool wizardShowedGroups;

    // The same for the live tracker's checkbox: was it drawn enabled? It starts ticked, so a user who
    // could not reach it has not agreed to it (see PluginSettings.SettleOccultConsent).
    private bool wizardCouldOfferOccultToggle;

    // The token box's text. ImGui edits it by ref, so it must outlive the frame.
    private string tokenInput;

    // The last successful probe's account, for the claimed-characters list.
    private MeResponse? account;

    // Until this instant the Sync now button shows "Syncing": a minimum display time, so even an
    // instant sync visibly reacts to the click.
    private DateTime syncFeedbackUntil;

    // The same, for the upload log's "Copied" flash.
    private DateTime logCopyFeedbackUntil;

    // Whether this session's automatic token probe has run or was unnecessary (see TryAutoVerifyToken).
    private bool sessionTokenProbeRequested;

    // While a BrandCard is open, its inner right edge in cursor space, so helpers wrap and size to the
    // card. Null outside any card.
    private float? activeCardInnerRight;

    /// <summary>Builds the window without opening it.</summary>
    public MainWindow(
        Configuration configuration,
        ApiClient apiClient,
        SyncManager syncManager,
        IReadOnlyList<ICollector> collectors,
        ITextureProvider textureProvider,
        IFontAtlas fontAtlas,
        IFontHandle iconFont,
        float baseFontSizePx,
        string pluginVersion)
        // The `###` ID stays stable, so a changed title keeps the window's saved position and size.
        : base($"{PluginMeta.DisplayName}###XIVShiniesMain")
    {
        this.configuration = configuration;
        this.syncManager = syncManager;
        this.collectors = collectors;
        this.textureProvider = textureProvider;
        this.iconFont = iconFont;
        this.pluginVersion = pluginVersion;
        this.diagnosticVersion = pluginVersion;
        verifier = new TokenVerifier(apiClient);
        uploadLogTable = new UploadLogTable(collectors);

        headingFont = fontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk => tk.AddDalamudDefaultFont(baseFontSizePx * 1.3f, null)));

        buttonFont = fontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Axis, baseFontSizePx) { Bold = true });

        // Unscaled: Dalamud's Window scales Size and SizeConstraints itself. OnOpen's size holds until
        // the user resizes; below the minimum width, the header and upload table stop being worth reading.
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Seeded from the saved token, so reopening the wizard does not look like it was lost.
        tokenInput = configuration.Settings.Token;
    }

#if DEBUG
    /// <summary>
    /// Shows a different version in the masthead, for release screenshots taken before the release
    /// flow sets the real one.
    /// </summary>
    /// <remarks>
    /// Only the release flow may write the version, so a development build is told what to draw
    /// instead. Uploads and the pasted diagnostic keep the real version. A leading "v" is dropped
    /// because the masthead adds its own.
    /// </remarks>
    public void OverrideVersionForScreenshots(string version) =>
        pluginVersion = version.TrimStart('v', 'V');
#endif

    /// <summary>Cancels any token probe in flight and releases the fonts this window owns.</summary>
    public void Dispose()
    {
        verifier.Dispose();
        headingFont.Dispose();
        buttonFont.Dispose();
    }

    /// <summary>Widens this window's padding.</summary>
    /// <remarks>
    /// Runs just before Begin, which reads the value, and <see cref="PostDraw"/> pops it, so only this
    /// window and its child windows get it. Style values are physical pixels, hence the scale.
    /// </remarks>
    public override void PreDraw()
    {
        base.PreDraw();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * ImGuiHelpers.GlobalScale));
    }

    /// <summary>Pops what <see cref="PreDraw"/> pushed. The pair must stay balanced.</summary>
    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        base.PostDraw();
    }

    /// <summary>
    /// Chooses the window's opening size: the content's preferred size, capped by the screen.
    /// </summary>
    /// <remarks>
    /// At high UI scales the logical desktop shrinks, so a reasonable content size can take most of
    /// the screen. The preferred size and the display are both in logical units: ImGui reports
    /// physical pixels, and Dalamud scales <see cref="Window.Size"/> back up. Done here, not in the
    /// constructor, because ImGui is only safe to ask inside its own frame.
    /// </remarks>
    public override void OnOpen()
    {
        var displayLogical = ImGui.GetIO().DisplaySize / ImGuiHelpers.GlobalScale;

        // Tall because the settings list is. If the rows get more compact, lower the preference
        // rather than raising the cap.
        Size = new Vector2(
            Math.Min(560f, displayLogical.X * 0.45f),
            Math.Min(620f, displayLogical.Y * 0.70f));
    }

    /// <summary>
    /// Retires the badges shown during this visit and forgets the consent step's opening state.
    /// </summary>
    /// <remarks>
    /// The window object lives as long as the plugin, so without this the session badges would
    /// linger until it unloads.
    /// </remarks>
    public override void OnClose()
    {
        categoriesBadgedThisSession.Clear();
        groupsBadgedThisSession.Clear();
        crucibleBadgedThisSession = false;
        chooseStepOpening = null;
    }

    /// <summary>Draws the window. Called every frame while it is open.</summary>
    /// <remarks>Keep it cheap and never block: it runs on the thread that draws the game's frames.</remarks>
    public override void Draw()
    {
        // Fold in a finished token probe before drawing anything that depends on it.
        ConsumeTokenProbe();

        if (!configuration.Settings.OnboardingComplete)
        {
            DrawWizard();
            return;
        }

        DrawSettings();
    }

    /// <summary>The teal window title with an accent underline, and an optional right-aligned note.</summary>
    private void DrawBrandTitle(string? rightAligned)
    {
        float titleWidth;

        using (headingFont.Available ? headingFont.Push() : null)
        {
            ImGui.TextColored(Brand.Teal, PluginMeta.DisplayName);
            titleWidth = ImGui.CalcTextSize(PluginMeta.DisplayName).X;
        }

        if (rightAligned is not null)
        {
            ImGui.SameLine();
            Widgets.AlignRight(ImGui.CalcTextSize(rightAligned).X);

            // Full contrast: the wizard's step counter is meant to be read.
            ImGui.TextUnformatted(rightAligned);
        }

        // ImGui has no underlined text, so the underline goes on the draw list.
        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(
            start,
            start + new Vector2(titleWidth, 2f * ImGuiHelpers.GlobalScale),
            ImGui.GetColorU32(Brand.Teal),
            0f,
            ImDrawFlags.None);

        // Draw-list shapes do not advance the layout cursor, so reserve the underline's space and air.
        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
    }

    /// <summary>A teal section label over the standard branded divider.</summary>
    private void DrawSectionHeading(string text)
    {
        ImGui.TextColored(Brand.Teal, text);
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
    }

    /// <summary>
    /// Binds this window's card wrap edge to <see cref="Widgets.BrandSeparator"/>: inside a
    /// <see cref="BrandCard"/> the rule spans the card's inner width, not the window's.
    /// </summary>
    private void BrandSeparator() => Widgets.BrandSeparator(activeCardInnerRight);

    /// <summary>
    /// A bordered card with brand padding and an optional teal icon-and-title header, for a
    /// <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// The content goes in an ImGui group and the border is drawn around it afterwards, so a card of
    /// any height needs no arithmetic. Text inside wraps at the card's inner edge.
    /// </remarks>
    private CardScope BrandCard(FontAwesomeIcon? icon = null, string? title = null) =>
        new(this, icon, title);

    /// <summary>The disposable half of <see cref="BrandCard"/>; disposal closes and draws the card.</summary>
    private sealed class CardScope : IDisposable
    {
        private readonly MainWindow owner;
        private readonly Vector2 topLeft;
        private readonly float rightEdge;
        private readonly float padding;
        private readonly IDisposable wrapScope;

        // The enclosing card's edge, restored on close so a nested card hands it back.
        private readonly float? previousInnerRight;

        public CardScope(MainWindow owner, FontAwesomeIcon? icon, string? title)
        {
            this.owner = owner;
            padding = 16f * ImGuiHelpers.GlobalScale;
            topLeft = ImGui.GetCursorScreenPos();
            rightEdge = topLeft.X + ImGui.GetContentRegionAvail().X;

            // Group everything inside the padding so the closing border can measure it.
            ImGui.SetCursorScreenPos(topLeft + new Vector2(padding, padding));
            ImGui.BeginGroup();

            var innerRightLocal =
                ImGui.GetCursorPosX() + (rightEdge - padding - ImGui.GetCursorScreenPos().X);
            wrapScope = ImRaii.TextWrapPos(innerRightLocal);
            previousInnerRight = owner.activeCardInnerRight;
            owner.activeCardInnerRight = innerRightLocal;

            if (icon is { } cardIcon && title is not null)
            {
                owner.DrawCardTitle(cardIcon, title);
                owner.CloseCardHeader();
            }
        }

        public void Dispose()
        {
            owner.activeCardInnerRight = previousInnerRight;
            wrapScope.Dispose();
            ImGui.EndGroup();

            var bottom = ImGui.GetItemRectMax().Y + padding;
            ImGui.GetWindowDrawList().AddRect(
                topLeft,
                new Vector2(rightEdge, bottom),
                ImGui.GetColorU32(ImGuiCol.Border),
                0f,
                ImDrawFlags.None,
                1f * ImGuiHelpers.GlobalScale);

            // Draw-list shapes do not advance the cursor, so step past the border.
            ImGui.SetCursorScreenPos(new Vector2(topLeft.X, bottom));
            ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
        }
    }

    /// <summary>
    /// Binds this window's bold font to <see cref="Widgets.BoldButton(IFontHandle, string)"/>.
    /// </summary>
    private bool BoldButton(string label) => Widgets.BoldButton(buttonFont, label);

    /// <summary>
    /// Binds this window's bold font to <see cref="Widgets.BoldButton(IFontHandle, string, Vector2)"/>.
    /// </summary>
    private bool BoldButton(string label, Vector2 size) => Widgets.BoldButton(buttonFont, label, size);

    /// <summary>Binds this window's bold font to <see cref="Widgets.PrimaryButton"/>.</summary>
    private bool PrimaryButton(string label, Vector2 size, bool enabled = true) =>
        Widgets.PrimaryButton(buttonFont, label, size, enabled);

    /// <summary>Binds this window's bold font to <see cref="Widgets.PaddedButtonWidth"/>.</summary>
    private float PaddedButtonWidth(string label) => Widgets.PaddedButtonWidth(buttonFont, label);

    /// <summary>Binds this window's fonts to <see cref="Widgets.DrawButtonFeedback"/>.</summary>
    private void DrawButtonFeedback(
        Vector2 buttonPos, float buttonWidth, FontAwesomeIcon icon, Vector4 iconColor,
        string text, Vector4 textColor) =>
        Widgets.DrawButtonFeedback(
            iconFont, buttonFont, buttonPos, buttonWidth, icon, iconColor, text, textColor);

    /// <summary>
    /// A red warning the user should act on: a triangle beside wrapped red text. Every red warning
    /// goes through here, so the icon is part of what red means.
    /// </summary>
    private void DrawWarning(string text) =>
        DrawIconedText(
            FontAwesomeIcon.ExclamationTriangle, Widgets.ErrorColor, text, Widgets.InlineIconScale);

    /// <summary>
    /// Binds this window's icon font and wrap edge to <see cref="Widgets.DrawIconedText"/>.
    /// </summary>
    private void DrawIconedText(FontAwesomeIcon icon, Vector4 color, string text, float iconScale = 1f) =>
        Widgets.DrawIconedText(iconFont, icon, color, text, activeCardInnerRight, iconScale);

    /// <summary>
    /// The teal icon and title of a card header that right-aligns a control, which BrandCard's own
    /// header has no slot for. Close the row with <see cref="CloseCardHeader"/> after the control.
    /// </summary>
    private void DrawCardTitle(FontAwesomeIcon icon, string title)
    {
        ImGui.AlignTextToFramePadding();
        DrawIcon(icon, Brand.Teal);
        ImGui.SameLine(0f, 10f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(Brand.Teal, title);
    }

    /// <summary>Closes a custom card header row: air, the branded divider, then body spacing.</summary>
    private void CloseCardHeader()
    {
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
        BrandSeparator();
        ImGui.Spacing();
    }

    /// <summary>Binds this window's icon font to <see cref="Widgets.DrawIcon"/>.</summary>
    private void DrawIcon(FontAwesomeIcon icon, Vector4 color, float scale = 1f) =>
        Widgets.DrawIcon(iconFont, icon, color, scale);

    /// <summary>
    /// The privacy card. It discloses what is sent about the user's character, so it draws at full
    /// contrast.
    /// </summary>
    private void DrawPrivacyCard(string text)
    {
        using (BrandCard(FontAwesomeIcon.Shield, "Your privacy"))
        {
            DrawWrapped(text, ImGuiCol.Text);
        }
    }

    /// <summary>Binds this window's fonts to <see cref="Widgets.IconButtonWidth"/>.</summary>
    private float IconButtonWidth(FontAwesomeIcon icon, string label) =>
        Widgets.IconButtonWidth(iconFont, buttonFont, icon, label);

    /// <summary>Binds this window's card wrap edge to <see cref="Widgets.DrawSectionLabel"/>.</summary>
    private void DrawSectionLabel(string label) =>
        Widgets.DrawSectionLabel(label, activeCardInnerRight);

    /// <summary>Binds this window's icon font to <see cref="Widgets.DrawChip"/>.</summary>
    private void DrawChip(FontAwesomeIcon icon, string text, Vector4 color, bool filled = false) =>
        Widgets.DrawChip(iconFont, icon, text, color, filled);

    // BackendHost's cache, keyed on the setting it came from. Empty until first asked.
    private string? backendHostSource;
    private string backendHost = string.Empty;

    /// <summary>
    /// The configured server's host. Every sentence in this window that names the service asks here.
    /// </summary>
    /// <remarks>
    /// <see cref="BackendUrl.DisplayHost"/> owns the derivation and the fallback. Cached per setting,
    /// because parsing a URL every frame allocates on always-visible paths; a changed setting still
    /// takes effect without a restart.
    /// </remarks>
    private string BackendHost()
    {
        var configured = configuration.Settings.BaseUrl;

        // DisplayHost never answers with an empty string, so an empty cache means "not yet asked".
        if (backendHost.Length == 0 || backendHostSource != configured)
        {
            backendHostSource = configured;
            backendHost = BackendUrl.DisplayHost(configured);
        }

        return backendHost;
    }

    /// <summary>Binds this window's icon font to <see cref="Widgets.ChipWidth"/>.</summary>
    private float ChipWidth(FontAwesomeIcon icon, string text) =>
        Widgets.ChipWidth(iconFont, icon, text);

    /// <summary>Binds this window's icon font to <see cref="Widgets.DrawHeaderRightChip"/>.</summary>
    private void DrawHeaderRightChip(FontAwesomeIcon icon, string text, Vector4 color, float rightEdgeScreenX) =>
        Widgets.DrawHeaderRightChip(iconFont, icon, text, color, rightEdgeScreenX);

    /// <summary>
    /// Binds this window's card wrap edge to <see cref="Widgets.DrawWrapped(string, Vector4, float?)"/>.
    /// </summary>
    private void DrawWrapped(string text, Vector4 color) =>
        Widgets.DrawWrapped(text, color, activeCardInnerRight);

    /// <summary>
    /// Binds this window's card wrap edge to <see cref="Widgets.DrawWrapped(string, ImGuiCol, float?)"/>.
    /// </summary>
    private void DrawWrapped(string text, ImGuiCol styleColor) =>
        Widgets.DrawWrapped(text, styleColor, activeCardInnerRight);
}
