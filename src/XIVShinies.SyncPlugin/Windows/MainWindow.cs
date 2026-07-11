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
// Dalamud's standard palette (DalamudRed, HealerGreen, …), designed against its own themes —
// preferred over hand-picked literals, which ignore the user's chosen style.
using Dalamud.Interface.Colors;
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
// Util.OpenLink — opens a URL in the user's browser (the masthead's community links).
using Dalamud.Utility;
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
internal sealed class MainWindow : Window, IDisposable
{
    /// <summary>The longest token string the input box will accept, comfortably above the real 47.</summary>
    private const int TokenInputCapacity = 128;

    // Colors come from Dalamud's own palette (and, for muted text, from the active style via
    // ImGui.TextDisabled) rather than hardcoded literals: the user picks a Dalamud theme, including
    // light ones, and a hand-picked gray that reads fine on dark is mush on light.
    private static readonly Vector4 ErrorColor = ImGuiColors.DalamudRed;
    private static readonly Vector4 SuccessColor = ImGuiColors.HealerGreen;

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

    // Category key → the display name its collector declared, for the upload log. Built once in
    // the constructor: the collector list is fixed for the plugin's lifetime.
    private readonly Dictionary<string, string> categoryNames = new();

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

        foreach (var collector in collectors)
            categoryNames[collector.CategoryKey] = collector.DisplayName;

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

    // --- Wizard ----------------------------------------------------------------------------

    private void DrawWizard()
    {
        // The branded header carries "Step 1 of 3" — without it the wizard's length is unknowable.
        // The numbers come from the enum's positions, so a new step renumbers this automatically.
        var stepCount = (int)OnboardingStep.Done;
        var stepNumber = (int)onboarding.Step + 1;
        DrawBrandTitle(stepNumber <= stepCount ? $"Step {stepNumber} of {stepCount}" : null);

        switch (onboarding.Step)
        {
            case OnboardingStep.Welcome:
                DrawWelcomeStep();
                break;

            case OnboardingStep.LinkAccount:
                DrawLinkAccountStep();
                break;

            case OnboardingStep.ChooseCategories:
                DrawChooseCategoriesStep();
                break;

            // Reaching Done means Finish already ran and flipped OnboardingComplete, so the next
            // frame draws the settings instead. Nothing to render.
            default:
                break;
        }
    }

    private void DrawWelcomeStep()
    {
        // The website's name is picked out in the brand gold mid-sentence, which TextWrapped cannot
        // do — hence the span helper.
        DrawWrappedSpans(
            ($"{PluginMeta.DisplayName} reads what you have collected in game and uploads it to", null),
            ("xiv-shinies.com,", Brand.Gold),
            ("so the website knows what you own without you ticking it off by hand.", null));

        SectionGap();
        DrawSectionHeading("What it sends");

        // The description's left edge lines up with the name's, not the gem's: measure the icon
        // column (glyph plus the spacing SameLine inserts) and indent by exactly that.
        float iconColumn;
        using (iconFont.Push())
        {
            iconColumn = ImGui.CalcTextSize(FontAwesomeIcon.Gem.ToIconString()).X
                + ImGui.GetStyle().ItemSpacing.X;
        }

        // Each collector describes itself. Adding a collection makes it appear here with no change
        // to this window: a gold gem, the name at full brightness, and the collector's own
        // plain-language disclosure beneath it.
        foreach (var collector in collectors)
        {
            DrawIcon(FontAwesomeIcon.Gem, Brand.Gold);
            ImGui.SameLine();
            ImGui.TextUnformatted(collector.DisplayName);

            ImGui.Indent(iconColumn);
            DrawWrapped(collector.WhatGetsSent, ImGuiCol.TextDisabled);
            ImGui.Unindent(iconColumn);
        }

        SectionGap();
        DrawPrivacyCard(
            "Your character is identified by a one-way fingerprint computed on this machine. " +
            "Your character's name and home world are sent so xiv-shinies.com can match the " +
            "character you already claimed. Nothing is uploaded until you finish this setup, " +
            "and you choose which of the above to include.");

        DrawWizardNav("Get started");
    }

    // --- Brand drawing helpers ---------------------------------------------------------------

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
            AlignRight(ImGui.CalcTextSize(rightAligned).X);
            ImGui.TextDisabled(rightAligned);
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

    /// <summary>A consistent vertical gap between sections — one place to tune the page rhythm.</summary>
    private static void SectionGap() => ImGui.Dummy(new Vector2(0f, 10f * ImGuiHelpers.GlobalScale));

    /// <summary>
    /// Moves the cursor so a widget of the given width ends at the right edge — the current
    /// line's content edge by default, or an explicit edge such as a card's inner right.
    /// </summary>
    /// <remarks>
    /// Never moves the cursor LEFT: on a window too narrow for the widget, the cursor stays put
    /// and the row simply runs long, instead of the widget overlapping whatever was already
    /// drawn on the line. The window's minimum size makes that case rare; this makes it safe.
    /// </remarks>
    private static void AlignRight(float width, float? rightEdge = null)
    {
        var edge = rightEdge ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), edge - width));
    }

    /// <summary>A branded divider: a 2px teal rule instead of ImGui's gray separator.</summary>
    /// <remarks>Inside a <see cref="BrandCard"/> it spans the card's inner width, not the window's.</remarks>
    private void BrandSeparator()
    {
        var start = ImGui.GetCursorScreenPos();
        var width = activeCardInnerRight is { } innerRight
            ? innerRight - ImGui.GetCursorPosX()
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

    /// <summary>
    /// A secondary button: bold label, theme colors at rest, and a soft teal tint on hover — every
    /// non-primary button in this window goes through here.
    /// </summary>
    /// <remarks>
    /// The hover override exists because the resting look should follow the user's theme, but many
    /// themes hover buttons red — and red on a harmless button reads as "this will do something
    /// bad". The tint is translucent, so it layers over any theme rather than fighting it.
    /// </remarks>
    private bool BoldButton(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Brand.SecondaryHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Brand.SecondaryActive))
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.Button(label);
        }
    }

    /// <summary>A secondary button with an explicit size.</summary>
    private bool BoldButton(string label, Vector2 size)
    {
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Brand.SecondaryHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Brand.SecondaryActive))
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.Button(label, size);
        }
    }

    /// <summary>
    /// The branded primary button: near-black bold text on the bright teal, the site's own
    /// primary-button pattern. One per screen — it marks the action that moves the user forward.
    /// </summary>
    /// <remarks>
    /// Deliberately not built on <see cref="BoldButton"/>: color pushes are a stack where the most
    /// recent push wins, so an inner secondary-hover push would override the primary's hover.
    /// </remarks>
    private bool PrimaryButton(string label, Vector2 size)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, Brand.Teal))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Brand.TealHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Brand.TealDark))
        using (ImRaii.PushColor(ImGuiCol.Text, Brand.TealForeground))
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.Button(label, size);
        }
    }

    /// <summary>
    /// The width for a comfortably padded button: the label (measured in the bold button font)
    /// plus generous side padding. ImGui's default button width hugs the text.
    /// </summary>
    private float PaddedButtonWidth(string label)
    {
        using (buttonFont.Available ? buttonFont.Push() : null)
        {
            return ImGui.CalcTextSize(label).X + (28f * ImGuiHelpers.GlobalScale);
        }
    }

    /// <summary>The width of <see cref="BrandToggle"/>, for layout arithmetic.</summary>
    private static float ToggleWidth => ImGui.GetFrameHeight() * 1.55f;

    /// <summary>
    /// A sliding on/off switch whose hover color previews the click's outcome: green when
    /// clicking would turn it on, red when clicking would turn it off. The knob stays white.
    /// </summary>
    /// <remarks>
    /// Drawn by hand (an invisible button for the interaction, draw-list shapes for the look)
    /// rather than using Dalamud's ToggleButton, whose colors come from the active theme with no
    /// way to override the hover per state. At rest the track is brand teal when on and gray
    /// when off. Returns true on the frame the value flips, like ImGui.Checkbox.
    /// </remarks>
    private static bool BrandToggle(string id, ref bool value)
    {
        var topLeft = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var height = ImGui.GetFrameHeight();
        var width = ToggleWidth;
        var radius = height * 0.5f;

        // The invisible button supplies hit-testing and hover state; everything visible below is
        // drawn over it by hand.
        ImGui.InvisibleButton(id, new Vector2(width, height));
        var pressed = ImGui.IsItemClicked();
        if (pressed)
            value = !value;

        var track = ImGui.IsItemHovered()
            ? (value ? ErrorColor : SuccessColor)
            : (value ? Brand.TealDark : new Vector4(0.35f, 0.35f, 0.35f, 1f));

        // A fully rounded rectangle (rounding = half the height) reads as a pill-shaped track.
        drawList.AddRectFilled(
            topLeft,
            topLeft + new Vector2(width, height),
            ImGui.GetColorU32(track),
            radius,
            ImDrawFlags.None);

        // The knob sits at the end matching the state, inset slightly from the track's edge.
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
    /// Draws an icon-plus-text pair centered over an already-drawn button whose visible label is
    /// blank — transient in-button feedback like "Copied" or "Syncing".
    /// </summary>
    /// <remarks>
    /// A button label lives in a single font, and this needs two: the icon font for the glyph and
    /// the bold font for the word. Text items are not interactive, so overlapping the button is
    /// harmless. The layout cursor is teleported onto the button, the two pieces drawn, and the
    /// cursor restored so normal layout resumes.
    /// </remarks>
    private void DrawButtonFeedback(
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

        // A negative wrap position means "never wrap" — a button's face is a single line, and
        // without this a card's wrap scope would fold a right-aligned button's label at the
        // card edge running through it.
        using (ImRaii.TextWrapPos(-1f))
        {
            DrawIcon(icon, iconColor);
            ImGui.SameLine(0f, spacing);
            using (buttonFont.Available ? buttonFont.Push() : null)
                ImGui.TextColored(textColor, text);
        }

        ImGui.SetCursorPos(resume);
    }

    /// <summary>
    /// A red warning the user should act on: a triangle icon beside wrapped red text. Every red
    /// warning in the window goes through here, so the icon is part of what red means.
    /// </summary>
    private void DrawWarning(string text)
    {
        DrawIcon(FontAwesomeIcon.ExclamationTriangle, ErrorColor);
        ImGui.SameLine();

        // Grouping makes the text one block beside the icon: wrapped lines come back to the
        // block's left edge instead of sliding under the triangle.
        using (ImRaii.Group())
        {
            DrawWrapped(text, ErrorColor);
        }
    }

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

    /// <summary>Draws one FontAwesome glyph in the given color.</summary>
    private void DrawIcon(FontAwesomeIcon icon, Vector4 color)
    {
        // The icon font maps FontAwesome codepoints to glyphs; ToIconString yields the codepoint as
        // a string. Outside this push the same string would render as an empty box.
        using (iconFont.Push())
        {
            ImGui.TextColored(color, icon.ToIconString());
        }
    }

    /// <summary>The privacy disclosure, framed as a bordered card so it reads as deliberate fine print.</summary>
    private void DrawPrivacyCard(string text)
    {
        using (BrandCard(FontAwesomeIcon.Shield, "Your privacy"))
        {
            DrawWrapped(text, ImGuiCol.TextDisabled);
        }
    }

    private void DrawLinkAccountStep()
    {
        DrawTokenPanel();
        DrawWizardNav("Continue");
    }

    /// <summary>
    /// The token box, its Verify button, and the resulting feedback.
    /// </summary>
    /// <remarks>
    /// Shared by the wizard and the settings screen. Without it in the settings, a user whose token is
    /// revoked would be told to "generate a new one" with nowhere to paste it, and the wizard never
    /// reappears once onboarding is complete — the plugin would be permanently stuck.
    /// </remarks>
    private void DrawTokenPanel()
    {
        using (BrandCard())
        {
            // Custom card header: title left; right-aligned, a button that opens the browser
            // straight to the profile settings on the website, where tokens are created.
            DrawCardTitle(FontAwesomeIcon.Key, "Your account");

            const string openLabel = "Open profile settings";
            var openWidth = IconButtonWidth(FontAwesomeIcon.Globe, openLabel);
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            AlignRight(openWidth, innerRight);
            var openButtonPos = ImGui.GetCursorPos();

            if (BoldButton("###openProfile", new Vector2(openWidth, 0f)))
                Util.OpenLink($"{BackendUrl.Default}/profile");

            DrawButtonFeedback(
                openButtonPos, openWidth, FontAwesomeIcon.Globe, Brand.Teal,
                openLabel, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);

            CloseCardHeader();

            DrawWrapped(
                "Create a plugin token in your profile settings on xiv-shinies.com, then paste " +
                "it below. The token is shown once and can be revoked at any time.",
                ImGuiCol.Text);

            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));

            // Size the input to the space the card has left after the Verify button — clamped on
            // both ends: a token is a fixed ~47 characters, so on a very wide window a full-width
            // box would be absurd, and on a degenerately narrow one the arithmetic could go
            // negative (which ImGui reads as "size from the right edge", not zero) — the floor
            // keeps the box a box. Logical units, scaled like everything else.
            var verifyWidth = PaddedButtonWidth("Verify");

            var rightEdge = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            var available =
                rightEdge - ImGui.GetCursorPosX() - verifyWidth - ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(Math.Clamp(
                available, 120f * ImGuiHelpers.GlobalScale, 420f * ImGuiHelpers.GlobalScale));

            // ImGuiInputTextFlags.Password masks the text (ImGui draws asterisks). It is a credential,
            // and a plugin window is frequently on screen while streaming.
            if (ImGui.InputText("##token", ref tokenInput, TokenInputCapacity, ImGuiInputTextFlags.Password))
            {
                // Any edit invalidates the previous verification. Forget() also bumps the verifier's
                // generation, so an answer already in flight for the OLD token is discarded when it
                // lands rather than being reported against the text now in the box.
                onboarding.NotifyTokenEdited();
                verifier.Forget();
                account = null;
            }

            ImGui.SameLine();

            // The token is saved before the probe, because the API client reads it straight from the
            // settings rather than being handed it. Persisting an unverified token is harmless:
            // nothing uploads until onboarding is complete, and the master switch gates it thereafter.
            var canVerify = !verifier.InFlight && TokenFormat.IsWellFormed(tokenInput);

            // `using` guarantees the matching EndDisabled even if something inside throws — the same
            // job a `finally` would do, without the ceremony. An unbalanced disabled stack grays out
            // everything drawn after it for the rest of the frame.
            bool verifyPressed;
            using (ImRaii.Disabled(!canVerify))
                verifyPressed = BoldButton("Verify", new Vector2(verifyWidth, 0f));

            if (verifyPressed)
            {
                configuration.Settings.Token = tokenInput;
                configuration.Save();

                onboarding.BeginTokenCheck();
                verifier.Start();
            }

            DrawTokenCheckFeedback();
        }
    }

    /// <summary>Says what the token box should be telling the user, in a color that matches.</summary>
    /// <remarks>
    /// Which message applies is decided by <see cref="TokenFeedback"/>, which is pure and tested. All
    /// that is left here is turning a kind into pixels.
    /// </remarks>
    private void DrawTokenCheckFeedback()
    {
        ImGui.Spacing();

        switch (TokenFeedback.For(onboarding.TokenCheck, tokenInput))
        {
            case TokenFeedbackKind.Empty:
                ImGui.TextDisabled("Paste your token to continue.");
                break;

            case TokenFeedbackKind.Malformed:
                ImGui.TextDisabled(
                    $"That does not look like a token. They begin with {TokenFormat.Prefix}.");
                break;

            case TokenFeedbackKind.ReadyToVerify:
                ImGui.TextDisabled("Select Verify to check this token.");
                break;

            case TokenFeedbackKind.Checking:
                ImGui.TextDisabled("Checking with xiv-shinies.com…");
                break;

            case TokenFeedbackKind.Accepted:
                DrawIcon(FontAwesomeIcon.Check, SuccessColor);
                ImGui.SameLine();
                ImGui.TextColored(SuccessColor, "Token accepted");
                ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
                DrawClaimedCharacters();
                break;

            case TokenFeedbackKind.Rejected:
                DrawWarning("That token was not recognized. Generate a new one and paste it here.");
                break;

            case TokenFeedbackKind.Unreachable:
                DrawWarning("Could not reach xiv-shinies.com. Your token may be fine — try again.");
                break;
        }
    }

    /// <summary>
    /// Lists the characters this account has claimed, so the user can see the plugin will have
    /// somewhere to put the data before they finish setup.
    /// </summary>
    private void DrawClaimedCharacters()
    {
        if (account is null)
            return;

        if (account.Characters.Count == 0)
        {
            DrawWarning(
                "This account has not claimed any characters yet. Claim your character on " +
                "xiv-shinies.com first, or uploads will be refused.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Claimed characters:");

        foreach (var character in account.Characters)
        {
            // A person glyph rather than ImGui.Bullet — the round bullet reads like a radio button
            // next to this window's checkboxes.
            DrawIcon(FontAwesomeIcon.User, Brand.Teal);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{character.Name} ({character.World})");
        }

        // The list is a snapshot from the last probe, and nothing about it says so — a user who
        // just claimed an alt on the website would otherwise stare at a list that looks live and
        // never guess that Verify doubles as refresh.
        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
        DrawWrapped(
            "Claimed a new character on xiv-shinies.com? Press Verify to refresh this list.",
            ImGuiCol.TextDisabled);
    }

    private void DrawChooseCategoriesStep()
    {
        ImGui.TextWrapped(
            "Choose what to upload. Everything starts switched off — nothing is sent unless you " +
            "turn it on here. You can change any of this later.");

        SectionGap();
        DrawCategoryRows();

        ImGui.Spacing();
        DrawWizardNav("Finish");
    }

    /// <summary>Draws Back and the step's forward button, disabling the latter when the step forbids it.</summary>
    /// <remarks>
    /// Classic wizard footer: Back sits quietly on the left in the default style; the forward button
    /// is the branded primary, right-aligned — the strongest visual weight on the one action that
    /// moves the user forward.
    /// </remarks>
    private void DrawWizardNav(string forwardLabel)
    {
        // The footer gets more air than the sections above it: the primary action should sit
        // apart from the content, not crowd the last paragraph.
        SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Wide enough to feel like a primary action even for short labels, and grows with long ones.
        // Back uses the same size, so the footer's two buttons read as a matched pair.
        var buttonSize = new Vector2(
            Math.Max(120f * ImGuiHelpers.GlobalScale,
                ImGui.CalcTextSize(forwardLabel).X + (40f * ImGuiHelpers.GlobalScale)),
            0f);

        if (onboarding.CanGoBack)
        {
            if (BoldButton("Back", buttonSize))
                onboarding.Back();

            ImGui.SameLine();
        }

        // Right-align: the forward button's right edge meets the content region's.
        AlignRight(buttonSize.X);

        bool forwardPressed;
        using (ImRaii.Disabled(!onboarding.CanAdvance))
        {
            forwardPressed = PrimaryButton(forwardLabel, buttonSize);
        }

        if (forwardPressed)
        {
            onboarding.Advance();

            // Finish is a no-op until the last step, so calling it unconditionally is safe: it is the
            // state machine, not this window, that decides when consent has been given.
            onboarding.Finish(configuration.Settings);

            if (configuration.Settings.OnboardingComplete)
                configuration.Save();
        }
    }

    // --- Settings --------------------------------------------------------------------------

    private void DrawSettings()
    {
        DrawSettingsHeader();

        SectionGap();
        DrawSyncCard();

        SectionGap();
        DrawCategoryRows(FontAwesomeIcon.Gem, "Collections to include");

        SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Collapsed by default: replacing a token is rare, and the box should not invite fiddling.
        // But it must exist — a revoked token otherwise leaves the plugin permanently stuck, since the
        // wizard never returns once onboarding is complete.
        if (ImGui.CollapsingHeader("Account"))
        {
            TryAutoVerifyToken();

            ImGui.Spacing();
            DrawTokenPanel();
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // The privacy disclosure from the wizard, permanently reachable: consent context should
        // not vanish the moment onboarding completes. Same card, with the last sentence phrased
        // for a configured plugin rather than a wizard mid-setup.
        if (ImGui.CollapsingHeader("Privacy"))
        {
            ImGui.Spacing();
            DrawPrivacyCard(
                "Your character is identified by a one-way fingerprint computed on this machine. " +
                "Your character's name and home world are sent so xiv-shinies.com can match the " +
                "character you already claimed. Nothing is uploaded unless syncing is switched " +
                "on, and you choose which collections to include.");
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // The privacy card's receipts: what actually went out, per upload. Lives at the very
        // bottom because it is a reference surface, not a control.
        if (ImGui.CollapsingHeader("Recent uploads"))
        {
            ImGui.Spacing();
            DrawUploadLog();
        }
    }

    /// <summary>
    /// The recent uploads, newest first: when, what triggered each, what was sent per category,
    /// and how the server answered.
    /// </summary>
    /// <remarks>
    /// Rendered entirely from <see cref="UploadLogEntry"/> data plus the collectors' own display
    /// names — no category-name branches, so a new collector appears here for free. The strings
    /// are built per frame, which is affordable only because the section draws while expanded;
    /// do not copy this pattern into an always-visible path.
    /// </remarks>
    private void DrawUploadLog()
    {
        var history = syncManager.UploadHistory;

        using (BrandCard())
        {
            // Custom card header: title left, the copy button right-aligned on the same row.
            // "Recent" is honest labeling — the log is in-memory and bounded, so it never claims
            // to be a full history.
            DrawCardTitle(FontAwesomeIcon.History, "Recent uploads");

            // Two right-aligned header controls: Clear wipes the in-memory log, Copy log puts a
            // wire-term plain-text dump on the clipboard for bug reports. Both dim while there is
            // nothing to act on. Buttons are drawn first and their faces after — a face's text is
            // its own item, and SameLine between the buttons must anchor on a real button
            // rectangle, not on an overlay (the same two-pass trick as the masthead links).
            const string clearLabel = "Clear";
            const string copyLabel = "Copy log";
            var clearWidth = IconButtonWidth(FontAwesomeIcon.Trash, clearLabel);
            var copyWidth = IconButtonWidth(FontAwesomeIcon.Copy, copyLabel);
            var gap = 8f * ImGuiHelpers.GlobalScale;
            var showCopied = DateTime.UtcNow < logCopyFeedbackUntil;
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            AlignRight(clearWidth + gap + copyWidth, innerRight);

            bool clearPressed;
            bool copyPressed;
            using (ImRaii.Disabled(history.Count == 0))
            {
                var clearButtonPos = ImGui.GetCursorPos();
                clearPressed = BoldButton("###clearLog", new Vector2(clearWidth, 0f));

                ImGui.SameLine(0f, gap);
                var copyButtonPos = ImGui.GetCursorPos();
                copyPressed = BoldButton("###copyLog", new Vector2(copyWidth, 0f));

                // The faces. The red trash glyph marks Clear as the destructive one of the pair.
                var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
                DrawButtonFeedback(
                    clearButtonPos, clearWidth, FontAwesomeIcon.Trash, ErrorColor,
                    clearLabel, textColor);

                if (showCopied)
                {
                    DrawButtonFeedback(
                        copyButtonPos, copyWidth, FontAwesomeIcon.Check, SuccessColor,
                        "Copied", textColor);
                }
                else
                {
                    DrawButtonFeedback(
                        copyButtonPos, copyWidth, FontAwesomeIcon.Copy, Brand.Teal,
                        copyLabel, textColor);
                }
            }

            // No confirmation on Clear on purpose: the log is a bounded, in-memory convenience
            // that repopulates with the next upload — nothing of consequence is lost.
            if (clearPressed)
                syncManager.ClearUploadHistory();

            if (copyPressed && !showCopied)
            {
                // The dump names the EFFECTIVE backend (the user-overridable setting, not the
                // default): "you are pointed at the wrong server" is a classic support case.
                ImGui.SetClipboardText(UploadLogText.ClipboardText(
                    pluginVersion, configuration.Settings.BaseUrl, history));
                logCopyFeedbackUntil = DateTime.UtcNow.AddSeconds(1.5);
            }

            CloseCardHeader();

            DrawWrapped(
                "What this plugin sent recently. Kept in memory only — the log clears when the " +
                "plugin unloads.",
                ImGuiCol.TextDisabled);
            ImGui.Spacing();

            if (history.Count == 0)
            {
                ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
                ImGui.TextDisabled("Nothing has been uploaded yet this session.");
                return;
            }

            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
            DrawUploadLogTable(history, innerRight);
        }
    }

    /// <summary>The upload log's table body: one row per upload, newest first.</summary>
    private void DrawUploadLogTable(IReadOnlyList<UploadLogEntry> history, float innerRight)
    {
        // Inside the table, wrapped text must break at each CELL's edge — which wrap position 0
        // already means, because tables clamp the content region per column. The card-wide wrap
        // limit would instead let one cell's text run underneath its neighbor, so it is
        // suspended for the table's duration and restored after.
        var cardWrapEdge = activeCardInnerRight;
        activeCardInnerRight = null;
        try
        {
            DrawUploadLogTableRows(history, innerRight);
        }
        finally
        {
            activeCardInnerRight = cardWrapEdge;
        }
    }

    private void DrawUploadLogTableRows(IReadOnlyList<UploadLogEntry> history, float innerRight)
    {
        // The metadata columns adapt to the room available: on a roomy table, When and Trigger
        // each render on one line; on a narrow one, the date stacks over the time and the
        // trigger stacks word by word. The stacking is explicit lines, never ImGui text
        // wrapping — a wrap would happily break inside "7/11/2026" or "login" the moment the
        // column got narrower than the word. Both candidate widths are measured from the rows
        // actually shown, so the columns are always exactly as wide as their widest line.
        // Seeded with the header labels: a column exactly as wide as "login" clips its own
        // "Trigger" heading.
        var whenOneLine = ImGui.CalcTextSize("When").X;
        var whenStacked = whenOneLine;
        var triggerOneLine = ImGui.CalcTextSize("Trigger").X;
        var triggerStacked = triggerOneLine;
        foreach (var entry in history)
        {
            // "d"/"t" are the short date and short time in the user's own locale.
            var at = entry.At.ToLocalTime();
            var date = at.ToString("d");
            var time = at.ToString("t");
            whenOneLine = Math.Max(whenOneLine, ImGui.CalcTextSize($"{date} {time}").X);
            whenStacked = Math.Max(
                whenStacked, Math.Max(ImGui.CalcTextSize(date).X, ImGui.CalcTextSize(time).X));

            var trigger = UploadLogText.TriggerText(entry.Trigger);
            triggerOneLine = Math.Max(triggerOneLine, ImGui.CalcTextSize(trigger).X);
            foreach (var word in trigger.Split(' '))
                triggerStacked = Math.Max(triggerStacked, ImGui.CalcTextSize(word).X);
        }

        // Compact when one-line metadata would leave too little room for Outcome and Sent.
        var tableWidth = innerRight - ImGui.GetCursorPosX();
        var compact =
            tableWidth < whenOneLine + triggerOneLine + (360f * ImGuiHelpers.GlobalScale);

        var whenWidth = (compact ? whenStacked : whenOneLine) + (2f * ImGuiHelpers.GlobalScale);
        var triggerWidth =
            (compact ? triggerStacked : triggerOneLine) + (2f * ImGuiHelpers.GlobalScale);

        // Roomier cells than ImGui's default: CellPadding is the gap between a cell's border and
        // its content, pushed as a style variable scoped to this table.
        using var cellPadding = ImRaii.PushStyle(
            ImGuiStyleVar.CellPadding,
            new Vector2(8f * ImGuiHelpers.GlobalScale, 6f * ImGuiHelpers.GlobalScale));

        // Fixed columns for the short facts, the stretchy remainder for per-category detail, row
        // striping for scanability. The explicit outer width stops the table at the card's inner
        // edge — left alone it would stretch to the window and punch through the card's padding.
        using var table = ImRaii.Table(
            "uploadLogTable",
            4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
            new Vector2(innerRight - ImGui.GetCursorPosX(), 0f));

        if (!table.Success)
            return;

        // Metadata columns fixed at their measured width (one-line or stacked, chosen above);
        // Outcome and Sent stretch over whatever remains. Sent gets the lion's share: Outcome's
        // usual content is one short word, and the long failure phrases can wrap — the
        // per-category counts are the dense part.
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, whenWidth);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthFixed, triggerWidth);
        ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Sent", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableHeadersRow();

        var muted = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

        for (var index = 0; index < history.Count; index++)
        {
            var entry = history[index];

            // Counts that differ from this category's last appearance in the log light up gold —
            // "something new arrived since the previous upload".
            var changed = UploadLogDiff.ChangedCategories(history, index);

            ImGui.TableNextRow();

            // Short date + short time in the user's own locale — one line, or the date stacked
            // over the time in compact mode. Explicit lines: neither piece can ever be broken.
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
            var triggerText = UploadLogText.TriggerText(entry.Trigger);
            if (compact)
            {
                foreach (var word in triggerText.Split(' '))
                    ImGui.TextDisabled(word);
            }
            else
            {
                ImGui.TextDisabled(triggerText);
            }

            // The outcome (plus retry/attempt qualifiers) in a color that matches it: green for
            // accepted, muted for self-healing deferrals, red for anything the user must look
            // at. A validation rejection's detail — the server saying which field it hated —
            // follows muted, so the row itself explains the failure.
            ImGui.TableNextColumn();
            var statusColor = UploadLogText.IsSuccess(entry.Status) ? SuccessColor
                : UploadLogText.IsDeferral(entry.Status)
                    ? muted
                    : ErrorColor;
            DrawWrapped(UploadLogText.OutcomeText(entry), statusColor);

            if (entry.Detail is { } detail)
                DrawWrapped(detail, ImGuiCol.TextDisabled);

            // Mixed colors inside one flowing line need the span helper, exactly like the
            // wizard intro's gold website name. A changed category says so in words as well as
            // gold: the color alone is subtle, and "the count is the same but the contents
            // differ" (an item traded for another) is invisible without it.
            ImGui.TableNextColumn();
            var sent = new List<(string Text, Vector4? Color)>(entry.Categories.Count * 2);
            foreach (var category in entry.Categories)
            {
                if (sent.Count > 0)
                    sent.Add(("·", muted));

                var isChanged = changed.Contains(category.Key);
                sent.Add((
                    $"{DisplayNameFor(category.Key)} {category.Count:N0}"
                    + (isChanged ? " (changed)" : string.Empty),
                    isChanged ? Brand.Gold : muted));
            }

            DrawWrappedSpans(sent.ToArray());

            if (entry.Skipped.Count > 0)
            {
                var skipped = new List<string>(entry.Skipped.Count);
                foreach (var key in entry.Skipped.Keys)
                    skipped.Add(DisplayNameFor(key));
                DrawWrapped($"Could not read: {string.Join(", ", skipped)}", ImGuiCol.TextDisabled);
            }
        }
    }

    /// <summary>The display name a category's collector declared, or the raw key as a fallback.</summary>
    private string DisplayNameFor(string categoryKey) =>
        categoryNames.TryGetValue(categoryKey, out var name) ? name : categoryKey;

    /// <summary>
    /// Issues one token probe per session when the Account panel first opens with a saved, usable
    /// token — so it greets the user with the token's real state and the claimed-characters list,
    /// instead of asking them to press Verify to learn what is already true.
    /// </summary>
    /// <remarks>
    /// Runs through the upload gate: a Verify press is direct user action, but this is automatic,
    /// so it must respect the same consent switches as every other unprompted request. The flag is
    /// deliberately not reset on failure — "could not reach xiv-shinies.com" plus the Verify
    /// button to retry by hand is the honest resting state, not a silent retry loop.
    /// </remarks>
    private void TryAutoVerifyToken()
    {
        if (sessionTokenProbeRequested || verifier.InFlight)
            return;

        // A probe already answered this session (the wizard's Verify, most likely) — nothing to do.
        if (account is not null)
        {
            sessionTokenProbeRequested = true;
            return;
        }

        if (!UploadGate.CanContactServer(configuration.Settings)
            || !TokenFormat.IsWellFormed(tokenInput))
        {
            return;
        }

        sessionTokenProbeRequested = true;
        onboarding.BeginTokenCheck();
        verifier.Start();
    }

    /// <summary>The settings masthead: the mascot beside the plugin's name and one-line pitch.</summary>
    private void DrawSettingsHeader()
    {
        // GetWrapOrEmpty never blocks: while the file is still loading it returns a transparent
        // placeholder for this frame and the real pixels once ready — safe to call every frame.
        var mascot = mascotTexture.GetWrapOrEmpty();
        // Sized to roughly match the header block beside it (title, punchline, and link row).
        var mascotSize = 72f * ImGuiHelpers.GlobalScale;

        ImGui.Image(mascot.Handle, new Vector2(mascotSize));
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);

        // Grouping the title and pitch makes them one block beside the image: wrapped lines come
        // back to the block's left edge instead of the window's, staying clear of the mascot.
        using (ImRaii.Group())
        {
            using (headingFont.Available ? headingFont.Push() : null)
            {
                ImGui.TextColored(Brand.Teal, PluginMeta.DisplayName);
            }

            // The version, muted and right-aligned on the title line — the same spot the wizard
            // puts its step counter. It is the first thing a bug report needs.
            var versionLabel = $"v{pluginVersion}";
            ImGui.SameLine();
            AlignRight(ImGui.CalcTextSize(versionLabel).X);
            ImGui.TextDisabled(versionLabel);

            // The manifest punchline, with the site picked out in gold like the wizard intro.
            var muted = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
            DrawWrappedSpans(
                ("Your collections, on", muted),
                ("xiv-shinies.com,", Brand.Gold),
                ("the moment you earn them.", muted));

            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
            DrawLinkButtons();
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
        BrandSeparator();
    }

    /// <summary>The community links under the masthead description.</summary>
    /// <remarks>
    /// The Dalamud icon font carries only FontAwesome's solid set — no brand logos — so each link
    /// gets a fitting generic glyph instead. A link whose URL constant is empty draws no button,
    /// so retiring one is a one-line change in <see cref="PluginMeta"/>.
    /// </remarks>
    private void DrawLinkButtons()
    {
        // Two passes: every button first, overlays second. An overlay's icon and word are their
        // own (non-interactive) items, and SameLine anchors off the LAST item drawn — chaining the
        // next button off an overlay would place it mid-button and stack the row onto itself. With
        // the buttons drawn back-to-back, SameLine chains off real button rectangles.
        var overlays =
            new List<(Vector2 Position, float Width, FontAwesomeIcon Icon, Vector4 IconColor, string Label)>();
        var first = true;

        // Taller vertical item spacing for the row's scope, so when the buttons wrap onto a
        // second line the lines do not sit shoulder to shoulder. Horizontal gaps are unaffected
        // (SameLine passes them explicitly).
        using var rowSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));

        foreach (var (icon, iconColor, label, id, url) in LinkButtons)
        {
            if (url.Length == 0)
                continue;

            // The button is blank (its icon and label need two fonts, drawn over it below) but
            // sized for both plus the usual padding. Buttons flow like words: each tries the
            // current line and wraps to the next when it will not fit, so a narrow window
            // stacks the row instead of overlapping it.
            var width = IconButtonWidth(icon, label);

            if (!first)
            {
                ImGui.SameLine(0f, 10f * ImGuiHelpers.GlobalScale);
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
            }

            var position = ImGui.GetCursorPos();
            if (BoldButton(id, new Vector2(width, 0f)))
                Util.OpenLink(url);

            overlays.Add((position, width, icon, iconColor, label));
            first = false;
        }

        foreach (var (position, width, icon, iconColor, label) in overlays)
        {
            DrawButtonFeedback(
                position, width, icon, iconColor,
                label, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        }
    }

    /// <summary>The width of an icon-plus-label overlay button: both pieces plus button padding.</summary>
    private float IconButtonWidth(FontAwesomeIcon icon, string label)
    {
        float iconWidth;
        using (iconFont.Push())
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;

        return PaddedButtonWidth(label) + iconWidth + (8f * ImGuiHelpers.GlobalScale);
    }

    /// <summary>Everything sync in one card: the master switch, current status, manual trigger.</summary>
    private void DrawSyncCard()
    {
        using (BrandCard())
        {
            // Custom card header: icon and title left, the master switch right-aligned on the
            // same row.
            DrawCardTitle(FontAwesomeIcon.Sync, "Sync my collections");

            // The master switch is a toggle rather than a checkbox on purpose: the category
            // checkboxes below select what to include, while this is an on/off power switch —
            // giving it a different shape keeps the two from reading as the same kind of control.
            var masterEnabled = configuration.Settings.MasterEnabled;
            var stateLabel = masterEnabled ? "ON" : "OFF";

            // Position the state label + toggle as one right-aligned cluster: the toggle's right
            // edge sits on the card's inner edge.
            var gap = 8f * ImGuiHelpers.GlobalScale;
            var innerRight = activeCardInnerRight
                ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

            ImGui.SameLine();
            AlignRight(ToggleWidth + gap + ImGui.CalcTextSize(stateLabel).X, innerRight);

            // The label echoes the switch state: teal when on, muted when off.
            if (masterEnabled)
                ImGui.TextColored(Brand.Teal, stateLabel);
            else
                ImGui.TextDisabled(stateLabel);

            ImGui.SameLine(0f, gap);
            if (BrandToggle("##masterToggle", ref masterEnabled))
            {
                configuration.Settings.MasterEnabled = masterEnabled;
                configuration.Save();
            }

            CloseCardHeader();
            DrawStatus();
        }
    }

    private void DrawStatus()
    {
        // Ordered by which fact overrides which. The master switch beats everything: while it is
        // off, reporting the last upload's outcome (with its "will try again") would be a lie — the
        // plugin will not try again until the switch comes back.
        if (!configuration.Settings.MasterEnabled)
        {
            // Red rather than muted: everything below this line is inert while the switch is off,
            // and a quiet gray reads as "resting" when the truth is "doing nothing at all".
            DrawWarning("Syncing is switched off.");
        }
        else if (syncManager.BlockedPendingUserAction)
        {
            // The 403 case names the character when one is loaded, because "claim Some Name" is
            // actionable and "your token may have been revoked, or…" is a shrug. The server echoes
            // name and world for exactly this purpose; the local identity is the same information.
            var claimTarget = syncManager.LastStatus == ApiStatus.CharacterNotClaimed
                && syncManager.CharacterName is { } name
                    ? $"Claim {name} on xiv-shinies.com, then press Sync now."
                    : "Your token may have been revoked, or this character is not claimed on the " +
                      "website. Fix it there, then press Sync now.";

            DrawWarning($"Syncing has stopped. {claimTarget}");
        }
        else if (!syncManager.HasCharacter)
        {
            ImGui.TextDisabled("Waiting for a character to finish logging in.");
        }
        else if (syncManager.LastStatus is { } status)
        {
            DrawLastStatus(status);
        }
        else
        {
            ImGui.TextDisabled("Nothing has been uploaded yet this session.");
        }

        // "When?" is half of what a status line is for: without it, a deliberately quiet stretch
        // (item acquisitions fire no event) is indistinguishable from a hang.
        if (syncManager.LastSyncedAt is { } syncedAt)
            ImGui.TextDisabled($"Last synced {TimeText.Ago(DateTimeOffset.UtcNow - syncedAt)}.");

        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));

        // Primary: within this card, syncing now is the action everything above leads to. Clicking
        // gives the same in-button feedback as the copy button, but tied to reality: the label
        // yields to "Syncing" while the upload is actually on the wire, with a short minimum so
        // even an instant sync visibly reacts. The `###` suffix keeps the button's identity stable
        // while its visible label changes.
        var syncWidth = PaddedButtonWidth("Sync now");
        var showSyncing = DateTime.UtcNow < syncFeedbackUntil || syncManager.UploadInFlight;
        var syncButtonPos = ImGui.GetCursorPos();

        if (PrimaryButton(showSyncing ? "###syncNow" : "Sync now###syncNow", new Vector2(syncWidth, 0f))
            && !showSyncing)
        {
            syncManager.RequestManualSync();
            syncFeedbackUntil = DateTime.UtcNow.AddSeconds(1.5);
        }

        if (showSyncing)
        {
            // Dark-on-teal like the button's own label; the glyph matches the card's sync icon.
            DrawButtonFeedback(
                syncButtonPos, syncWidth, FontAwesomeIcon.Sync, Brand.TealForeground,
                "Syncing", Brand.TealForeground);
        }

        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Sets the expectation for every collection at once, so no category's own description has
        // to explain the sync mechanism. Phrased by mechanism, not by category name: unlock-style
        // acquisitions announce themselves and upload within seconds, while anything the game fires
        // no event for (item possession, for instance) waits for the scheduled sweep. The cadence
        // is the live value — the server tunes it — never a hardcoded number.
        // Hidden while the master switch is off — every claim in it is false then, and the status
        // line just said so.
        if (configuration.Settings.MasterEnabled)
        {
            DrawWrapped(
                "New unlocks upload within seconds. Everything else syncs automatically every " +
                $"{TimeText.Interval(syncManager.FullSyncInterval)} — press Sync now to update " +
                "immediately.",
                ImGuiCol.TextDisabled);
        }
    }

    /// <summary>Renders the last upload's outcome. Switches on a status, never on a category.</summary>
    private void DrawLastStatus(ApiStatus status)
    {
        switch (status)
        {
            case ApiStatus.Ok:
                ImGui.TextColored(SuccessColor, "Your collections are up to date.");
                break;

            case ApiStatus.CharacterNotClaimed:
                DrawWarning(
                    syncManager.CharacterName is { } name
                        ? $"Claim {name} on xiv-shinies.com before it can sync."
                        : "Claim this character on xiv-shinies.com before it can sync.");
                break;

            case ApiStatus.InvalidToken:
                DrawWarning("Your token was rejected. Generate a new one.");
                break;

            case ApiStatus.RateLimited:
            case ApiStatus.SyncDisabled:
                ImGui.TextDisabled("Waiting before the next upload, as the server asked.");
                break;

            case ApiStatus.NetworkError:
                ImGui.TextDisabled("Could not reach xiv-shinies.com. Will try again.");
                break;

            default:
                ImGui.TextDisabled("The last upload did not succeed. Will try again.");
                break;
        }
    }

    /// <summary>
    /// Draws one checkbox per registered collector, shared by the wizard and the settings.
    /// </summary>
    /// <remarks>
    /// Contains no category names. Every label, description, and hint comes from the row the
    /// collector produced, which is what keeps "adding a collection is one new class" true.
    /// </remarks>
    private void DrawCategoryRows(FontAwesomeIcon? headerIcon = null, string? headerTitle = null)
    {
        var rows = CategorySettingsView.Build(
            collectors, configuration.Settings, syncManager.RemoteConfig, syncManager.LastSkipped);

        // ItemInnerSpacing is the gap ImGui puts between a checkbox's box and its label — wider
        // here so the labels get some air. Pushed as a style variable scoped to this card.
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemInnerSpacing,
                   new Vector2(9f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemInnerSpacing.Y)))
        using (BrandCard())
        {
            // The header is optional because this card is shared: the settings screen titles it,
            // while the wizard's step already introduces the list with its own copy.
            if (headerIcon is { } icon && headerTitle is not null)
            {
                DrawCardTitle(icon, headerTitle);
                CloseCardHeader();
            }
            // A checkbox's label starts after the box itself plus the inner spacing; indenting the
            // description by the same amount lines its left edge up with the label above it.
            // Measured inside the push so the description column moves with the label.
            var checkboxColumn = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
            DrawSelectAll(rows);
            BrandSeparator();
            ImGui.Spacing();

            foreach (var row in rows)
            {
                var enabled = row.UserEnabled;
                bool toggled;

                // The server switched this category off for everyone. Show it, disabled, with the
                // user's own preference intact underneath — flipping it back on later restores what
                // they chose.
                using (ImRaii.Disabled(!row.ServerEnabled))
                {
                    // Everything after `##` is hidden from the label but forms part of the widget's
                    // identity. ImGui derives a control's ID from its label text, so two collections
                    // that happened to choose the same DisplayName would share an ID and cross-wire
                    // their clicks. The category key is unique by construction, which makes this
                    // collision impossible rather than merely unlikely.
                    toggled = ImGui.Checkbox($"{row.DisplayName}##{row.Key}", ref enabled);
                }

                if (toggled)
                {
                    configuration.Settings.SetCategoryEnabled(row.Key, enabled);
                    configuration.Save();
                }

                ImGui.Indent(checkboxColumn);
                DrawWrapped(row.WhatGetsSent, ImGuiCol.TextDisabled);

                if (!row.ServerEnabled)
                    ImGui.TextDisabled("Temporarily switched off by XIV Shinies.");

                // The collector said why it could not read this category; the reason is turned into
                // advice without anyone here knowing which category it was.
                if (row.SkipReason is { } reason && CollectSkipReasons.Describe(reason) is { } hint)
                    DrawWarning(hint);

                ImGui.Unindent(checkboxColumn);
                ImGui.Spacing();
            }
        }
    }

    /// <summary>One checkbox that flips every collection at once.</summary>
    /// <remarks>
    /// Shown checked only when everything is on, so clicking it always does the obvious thing: from
    /// "all on" it turns everything off, from anything else it turns everything on. It never names a
    /// category — it iterates whatever rows exist. Server-disabled rows are left out of both the
    /// reading and the writing, so this control reaches exactly the checkboxes the user could click
    /// individually, no more.
    /// </remarks>
    private void DrawSelectAll(IReadOnlyList<CategorySettingsRow> rows)
    {
        var allEnabled = true;
        foreach (var row in rows)
        {
            if (row.ServerEnabled)
                allEnabled &= row.UserEnabled;
        }

        if (ImGui.Checkbox("All collections##selectAll", ref allEnabled))
        {
            foreach (var row in rows)
            {
                if (row.ServerEnabled)
                    configuration.Settings.SetCategoryEnabled(row.Key, allEnabled);
            }

            configuration.Save();
        }

        ImGui.Spacing();
    }

    /// <summary>Draws wrapped text in a color, without unbalancing anything.</summary>
    /// <remarks>
    /// Needed because <c>TextWrapped</c> has no colored variant and <c>TextColored</c> does not
    /// wrap — long category descriptions were running straight off the window edge. Pushing
    /// <c>ImGuiCol.Text</c> recolors whatever text is drawn inside the scope. The wrap position is
    /// pushed by hand rather than via <c>TextWrapped</c>, because TextWrapped always wraps at the
    /// window edge — inside a <see cref="BrandCard"/> the text must break at the card's inner edge
    /// instead. A wrap position of 0 means "the window edge", so outside a card this behaves
    /// exactly like TextWrapped.
    /// </remarks>
    private void DrawWrapped(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        using (ImRaii.TextWrapPos(activeCardInnerRight ?? 0f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>Draws wrapped text in one of the current style's own colors.</summary>
    private void DrawWrapped(string text, ImGuiCol styleColor)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(styleColor)))
        using (ImRaii.TextWrapPos(activeCardInnerRight ?? 0f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>
    /// A wrapped paragraph in which some spans carry their own color (null means the default text
    /// color). An ImGui text widget is a single colored block, so inline emphasis has no built-in —
    /// this flows the words by hand instead.
    /// </summary>
    /// <remarks>
    /// The same greedy wrap ImGui performs, done manually: each word goes on the current line if it
    /// fits and starts a new line otherwise, which lets the color change mid-paragraph. Words are
    /// always joined by a single space, so attach punctuation directly to a span's words rather
    /// than starting a span with it. Vertical item spacing is zeroed for the scope so the flowed
    /// lines sit as tightly as a real wrapped paragraph.
    /// </remarks>
    private static void DrawWrappedSpans(params (string Text, Vector4? Color)[] spans)
    {
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var first = true;

        // This flow is the ONLY wrapping authority: the negative wrap position turns ImGui's own
        // text wrapping off for the words. Without it, a surrounding wrap scope (a card's, say)
        // can character-wrap the tail of a word this flow already measured as fitting — the last
        // letters end up stacked under the word instead of the word moving to the next line.
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
                        // Tentatively continue the line, then wrap if the word will not fit —
                        // after SameLine, the available content region is what remains of the line.
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

    // --- Token probe -----------------------------------------------------------------------

    /// <summary>Folds a finished token probe into the wizard's state.</summary>
    private void ConsumeTokenProbe()
    {
        if (verifier.TakeResult() is not { } response)
            return;

        onboarding.RecordTokenCheck(response.Status);
        account = response.Value;
    }
}
