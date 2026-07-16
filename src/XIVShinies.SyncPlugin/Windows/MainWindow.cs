using System;
using System.Collections.Generic;
// Vector2/Vector4 are simple float tuples from the .NET math library. ImGui uses them for sizes,
// positions, and colors (RGBA, each component 0..1).
using System.Numerics;
// At Dalamud API 15 the ImGui bindings live under Dalamud.Bindings.ImGui (NOT the older
// ImGuiNET package). ImGui is an "immediate mode" GUI: instead of building a retained tree of
// components like React, you re-issue draw calls every frame inside Draw() below.
using Dalamud.Bindings.ImGui;
// FontAwesomeIcon (the icon glyphs) and its ToIconString extension.
using Dalamud.Interface;
// GameFontStyle/GameFontFamily — the game's own typefaces, which are the only route to real bold
// (ImGui has no font-weight styling; bold is a different font).
using Dalamud.Interface.GameFonts;
// Font handles: a heading-sized variant of the default font, and the icon font, are pushed and
// popped around the text they style — fonts are a stack in ImGui, exactly like colors.
using Dalamud.Interface.ManagedFontAtlas;
// ISharedImmediateTexture — a lazily-loaded, Dalamud-owned image ImGui can draw (the mascot).
using Dalamud.Interface.Textures;
// Scaling helpers: users on high-DPI displays run Dalamud at 1.5–2x, and raw pixel sizes ignore it.
using Dalamud.Interface.Utility;
// Scoped wrappers for ImGui's push/pop pairs. `using (ImRaii.Disabled(...))` guarantees the matching
// End/Pop even if the block throws — a whole class of unbalanced-stack bugs stops existing.
using Dalamud.Interface.Utility.Raii;
// The windowing helpers (Window base class, WindowSystem) that manage plugin windows for us.
using Dalamud.Interface.Windowing;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Onboarding;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The plugin window opened by the <c>/shinies</c> command.
/// </summary>
/// <remarks>
/// <para>
/// Shows the first-run wizard until the user has consented, and the settings afterwards. It never
/// opens itself: Dalamud forbids unprompted windows, so it appears only from <c>/shinies</c> or the
/// installer's open and settings buttons.
/// </para>
/// <para>
/// This class draws and nothing else. Which step the wizard is on lives in <see cref="OnboardingState"/>;
/// which rows to draw lives in <see cref="CategorySettingsView"/>; what a token probe means lives in
/// <see cref="Onboarding.TokenCheck"/>. All three are pure and unit-tested, which is why almost
/// nothing here can be wrong in an interesting way.
/// </para>
/// </remarks>
// `sealed` means no other class may inherit from this one. We inherit from Dalamud's `Window` base
// class AND implement `IDisposable` — the .NET pattern for "I hold something that must be cleaned
// up", whose `Dispose()` is the rough equivalent of a React `useEffect` cleanup function.
//
// `internal` (visible only inside this assembly) rather than `public`, because it takes a SyncManager,
// which is itself internal. C# refuses to expose a public method whose parameter type is less
// accessible than the method — otherwise a caller outside the assembly could see the constructor but
// never name a value to pass it. Nothing outside the plugin constructs this window anyway.
//
// `partial` splits this one class across several files by concern — a first-class C# feature
// (WinForms and WPF are built on it), not inheritance: every MainWindow.*.cs file contributes
// members to this same class. This file holds the class doc, the window state, the lifecycle,
// and the shared card system and widget bindings; each sibling file holds one screen or panel.
internal sealed partial class MainWindow : Window, IDisposable
{
    /// <summary>The longest token string the input box will accept, comfortably above the real 47.</summary>
    private const int TokenInputCapacity = 128;

    private readonly Configuration configuration;
    private readonly SyncManager syncManager;
    private readonly IReadOnlyList<ICollector> collectors;
    private readonly TokenVerifier verifier;

    // A heading-sized build of the default font, owned by this window (created below, disposed in
    // Dispose). Font atlases build asynchronously, so pushes are guarded on Available — until then
    // headings just render at body size.
    private readonly IFontHandle headingFont;

    // Dalamud's FontAwesome icon font — borrowed from the UiBuilder, never disposed here.
    private readonly IFontHandle iconFont;

    // A bold build of the game's Axis face for button labels, owned here. ImGui has no font-weight
    // styling, so "bold" means a different font.
    private readonly IFontHandle buttonFont;

    // The mascot image for the settings header. Shared and Dalamud-owned: it loads in the
    // background and is never disposed here.
    private readonly ISharedImmediateTexture mascotTexture;

    // The running version, shown in the masthead — the same string the uploads carry, so what a
    // user reads in a bug report matches what the server saw.
    private readonly string pluginVersion;

    // The upload log's table renderer, which captures each collector's display name at
    // construction — the collector list is fixed for the plugin's lifetime.
    private readonly UploadLogTable uploadLogTable;

    // The masthead's link row. Static because every entry is a constant and Draw runs every
    // frame — rebuilding the array per frame would be needless garbage in an always-visible
    // path. Id is the stable ImGui identity (`###`), precomputed for the same reason.
    private static readonly (FontAwesomeIcon Icon, Vector4 IconColor, string Label, string Id, string Url)[]
        LinkButtons =
        {
            (FontAwesomeIcon.Globe, Brand.Teal, "xiv-shinies.com", "###linkSite", BackendUrl.Default),
            (FontAwesomeIcon.Comments, Brand.Teal, "Discord", "###linkDiscord", PluginMeta.DiscordUrl),
            (FontAwesomeIcon.Code, Brand.Teal, "Source code", "###linkSource", PluginMeta.SourceUrl),
            (FontAwesomeIcon.Heart, Brand.Gold, "Sponsor", "###linkSponsor", PluginMeta.SponsorUrl),
        };

    private readonly OnboardingState onboarding = new();

    // Group keys whose "New" badge has been shown during THIS window's lifetime. The moment a new
    // group is drawn we persist its "seen" flag (so it is gone next session), but we keep drawing its
    // badge for the rest of the session by remembering it here — otherwise the badge would vanish one
    // frame after appearing, since the very next frame's rebuild would report it as no longer new.
    // See DrawGroupCheckboxes for the full lifecycle.
    private readonly HashSet<string> seenThisSession = new();

    // Whether the wizard has put per-group consent checkboxes on screen during THIS frame. Reset at
    // the top of every wizard frame and set by DrawGroupCheckboxes when it actually draws a group row,
    // so by the time the footer's Finish button is handled — drawn after the rows, in the same frame —
    // it answers "was this user shown groups to choose from?" rather than "did the server ever have
    // any?". Those differ: a config that carries no groups still answers, and a poll that fails answers
    // too, and in both cases the user chose nothing group-level because there was nothing to choose
    // from. See DrawWizardNav for what rides on the answer.
    private bool wizardShowedGroups;

    // The text currently in the token box. ImGui hands us a `ref string` and rewrites it in place,
    // so this must be a field rather than something rebuilt each frame. Seeded from the saved token
    // in the constructor.
    private string tokenInput;

    // The account the last successful probe belonged to, for showing which characters are claimed.
    private MeResponse? account;

    // Until this instant, the Sync now button shows "Syncing" feedback instead of its label.
    // Immediate mode has no timers or callbacks: the window redraws every frame anyway, so
    // transient UI is just a deadline that each frame compares against the clock. This one is a
    // minimum display time, so even a sync that finishes instantly visibly reacts to the click.
    private DateTime syncFeedbackUntil;

    // The same idea for the upload log's copy button and its "Copied" flash.
    private DateTime logCopyFeedbackUntil;

    // Whether this session's automatic token probe has been issued (or found unnecessary). One
    // per session: the Account panel should greet the user with the token's real state, not
    // re-verify on every draw.
    private bool sessionTokenProbeRequested;

    // While a BrandCard is open, the window-local X of its inner right edge — so card-aware
    // helpers (BrandSeparator) can size themselves to the card instead of the window. Null outside
    // any card.
    private float? activeCardInnerRight;

    /// <summary>Builds the window. It is not opened here — Dalamud forbids unprompted windows.</summary>
    public MainWindow(
        Configuration configuration,
        ApiClient apiClient,
        SyncManager syncManager,
        IReadOnlyList<ICollector> collectors,
        ISharedImmediateTexture mascotTexture,
        IFontAtlas fontAtlas,
        IFontHandle iconFont,
        float baseFontSizePx,
        string pluginVersion)
        // `: base(...)` calls the parent Window constructor first, passing the window's title. The
        // `###XIVShiniesMain` suffix is an ImGui trick: text before `###` is the visible title, and
        // the part from `###` on is a stable internal ID, so the visible title can change later
        // without ImGui treating it as a different window (and losing its saved position/size).
        : base($"{PluginMeta.DisplayName}###XIVShiniesMain")
    {
        this.configuration = configuration;
        this.syncManager = syncManager;
        this.collectors = collectors;
        this.mascotTexture = mascotTexture;
        this.iconFont = iconFont;
        this.pluginVersion = pluginVersion;
        verifier = new TokenVerifier(apiClient);
        uploadLogTable = new UploadLogTable(collectors);

        // A 1.3x build of the game's default font, for headings. The lambda runs during the atlas's
        // asynchronous build; pushes are guarded on Available until it finishes.
        headingFont = fontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk => tk.AddDalamudDefaultFont(baseFontSizePx * 1.3f, null)));

        // Bold Axis (the game's own UI face) at body size, for button labels.
        buttonFont = fontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Axis, baseFontSizePx) { Bold = true });

        // Sizing values are UNSCALED logical units: Dalamud's Window base multiplies Size and
        // SizeConstraints by the user's global UI scale itself, so pre-scaling them (for example
        // with ImGuiHelpers.ScaledVector2) double-scales — at high scales even the minimum can then
        // exceed the screen.
        //
        // The opening size is computed in OnOpen and applied under FirstUseEver: ImGui uses it only
        // when it has no remembered size for this window; after that the user's own resize wins and
        // persists. MinimumSize is a real design floor (a minimum clamps UP): the width matches the
        // preferred opening width, below which the header rows and the upload table stop being
        // worth reading.
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Start the token box from whatever is already saved, so reopening the wizard does not look
        // like the token vanished.
        tokenInput = configuration.Settings.Token;
    }

    /// <summary>Cancels any token probe still in flight and releases the owned fonts.</summary>
    /// <remarks>The icon font is borrowed from Dalamud and deliberately not disposed here.</remarks>
    public void Dispose()
    {
        verifier.Dispose();
        headingFont.Dispose();
        buttonFont.Dispose();
    }

    /// <summary>
    /// Widens this window's padding so content does not butt against the edges.
    /// </summary>
    /// <remarks>
    /// Style variables are a stack, like colors and fonts: pushed here (which runs just before the
    /// window is created for the frame, so Begin reads the value) and popped in <see cref="PostDraw"/>
    /// after it — scoping the padding to this window alone. The privacy card and any other child
    /// windows are created while the value is still pushed, so they inherit it too. Style values are
    /// physical pixels, hence the explicit scale.
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
    /// <para>
    /// Scale alone cannot decide this: at high UI scales the logical desktop shrinks, so a
    /// reasonable content size can be an unreasonable share of the screen. The preferred size is
    /// therefore capped against the display, with both sides in logical units — ImGui reports the
    /// display in physical pixels, dividing by the scale converts it, and Dalamud scales the result
    /// back up when applying <see cref="Window.Size"/>.
    /// </para>
    /// <para>
    /// Computed here rather than in the constructor because the display size comes from ImGui,
    /// which is only safely consulted inside its own frame — OnOpen runs before the frame's size
    /// conditionals are applied. Under <c>FirstUseEver</c> the value matters only when ImGui has no
    /// remembered size for this window; thereafter the user's resize always wins.
    /// </para>
    /// </remarks>
    public override void OnOpen()
    {
        var displayLogical = ImGui.GetIO().DisplaySize / ImGuiHelpers.GlobalScale;

        // The height preference is generous because the settings list is currently tall; when the
        // rows become more compact, lower the preference rather than raising the cap.
        Size = new Vector2(
            Math.Min(560f, displayLogical.X * 0.45f),
            Math.Min(620f, displayLogical.Y * 0.70f));
    }

    /// <summary>
    /// Called once per frame by the WindowSystem while the window is open, so everything here runs
    /// roughly sixty times a second.
    /// </summary>
    /// <remarks>
    /// That is the immediate-mode model: the UI is described afresh every frame rather than mutated.
    /// Keep this cheap, and never block: whatever thread draws the game's frames is the one running
    /// this, and stalling it stalls the game.
    /// </remarks>
    public override void Draw()
    {
        // A probe finished on a background thread; fold its answer in before drawing anything that
        // depends on it.
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

        // `using` on a conditional: Push() returns an IDisposable that pops the font, and `using`
        // accepts null (it just skips the dispose) — so while the atlas is still building, this
        // renders at body size instead of waiting.
        using (headingFont.Available ? headingFont.Push() : null)
        {
            ImGui.TextColored(Brand.Teal, PluginMeta.DisplayName);
            titleWidth = ImGui.CalcTextSize(PluginMeta.DisplayName).X;
        }

        if (rightAligned is not null)
        {
            ImGui.SameLine();
            Widgets.AlignRight(ImGui.CalcTextSize(rightAligned).X);

            // Normal text color: this note carries the wizard's step counter — how far through setup
            // the user is — which is information they are meant to read.
            ImGui.TextUnformatted(rightAligned);
        }

        // The underline is drawn by hand: ImGui has no "underlined text", but the window's draw
        // list accepts arbitrary shapes at absolute screen coordinates.
        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(
            start,
            start + new Vector2(titleWidth, 2f * ImGuiHelpers.GlobalScale),
            ImGui.GetColorU32(Brand.Teal),
            0f,
            ImDrawFlags.None);

        // Reserve the space the underline occupied, plus breathing room, so the next widget does
        // not draw over it — draw-list shapes do not advance ImGui's layout cursor by themselves.
        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
    }

    /// <summary>A teal section label over the standard branded divider.</summary>
    private void DrawSectionHeading(string text)
    {
        ImGui.TextColored(Brand.Teal, text);
        BrandSeparator();

        // Extra air beyond the separator's own, so the section body does not crowd the rule.
        ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
    }

    /// <summary>
    /// Binds this window's card wrap edge to <see cref="Widgets.BrandSeparator"/>: inside a
    /// <see cref="BrandCard"/> the rule spans the card's inner width, not the window's.
    /// </summary>
    private void BrandSeparator() => Widgets.BrandSeparator(activeCardInnerRight);

    /// <summary>
    /// A bordered content card with brand padding and an optional teal icon-and-title header.
    /// </summary>
    /// <remarks>
    /// Unlike a fixed-size child window, this measures its content: everything drawn inside the
    /// <c>using</c> goes into an ImGui group, and the border rectangle is drawn afterwards around
    /// whatever the group turned out to be — so cards whose content varies (the token panel's
    /// claimed-character list, say) need no height arithmetic. Text wrapping is constrained to the
    /// card's inner width for the scope's duration.
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

        // What activeCardInnerRight held before this card opened, restored on close — so a card
        // nested inside another card would hand the outer card its wrap edge back, instead of
        // silently clearing it. (Nothing nests today; this keeps the invariant honest anyway.)
        private readonly float? previousInnerRight;

        public CardScope(MainWindow owner, FontAwesomeIcon? icon, string? title)
        {
            this.owner = owner;
            padding = 16f * ImGuiHelpers.GlobalScale;
            topLeft = ImGui.GetCursorScreenPos();
            rightEdge = topLeft.X + ImGui.GetContentRegionAvail().X;

            // Step inside the padding, then group everything so the closing border can measure it.
            ImGui.SetCursorScreenPos(topLeft + new Vector2(padding, padding));
            ImGui.BeginGroup();

            // Wrapped text must break at the card's inner edge, not the window's.
            var innerRightLocal =
                ImGui.GetCursorPosX() + (rightEdge - padding - ImGui.GetCursorScreenPos().X);
            wrapScope = ImRaii.TextWrapPos(innerRightLocal);
            previousInnerRight = owner.activeCardInnerRight;
            owner.activeCardInnerRight = innerRightLocal;

            // The same header treatment as the cards that compose their own rows — one source
            // for the icon gap, the divider, and the spacing.
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

            // The border hugs whatever the group turned out to be, plus the padding.
            var bottom = ImGui.GetItemRectMax().Y + padding;
            ImGui.GetWindowDrawList().AddRect(
                topLeft,
                new Vector2(rightEdge, bottom),
                ImGui.GetColorU32(ImGuiCol.Border),
                0f,
                ImDrawFlags.None,
                1f * ImGuiHelpers.GlobalScale);

            // Walk the layout cursor past the border — draw-list shapes do not advance it.
            ImGui.SetCursorScreenPos(new Vector2(topLeft.X, bottom));
            ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
        }
    }

    /// <summary>Binds this window's bold font to <see cref="Widgets.BoldButton(IFontHandle, string)"/>.</summary>
    private bool BoldButton(string label) => Widgets.BoldButton(buttonFont, label);

    /// <summary>Binds this window's bold font to <see cref="Widgets.BoldButton(IFontHandle, string, Vector2)"/>.</summary>
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
    /// A red warning the user should act on: a triangle icon beside wrapped red text. Every red
    /// warning in the window goes through here, so the icon is part of what red means.
    /// </summary>
    /// <remarks>Drawn at <see cref="Widgets.InlineIconScale"/>, like every other inline status icon.</remarks>
    private void DrawWarning(string text) =>
        DrawIconedText(
            FontAwesomeIcon.ExclamationTriangle, Widgets.ErrorColor, text, Widgets.InlineIconScale);

    /// <summary>Binds this window's icon font and wrap edge to <see cref="Widgets.DrawIconedText"/>.</summary>
    private void DrawIconedText(FontAwesomeIcon icon, Vector4 color, string text, float iconScale = 1f) =>
        Widgets.DrawIconedText(iconFont, icon, color, text, activeCardInnerRight, iconScale);

    /// <summary>
    /// The left half of a custom card header row: teal icon and title. Used by the cards that
    /// right-align a control (a toggle, a button) on the same line — which BrandCard's built-in
    /// header has no slot for. AlignTextToFramePadding centers the text against that frame-tall
    /// neighbor. Close the row with <see cref="CloseCardHeader"/> after the trailing control.
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

    /// <summary>The privacy disclosure, framed as a bordered card so it reads as a deliberate statement.</summary>
    /// <remarks>
    /// This is the disclosure of what the plugin sends about the user's character, so it is exactly
    /// the text they must be able to read: it draws at full contrast, and the card's border is what
    /// sets it apart.
    /// </remarks>
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
    private void DrawChip(FontAwesomeIcon icon, string text, Vector4 color) =>
        Widgets.DrawChip(iconFont, icon, text, color);

    /// <summary>Binds this window's icon font to <see cref="Widgets.DrawHeaderRightChip"/>.</summary>
    private void DrawHeaderRightChip(FontAwesomeIcon icon, string text, Vector4 color, float rightEdgeScreenX) =>
        Widgets.DrawHeaderRightChip(iconFont, icon, text, color, rightEdgeScreenX);

    /// <summary>Binds this window's card wrap edge to <see cref="Widgets.DrawWrapped(string, Vector4, float?)"/>.</summary>
    private void DrawWrapped(string text, Vector4 color) =>
        Widgets.DrawWrapped(text, color, activeCardInnerRight);

    /// <summary>Binds this window's card wrap edge to <see cref="Widgets.DrawWrapped(string, ImGuiCol, float?)"/>.</summary>
    private void DrawWrapped(string text, ImGuiCol styleColor) =>
        Widgets.DrawWrapped(text, styleColor, activeCardInnerRight);
}
