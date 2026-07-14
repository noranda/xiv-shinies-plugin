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

    /// <summary>
    /// The size every inline status icon draws at, as a fraction of the icon font's normal size (see
    /// <see cref="DrawIcon"/>'s <c>scale</c> parameter). "Inline" means an icon sitting directly beside
    /// a line of body text — a warning triangle, a source note's tone glyph — as opposed to a
    /// standalone feature icon like a card title's, which draws full size.
    /// </summary>
    /// <remarks>
    /// One constant, shared by every such icon, so that a warning triangle and a source note's glyph
    /// read as one family of small status icons rather than as two unrelated decorations. At full text
    /// size an icon visually outweighs the short line of text beside it, which is the opposite of what a
    /// status glyph is for.
    /// </remarks>
    private const float InlineIconScale = 0.82f;

    // Colors come from Dalamud's own palette (and, for muted text, from the active style via
    // ImGui.TextDisabled) rather than hardcoded literals: the user picks a Dalamud theme, including
    // light ones, and a hand-picked gray that reads fine on dark is mush on light.
    //
    // Muted (ImGuiCol.TextDisabled) is a low-contrast gray in Dalamud's default themes. It is for
    // incidental text only — relative timestamps, empty-state filler, captions over the value they
    // label, hints that restate a disabled control's state. Anything the user must read to decide
    // something or to understand what the plugin does, consent copy above all, draws at the normal
    // text color (ImGuiCol.Text).
    private static readonly Vector4 ErrorColor = ImGuiColors.DalamudRed;
    private static readonly Vector4 SuccessColor = ImGuiColors.HealerGreen;

    // Same rationale as ErrorColor/SuccessColor above: a Dalamud palette color rather than a
    // hand-picked yellow, so a cached (possibly-stale) source note still reads correctly against
    // whatever theme — including light ones — the user has chosen.
    private static readonly Vector4 CautionColor = ImGuiColors.DalamudYellow;

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
        // Frame-scoped: the answer must describe THIS frame's rows, not a frame whose rows are gone.
        wizardShowedGroups = false;

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
        // to this window: a gold gem, the name, and the collector's own plain-language disclosure
        // beneath it. The disclosure draws at the normal text color, never muted: it is the consent
        // copy telling the user what leaves their machine, so it has to be comfortably legible.
        foreach (var collector in collectors)
        {
            DrawIcon(FontAwesomeIcon.Gem, Brand.Gold);
            ImGui.SameLine();
            ImGui.TextUnformatted(collector.DisplayName);

            ImGui.Indent(iconColumn);
            DrawWrapped(collector.WhatGetsSent, ImGuiCol.Text);
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
    /// <para>
    /// Deliberately not built on <see cref="BoldButton"/>: color pushes are a stack where the most
    /// recent push wins, so an inner secondary-hover push would override the primary's hover.
    /// </para>
    /// <para>
    /// It owns its disabled state rather than leaving that to the caller, so the color and the behavior
    /// can never disagree: the face, its hover, and its active color all go to
    /// <see cref="Brand.DisabledSurface"/> (see there for why a flat slate rather than a dimmed teal), so
    /// nothing lights up under the cursor to contradict it, and <c>ImRaii.Disabled</c> swallows the press.
    /// </para>
    /// </remarks>
    /// <param name="label">The button's label.</param>
    /// <param name="size">Its size.</param>
    /// <param name="enabled">False to draw it dead and swallow the press — see the remarks.</param>
    private bool PrimaryButton(string label, Vector2 size, bool enabled = true)
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
    /// <remarks>Drawn at <see cref="InlineIconScale"/>, like every other inline status icon.</remarks>
    private void DrawWarning(string text) =>
        DrawIconedText(FontAwesomeIcon.ExclamationTriangle, ErrorColor, text, InlineIconScale);

    /// <summary>
    /// An icon beside wrapped text, both in the same color — the shared layout for every icon-led line
    /// of body copy in this window: <see cref="DrawWarning"/>'s red warnings, the green "Token accepted"
    /// line, and the "Reading from:" source notes. (A card header's icon and title share a color too,
    /// but that is a header row with a layout of its own — see <see cref="DrawCardTitle"/>.)
    /// </summary>
    /// <remarks>
    /// The icon and the text are two separate ImGui items on the same line (<c>SameLine</c> puts
    /// them side by side, the way flex-direction: row would). Without grouping them, a second
    /// wrapped line would start back at the window's left edge — underneath the icon — instead of
    /// lining up with the first line's text. <c>ImRaii.Group()</c> treats everything drawn inside
    /// it as one block for layout purposes, which is what keeps wrapped lines flush with the text
    /// above them rather than the icon beside it.
    /// </remarks>
    /// <param name="icon">The icon to draw.</param>
    /// <param name="color">The color shared by the icon and the text.</param>
    /// <param name="text">The wrapped text beside the icon.</param>
    /// <param name="iconScale">
    /// Forwarded to <see cref="DrawIcon"/> unchanged. The default of 1 (full size) suits a standalone
    /// icon+text line at normal weight; every status line passes <see cref="InlineIconScale"/>.
    /// </param>
    private void DrawIconedText(FontAwesomeIcon icon, Vector4 color, string text, float iconScale = 1f)
    {
        DrawIcon(icon, color, iconScale);
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            DrawWrapped(text, color);
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

    /// <summary>
    /// Draws one FontAwesome glyph in the given color, optionally shrunk relative to the
    /// surrounding line's text.
    /// </summary>
    /// <param name="icon">The glyph to draw.</param>
    /// <param name="color">The glyph's color.</param>
    /// <param name="scale">
    /// A fraction of the icon font's normal size (1 = unchanged, the default). Every inline status icon
    /// passes <see cref="InlineIconScale"/>; a standalone feature icon like a card title's leaves this
    /// at the default and renders full size.
    /// </param>
    /// <remarks>
    /// <para>
    /// At the default scale this is one
    /// <c>ImGui.TextColored</c> call inside the icon font push, which both paints the glyph and
    /// advances the layout cursor by one line's worth of space. Dalamud builds its icon font at the
    /// same size as the default UI font, which is why the glyph already lines up with body text
    /// beside it with no extra alignment work.
    /// </para>
    /// <para>
    /// A smaller scale reuses the footprint-reservation trick <see cref="DrawChip"/> uses for its
    /// hand-drawn badge: reserve the FULL, unscaled glyph's box with an invisible <c>Dummy</c> — so
    /// this call still advances the layout cursor exactly as far as the unscaled icon would, keeping
    /// whatever <c>ImGui.SameLine()</c> comes next lined up on the same row it would expect — then
    /// paint the smaller glyph directly onto the window's draw list, centered inside that reserved
    /// box. Painting through the draw list instead of a second <c>ImGui.Text</c> call means the
    /// smaller glyph itself never touches the layout cursor, so there is nothing for the mid-call
    /// font-scale change below to disturb.
    /// </para>
    /// <para>
    /// <c>ImGui.SetWindowFontScale</c> is WINDOW-global state — it is not scoped by the icon font
    /// push above it, so left set it would shrink every widget drawn afterwards in this window, not
    /// just this glyph. Every call below pairs it with a reset back to 1 immediately after the one
    /// measurement or draw call that needed it.
    /// </para>
    /// </remarks>
    private void DrawIcon(FontAwesomeIcon icon, Vector4 color, float scale = 1f)
    {
        // The icon font maps FontAwesome codepoints to glyphs; ToIconString yields the codepoint as
        // a string. Outside this push the same string would render as an empty box.
        using (iconFont.Push())
        {
            var glyph = icon.ToIconString();

            if (scale >= 1f)
            {
                ImGui.TextColored(color, glyph);
                return;
            }

            // The glyph's natural size at this font: the footprint an unscaled DrawIcon call would
            // have reserved, and so the footprint THIS call reserves too, regardless of scale — that
            // is what keeps the row's height, and SameLine's alignment, identical either way.
            var fullSize = ImGui.CalcTextSize(glyph);

            ImGui.SetWindowFontScale(scale);
            var scaledSize = ImGui.CalcTextSize(glyph);
            ImGui.SetWindowFontScale(1f);

            var topLeft = ImGui.GetCursorScreenPos();

            // Splits the size lost to scaling evenly on every side, which is what "centered" means
            // for a box shrinking around a fixed middle point — half the margin above and below,
            // half to the left and right, rather than the smaller glyph hugging the box's top-left
            // corner (where an unshifted draw would otherwise leave it).
            var offset = (fullSize - scaledSize) / 2f;

            ImGui.SetWindowFontScale(scale);
            ImGui.GetWindowDrawList().AddText(topLeft + offset, ImGui.GetColorU32(color), glyph);
            ImGui.SetWindowFontScale(1f);

            // Reserve the UNSCALED footprint — see the remarks above for why this has to match the
            // scale-1 path's Text call exactly, so nothing downstream can tell this icon was drawn
            // smaller.
            ImGui.Dummy(fullSize);
        }
    }

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

    private void DrawLinkAccountStep()
    {
        // A verified token is the first moment the server will answer this plugin at all, so it is when
        // the config — and with it the list of item groups the consent step must offer — is fetched.
        // Holding the user here until that answer lands is what makes the next step whole: it sees the
        // group checkboxes from its very first frame, so ticking a category can tick the groups that
        // belong to it, and no consent can be granted for a checkbox that was not on screen at the time.
        // A failed poll still answers, so this can never become a trap; it just leaves the next step
        // with no groups to show.
        onboarding.NotifyAwaitingConfig(
            onboarding.TokenCheck == TokenCheckState.Valid && syncManager.OnboardingConfigPending);

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

            // The button is busy for as long as anything it started is still outstanding: the token
            // probe itself, and — in the wizard — the config the accepted token goes on to fetch. Both
            // are the same wait as far as the user is concerned, and a button that went idle between
            // them would invite a second press for a request already on its way.
            var checkInFlight = verifier.InFlight || onboarding.AwaitingConfig;
            // Three periods rather than the single "…" glyph: the font centers that glyph vertically, so
            // it reads as a row of dots floating in the middle of the line instead of trailing the text.
            var verifyLabel = checkInFlight ? "Checking..." : "Verify";

            // Size the input to the space the card has left after the Verify button — clamped on
            // both ends: a token is a fixed ~47 characters, so on a very wide window a full-width
            // box would be absurd, and on a degenerately narrow one the arithmetic could go
            // negative (which ImGui reads as "size from the right edge", not zero) — the floor
            // keeps the box a box. Logical units, scaled like everything else.
            //
            // Measured against the widest label the button can wear, so the box beside it keeps its
            // size while the button changes what it says.
            var verifyWidth = Math.Max(PaddedButtonWidth("Verify"), PaddedButtonWidth("Checking..."));

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
            var canVerify = !checkInFlight && TokenFormat.IsWellFormed(tokenInput);

            // `using` guarantees the matching EndDisabled even if something inside throws — the same
            // job a `finally` would do, without the ceremony. An unbalanced disabled stack grays out
            // everything drawn after it for the rest of the frame.
            bool verifyPressed;
            using (ImRaii.Disabled(!canVerify))
                verifyPressed = BoldButton(verifyLabel, new Vector2(verifyWidth, 0f));

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

        // The token has been accepted and the wizard is now waiting on what the server asks about, with
        // Continue disabled until it arrives. A disabled button with no explanation beside it reads as a
        // broken wizard, so the wait says what it is waiting for.
        if (onboarding.AwaitingConfig)
        {
            ImGui.TextUnformatted("Token accepted. Asking XIV Shinies what it collects...");
            return;
        }

        // The four neutral states below draw at the normal text color: each one is either an
        // instruction ("paste", "select Verify") or the live state of a check the user is waiting
        // on, so all four are text they are meant to read. Only the two decided outcomes carry a
        // color, and that color is the message (green accepted, red rejected).
        switch (TokenFeedback.For(onboarding.TokenCheck, tokenInput))
        {
            case TokenFeedbackKind.Empty:
                ImGui.TextUnformatted("Paste your token to continue.");
                break;

            case TokenFeedbackKind.Malformed:
                ImGui.TextUnformatted(
                    $"That does not look like a token. They begin with {TokenFormat.Prefix}.");
                break;

            case TokenFeedbackKind.ReadyToVerify:
                ImGui.TextUnformatted("Select Verify to check this token.");
                break;

            case TokenFeedbackKind.Checking:
                ImGui.TextUnformatted("Checking with xiv-shinies.com...");
                break;

            case TokenFeedbackKind.Accepted:
                // The same icon-led line as the rejection below it, through the same helper: the two
                // alternate in the same spot of the same panel, and a check that sat differently from
                // the triangle it replaces on screen would read as a different kind of message.
                DrawIconedText(
                    FontAwesomeIcon.Check, SuccessColor, "Token accepted", InlineIconScale);
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

        // A caption over the list; the character names beneath it are what the user reads.
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
        // claimed an alt on the website after setup would otherwise stare at a list that looks live
        // and never guess that Verify doubles as refresh. During setup the probe just ran seconds
        // ago at the user's own press, so the list cannot be stale and the hint has nothing to say.
        if (!configuration.Settings.OnboardingComplete)
            return;

        ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
        DrawWrapped(
            "Claimed a new character on xiv-shinies.com? Press Verify to refresh this list.",
            ImGuiCol.Text);
    }

    private void DrawChooseCategoriesStep()
    {
        ImGui.TextWrapped(
            "Choose what to upload. Everything starts switched off — nothing is sent unless you " +
            "turn it on here. You can change any of this later.");

        SectionGap();

        // Everything this step will ever show is on screen from its first frame: the account step holds
        // the user until the server's config has answered (see DrawLinkAccountStep), so a category's
        // group checkboxes exist by the time its own checkbox can be ticked. That is what makes ticking
        // a category able to tick the groups it means, and it is why no consent here can ever be granted
        // for a checkbox the user was not looking at.
        //
        // No "New" badges: the manifest groups drawn beneath a category are all being shown for the
        // first time, to a user who installed the plugin minutes ago. DrawGroupCheckboxes still marks
        // every group it draws as seen, so the settings screen greets them badge-free afterwards.
        DrawCategoryRows(BuildCategoryRows(), showNewChips: false);

        ImGui.Spacing();
        DrawWizardNav("Finish");
    }

    /// <summary>
    /// The settings window's category rows, from the pure builder every consent and status surface in
    /// this window reads.
    /// </summary>
    /// <remarks>
    /// A cheap list build with no game calls in it, but it still allocates — so each frame builds the
    /// list ONCE and hands it to whichever surfaces need it, rather than each surface rebuilding it
    /// for itself sixty times a second on an always-visible path.
    /// </remarks>
    private IReadOnlyList<CategorySettingsRow> BuildCategoryRows() =>
        CategorySettingsView.Build(
            collectors, configuration.Settings, syncManager.RemoteConfig, syncManager.LastSkipped);

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

        // The forward button is the wizard's one live action, and the steps that hold it shut — an
        // unverified token, a config still being fetched — are the whole point of it being shut. It has
        // to LOOK shut, which is what PrimaryButton's own disabled treatment is for.
        var forwardPressed = PrimaryButton(forwardLabel, buttonSize, onboarding.CanAdvance);

        if (forwardPressed)
        {
            onboarding.Advance();

            // Finish is a no-op until the last step, so calling it unconditionally is safe: it is the
            // state machine, not this window, that decides when consent has been given.
            onboarding.Finish(configuration.Settings);

            if (configuration.Settings.OnboardingComplete)
            {
                // Settles the one-time migration flag for a user who chose their groups by hand, so that
                // migration can never later re-enable a group they deliberately left off. What settles it
                // is what the wizard DREW, tracked in wizardShowedGroups, never what the server sent: a
                // user shown no checkbox chose nothing, and the migration must stay free to speak for
                // them. See PluginSettings.SettleItemGroupConsent.
                configuration.Settings.SettleItemGroupConsent(wizardShowedGroups);

                // Unconditional: Finish has just written OnboardingComplete, and that has to reach disk
                // whether or not there was any group consent to settle alongside it.
                configuration.Save();
            }
        }
    }

    // --- Settings --------------------------------------------------------------------------

    private void DrawSettings()
    {
        // The three surfaces that need the category rows — the read-status panel inside the sync card,
        // the "New" chip on the Collections header, and the consent card itself — are all drawn from
        // THIS list (see BuildCategoryRows).
        var rows = BuildCategoryRows();

        DrawSettingsHeader();

        SectionGap();
        DrawSyncCard(rows);

        SectionGap();
        BrandSeparator();
        ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

        // Whether any manifest group anywhere in the list still counts as "New" (see AnyGroupIsNew).
        var hasNewGroup = AnyGroupIsNew(rows);

        // Captured immediately before the header so the "New" chip below can be placed on the
        // header's own row: CollapsingHeader always spans the full available width regardless of
        // whether it happens to sit inside a BrandCard, so this is the same cursor-space-to-inner-
        // right-edge fallback BrandSeparator and DrawSectionLabel use elsewhere in this file.
        var headerRowCursorX = ImGui.GetCursorPosX();
        var headerRowScreenX = ImGui.GetCursorScreenPos().X;
        var headerRowInnerRight =
            activeCardInnerRight ?? (headerRowCursorX + ImGui.GetContentRegionAvail().X);

        // The first of the settings screen's collapsible sections, and the only one that starts OPEN:
        // ImGuiTreeNodeFlags.DefaultOpen sets the header's initial state, after which ImGui remembers
        // whatever the user last chose. Consent is the one thing on this screen a user may want to
        // change at any moment, so it greets them expanded — but it is a long card, so it can be
        // folded away once they have made their choices.
        // "Collections", not the card's own longer title: the accordion headers name their section
        // briefly (Account, Privacy, Recent uploads) and the card inside carries the full heading.
        var collectionsOpen =
            ImGui.CollapsingHeader("Collections", ImGuiTreeNodeFlags.DefaultOpen);

        // Drawn in both states, open and collapsed (see AnyGroupIsNew). Positioned by DrawHeaderRightChip
        // rather than a plain SameLine(), because the header above just claimed the ENTIRE row width;
        // see that method's remarks for why SameLine cannot place a widget beside a full-width header.
        if (hasNewGroup)
        {
            DrawHeaderRightChip(
                FontAwesomeIcon.Star, "New", Brand.Gold,
                headerRowScreenX + (headerRowInnerRight - headerRowCursorX));
        }

        if (collectionsOpen)
        {
            ImGui.Spacing();
            DrawCategoryRows(rows, showNewChips: true, FontAwesomeIcon.Gem, "Collections to include");
        }

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

            // Normal text color: this sentence is what tells the user the log is a memory-only
            // record rather than a permanent one, so it is an explanation they need to read.
            DrawWrapped(
                "What this plugin sent recently. Kept in memory only — the log clears when the " +
                "plugin unloads.",
                ImGuiCol.Text);
            ImGui.Spacing();

            if (history.Count == 0)
            {
                // Muted: empty-state filler standing in for a table, with nothing in it to read.
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

        // The table's cells are data the user came here to read, so they draw in the normal text
        // color. Muted is used in exactly one place below — the "·" that separates the per-category
        // counts, which is punctuation rather than content.
        var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
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
                    ImGui.TextUnformatted(word);
            }
            else
            {
                ImGui.TextUnformatted(triggerText);
            }

            // The outcome (plus retry/attempt qualifiers) in a color that matches it: green for
            // accepted, red for anything the user must look at, and the normal text color for a
            // self-healing deferral — neither good news nor bad, but still the row's headline, so
            // it is not dimmed. A validation rejection's detail — the server saying which field it
            // hated — follows in the same normal color, so the row itself explains the failure.
            ImGui.TableNextColumn();
            var statusColor = UploadLogText.IsSuccess(entry.Status) ? SuccessColor
                : UploadLogText.IsDeferral(entry.Status)
                    ? textColor
                    : ErrorColor;
            DrawWrapped(UploadLogText.OutcomeText(entry), statusColor);

            if (entry.Detail is { } detail)
                DrawWrapped(detail, ImGuiCol.Text);

            // Mixed colors inside one flowing line need the span helper, exactly like the
            // wizard intro's gold website name. A null span color means the normal text color,
            // which is what the counts themselves use — they are the densest data in the table.
            // A changed category says so in words as well as gold: the color alone is subtle, and
            // "the count is the same but the contents differ" (an item traded for another) is
            // invisible without it. Only the separator between categories is muted.
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
                    isChanged ? Brand.Gold : null));
            }

            DrawWrappedSpans(sent.ToArray());

            if (entry.Skipped.Count > 0)
            {
                var skipped = new List<string>(entry.Skipped.Count);
                foreach (var key in entry.Skipped.Keys)
                    skipped.Add(DisplayNameFor(key));
                DrawWrapped($"Could not read: {string.Join(", ", skipped)}", ImGuiCol.Text);
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

            // The manifest punchline, with the site picked out in gold like the wizard intro. The
            // rest is a null span color, meaning the normal text color: it is the one sentence
            // that says what the plugin is for, so it reads at full contrast.
            DrawWrappedSpans(
                ("Your collections, on", null),
                ("xiv-shinies.com,", Brand.Gold),
                ("the moment you earn them.", null));

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
    /// <param name="rows">
    /// This frame's category rows, forwarded to the read-status panel at the bottom of the card (see
    /// <see cref="DrawStatus"/>). Passed in rather than rebuilt so the settings screen builds them
    /// exactly once per frame — see <see cref="DrawSettings"/>.
    /// </param>
    private void DrawSyncCard(IReadOnlyList<CategorySettingsRow> rows)
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
            DrawStatus(rows);
        }
    }

    /// <summary>
    /// The sync card's live status: what the last upload did, and what this session can currently read.
    /// </summary>
    /// <param name="rows">
    /// This frame's category rows, which the "Reading from:" panel turns into its collection lines.
    /// </param>
    private void DrawStatus(IReadOnlyList<CategorySettingsRow> rows)
    {
        // Ordered by which fact overrides which. The master switch beats everything: while it is
        // off, reporting the last upload's outcome (with its "will try again") would be a lie — the
        // plugin will not try again until the switch comes back.
        if (!configuration.Settings.MasterEnabled)
        {
            // Red: everything below this line is inert while the switch is off, and a quiet gray
            // would read as "resting" when the truth is "doing nothing at all".
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
            // Normal text color, like the colored states around it: this line IS the sync card's
            // status — the sentence the user came to read — even though it is neither good news nor
            // bad.
            ImGui.TextUnformatted("Waiting for a character to finish logging in.");
        }
        else if (syncManager.LastStatus is { } status)
        {
            DrawLastStatus(status);
        }
        else
        {
            ImGui.TextUnformatted("Nothing has been uploaded yet this session.");
        }

        // "When?" is half of what a status line is for: without it, a deliberately quiet stretch
        // (item acquisitions fire no event) is indistinguishable from a hang. Muted, unlike the
        // status line above it: a relative timestamp is a footnote to the status, not the status.
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

        // Both blocks below describe a pipeline that is actually running, so both are hidden when it
        // is not: while the master switch is off (everything in this card is inert), and while the
        // sync is halted for something only the user can fix (a bad token, an unclaimed character).
        // In the halted state the status line above is already telling them what to do, and a
        // cheerful cadence promise beneath it would simply be false. The source notes hide for a
        // second reason too: they carry a red "not scanned yet" tone, and a halted card is already
        // red — leaving them on would flatten "your sync is broken" and "one container is empty"
        // into the same alarm.
        var pipelineRunning =
            configuration.Settings.MasterEnabled && !syncManager.BlockedPendingUserAction;

        // Sets the expectation for every collection at once, so no category's own description has
        // to explain the sync mechanism. Phrased by mechanism, not by category name: unlock-style
        // acquisitions announce themselves and upload within seconds, while anything the game fires
        // no event for (item possession, for instance) waits for the scheduled sweep. The cadence
        // is the live value — the server tunes it — never a hardcoded number.
        if (pipelineRunning)
        {
            DrawWrapped(
                "New unlocks upload within seconds. Everything else syncs automatically every " +
                $"{TimeText.Interval(syncManager.FullSyncInterval)} — press Sync now to update " +
                "immediately.",
                ImGuiCol.Text);
        }

        // A snapshot of everything this sync reads: each collection the user has switched on (was it
        // readable this pass, or is the game withholding it?), and the physical storage containers
        // the item counts come from (inventory, saddlebag, armoire, and so on). This lives in the
        // sync card rather than beneath any one category's row, because it describes the pipeline as
        // a whole — and because storage containers are not category-scoped: a future collection that
        // also reads items would draw from this exact same set of containers.
        //
        // Gated on a pass having actually run for this character (see SyncManager.HasCollected).
        // Before then, no collection has a skip reason yet, so every enabled one would falsely
        // report as read.
        if (pipelineRunning && syncManager.HasCollected)
        {
            ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));

            // Normal text color, matching the notes it introduces: this heading is what makes the
            // list beneath it mean anything, and the notes themselves are colored by tone.
            ImGui.TextUnformatted("Reading from:");

            // The whole panel is assembled by a pure, tested builder, so this window stays a printer:
            // it draws whatever notes it is handed and never asks which collection or which container
            // produced one. The rows are this frame's, built once at the top of DrawSettings and
            // shared with the consent card below.
            var readStatus = ReadStatusView.Build(rows, syncManager.LastSourceNotes);

            // Whether any note drawn below is Missing — something contributing nothing at all right
            // now, whether a collection the game will not answer for or a storage container that has
            // never been opened. Tracked while drawing so the follow-up hint beneath the panel is
            // gated on the exact tones just shown, rather than re-deriving the same answer. The two
            // groups OR their answers together: the hint speaks for both.
            var hasMissingNote = DrawReadStatusGroup("Collections", readStatus.Collections);
            hasMissingNote |= DrawReadStatusGroup("Containers", readStatus.Containers);

            // Every Missing line names its own action — open the Saddlebag, open the Achievements
            // window — but none of them can say what happens next, because acting in game changes
            // nothing until the plugin reads again. This is the shared other half: the sync that
            // actually picks the change up. Hidden once nothing is Missing, since a permanently
            // Cached container has nothing left the user can do about it.
            if (hasMissingNote)
            {
                ImGui.Dummy(new Vector2(0f, 6f * ImGuiHelpers.GlobalScale));
                DrawWrapped(
                    "Anything above that has not been read yet names the action that fixes it — do " +
                    "it in game, then press Sync now.",
                    ImGuiCol.Text);
            }
        }
    }

    /// <summary>
    /// Draws a muted section label followed by a thin horizontal rule that fills the rest of the
    /// row, so the label reads as a section divider ("Label ────────") instead of a plain caption
    /// floating above the lines it introduces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ImGui has no built-in "label plus rule" widget, so the rule is hand-drawn on the window's
    /// <em>draw list</em> — a list of raw shapes attached to the window, separate from the layout
    /// cursor that ordinary widgets like <c>TextDisabled</c> advance. This is the same primitive
    /// <see cref="BrandSeparator"/> and <see cref="DrawChip"/> already use elsewhere in this file.
    /// Because draw-list calls never move the cursor themselves, the label's own <c>TextDisabled</c>
    /// call is what actually reserves layout space; the rule is simply painted over empty space to
    /// its right, and <c>NewLine</c> at the end restores normal top-to-bottom flow for whatever the
    /// caller draws next.
    /// </para>
    /// <para>
    /// The rule spans from just right of the label text to the row's right edge: the enclosing
    /// <see cref="BrandCard"/>'s inner right edge (<see cref="activeCardInnerRight"/>) when this is
    /// drawn inside one, or the window's own content edge otherwise — the same fallback
    /// <see cref="BrandSeparator"/> uses, so the rule can never overrun whichever container it is in.
    /// It is centered on the label's text line (half of <c>GetTextLineHeight</c> below the line's
    /// top, where the cursor sits) and colored from the active Dalamud theme's disabled-text color
    /// with its alpha thinned further, rather than a literal color, so it reads as a quiet divider
    /// beneath the label instead of competing with it — and still matches whatever theme the user
    /// has chosen.
    /// </para>
    /// </remarks>
    /// <param name="label">The section heading drawn before the rule.</param>
    private void DrawSectionLabel(string label)
    {
        // The muted heading itself. This is the ONLY call that advances the layout cursor; the rule
        // drawn below is pure decoration painted beside it, not a second widget.
        ImGui.TextDisabled(label);
        ImGui.SameLine();

        // GetCursorPosX() is in cursor-space (relative to the window's content region), the same
        // coordinate space activeCardInnerRight is stored in — see CardScope. Subtracting one from
        // the other gives a plain pixel width that stays correct no matter where on screen this row
        // ends up, which is why BrandSeparator computes its own width the same way.
        var afterLabelX = ImGui.GetCursorPosX();
        var rightEdge = activeCardInnerRight ?? (afterLabelX + ImGui.GetContentRegionAvail().X);

        // A small scaled gap so the rule does not touch the last letter of the label.
        var gap = 6f * ImGuiHelpers.GlobalScale;
        var width = Math.Max(0f, rightEdge - afterLabelX - gap);

        // GetCursorScreenPos() is the SCREEN-space counterpart of GetCursorPosX() above — the point
        // AddLine needs, since draw-list calls work in screen pixels, not window-relative layout
        // coordinates. Half the text line's height offsets from the line's top down to its
        // vertical middle, centering the rule on the label's text rather than its top edge.
        var centerY = ImGui.GetTextLineHeight() / 2f;
        var lineStart = ImGui.GetCursorScreenPos() + new Vector2(gap, centerY);
        var lineEnd = lineStart + new Vector2(width, 0f);

        // Derived from the active style rather than a hardcoded literal, matching how the upload
        // log reads ImGuiCol.TextDisabled for its own muted color elsewhere in this file. The alpha
        // is thinned further on top of that so the rule sits visually behind the label instead of
        // reading as more text.
        var ruleColor = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        ruleColor.W *= 0.6f;

        var thickness = 1f * ImGuiHelpers.GlobalScale;
        ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd, ImGui.GetColorU32(ruleColor), thickness);

        // The AddLine call above did not move the cursor, and SameLine left it sitting on the
        // label's own line. NewLine advances to a fresh line beneath it so whatever the caller
        // draws next is unaffected by either the SameLine or the rule.
        ImGui.NewLine();
    }

    /// <summary>
    /// Draws one labelled group of the "Reading from:" panel — its heading, then its notes — and
    /// reports whether any note in it was <see cref="SourceTone.Missing"/>. An empty group draws
    /// nothing at all, heading included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The heading is what tells the reader which KIND of thing the lines beneath it are — see
    /// <see cref="ReadStatus"/> for the two questions the groups answer. Without the labels the two read
    /// as one undifferentiated list of nouns. The label is a parameter rather than a branch on the group
    /// — this method never learns which of the two it is drawing, exactly as it never learns which
    /// collection or container any individual note came from.
    /// </para>
    /// <para>
    /// Drawn muted via <see cref="DrawSectionLabel"/>, unlike the "Reading from:" heading above it:
    /// these headings are captions naming the lines beneath them, and the tone-colored notes are what
    /// the reader is meant to actually read. <see cref="DrawSectionLabel"/> adds the trailing rule
    /// that turns the caption into a visible section divider.
    /// </para>
    /// </remarks>
    /// <param name="label">The group's heading.</param>
    /// <param name="notes">The group's lines, already written and toned by the pure builder.</param>
    /// <returns>True when at least one note carries the <see cref="SourceTone.Missing"/> tone.</returns>
    private bool DrawReadStatusGroup(string label, IReadOnlyList<SourceNote> notes)
    {
        // A group with nothing in it (no collection switched on; no item pass yet) skips its heading
        // too — an empty labelled section would only ask the reader what is supposed to be there.
        if (notes.Count == 0)
            return false;

        ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
        DrawSectionLabel(label);

        var hasMissingNote = false;

        foreach (var note in notes)
        {
            // Drawn at InlineIconScale, like every other inline status icon (see the constant).
            DrawIconedText(ToneIcon(note.Tone), ToneColor(note.Tone), note.Text, InlineIconScale);

            // Cached does not count as missing (see SourceNoteText for why Cached is a healthy resting
            // state rather than a gap): gating the caller's follow-up hint on it too would make that
            // hint permanent noise that never clears. Missing is different — it means the source is
            // contributing nothing at all, and every Missing note names a real, one-time in-game action
            // that changes that.
            if (note.Tone is SourceTone.Missing)
                hasMissingNote = true;
        }

        return hasMissingNote;
    }

    /// <summary>
    /// Maps a source note's tone to the color it draws in. A switch on the <em>tone</em>, never on a
    /// source key or scan-state string — the three tones are the only vocabulary this method knows,
    /// so a future storage source colors correctly the moment <see cref="SourceNoteText.Describe"/>
    /// assigns it one of the three, with no change needed here.
    /// </summary>
    private static Vector4 ToneColor(SourceTone tone) =>
        tone switch
        {
            SourceTone.Live => SuccessColor,
            SourceTone.Cached => CautionColor,
            SourceTone.Missing => ErrorColor,

            // The enum has exactly three members today; this arm exists only so the expression is
            // exhaustive against a future fourth tone without throwing at draw time.
            _ => ImGuiColors.DalamudGrey,
        };

    /// <summary>
    /// Maps a source note's tone to the icon drawn beside it — the same switch-on-tone discipline as
    /// <see cref="ToneColor"/>, and for the reason given there.
    /// </summary>
    private static FontAwesomeIcon ToneIcon(SourceTone tone) =>
        tone switch
        {
            SourceTone.Live => FontAwesomeIcon.CheckCircle,
            SourceTone.Cached => FontAwesomeIcon.Clock,
            SourceTone.Missing => FontAwesomeIcon.ExclamationCircle,

            // Mirrors ToneColor's fallback arm: keeps the expression exhaustive against a future
            // fourth tone without throwing at draw time.
            _ => FontAwesomeIcon.QuestionCircle,
        };

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

            // The three self-healing outcomes below need no action from the user, so they carry no
            // warning color — but they are still the sync card's status line, so they draw at full
            // contrast. Only red says "you have to do something".
            case ApiStatus.RateLimited:
            case ApiStatus.SyncDisabled:
                ImGui.TextUnformatted("Waiting before the next upload, as the server asked.");
                break;

            case ApiStatus.NetworkError:
                ImGui.TextUnformatted("Could not reach xiv-shinies.com. Will try again.");
                break;

            default:
                ImGui.TextUnformatted("The last upload did not succeed. Will try again.");
                break;
        }
    }

    /// <summary>
    /// Draws one checkbox per registered collector, shared by the wizard and the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contains no category names. Every label and description comes from the row the collector
    /// produced, which is what keeps "adding a collection is one new class" true.
    /// </para>
    /// <para>
    /// This card is about <b>consent alone</b> — what the user chooses to send. Whether a chosen
    /// collection could actually be READ is a live status, and it belongs with every other live
    /// status, in the sync card's read-status panel (see <see cref="DrawStatus"/>).
    /// </para>
    /// </remarks>
    /// <param name="rows">
    /// This frame's category rows, from <see cref="BuildCategoryRows"/>. Passed in rather than rebuilt
    /// here so a settings frame builds them exactly once for all three of its consumers — see
    /// <see cref="DrawSettings"/>.
    /// </param>
    /// <param name="showNewChips">
    /// Whether a manifest group the user has not been shown before wears a "New" badge (see
    /// <see cref="DrawGroupCheckboxes"/>). The settings pass true; the wizard passes false, because
    /// in the wizard every group is being shown for the first time and a badge on all of them at
    /// once distinguishes nothing. Either way the groups drawn are marked seen, so a user arriving
    /// in the settings straight from the wizard finds no badges waiting for them.
    /// </param>
    /// <param name="headerIcon">The card header's icon, or null for a headerless card.</param>
    /// <param name="headerTitle">The card header's title, or null for a headerless card.</param>
    private void DrawCategoryRows(
        IReadOnlyList<CategorySettingsRow> rows,
        bool showNewChips,
        FontAwesomeIcon? headerIcon = null,
        string? headerTitle = null)
    {
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
                    ManifestConsent.SetRowConsent(row, enabled, configuration.Settings);
                    configuration.Save();
                }

                // The description draws at the normal text color: it is the consent copy for this
                // category — what the plugin will send if the box is ticked — so it has to be
                // comfortably legible.
                ImGui.Indent(checkboxColumn);
                DrawWrapped(row.WhatGetsSent, ImGuiCol.Text);

                // Muted: it only restates why the checkbox above it is grayed out, which the
                // disabled control already conveys on its own.
                if (!row.ServerEnabled)
                    ImGui.TextDisabled("Temporarily switched off by XIV Shinies.");

                // Disabled along with the category above them. A group belongs to its category and is
                // only ever scanned as part of that category's pass, so leaving the groups live under a
                // greyed-out parent would offer the user a consent choice that cannot mean anything —
                // and ticking one would switch its category back on behind the very control that says it
                // is off.
                using (ImRaii.Disabled(!row.ServerEnabled))
                    DrawGroupCheckboxes(row, showNewChips);

                ImGui.Unindent(checkboxColumn);
                ImGui.Spacing();
            }
        }
    }

    /// <summary>
    /// True when at least one manifest-driven category's consent group (see
    /// <see cref="CategorySettingsRow.Groups"/>) still counts as "New" — the same test
    /// <see cref="DrawGroupCheckboxes"/> uses to decide whether to draw that group's own badge.
    /// </summary>
    /// <remarks>
    /// Used to decide whether the collapsed "Collections" header itself should wear a
    /// "New" chip (see <see cref="DrawSettings"/>): with the card collapsed, none of the per-group
    /// badges beneath it are visible, so a group added since the last session would otherwise go
    /// unnoticed until the user happened to expand the card. This reads the same rows
    /// <see cref="CategorySettingsView.Build"/> already produces and the same <c>seenThisSession</c>
    /// set <see cref="DrawGroupCheckboxes"/> already maintains — no new state, and no branch on which
    /// category or group is being asked about.
    /// </remarks>
    /// <param name="rows">The category rows to scan, from <see cref="CategorySettingsView.Build"/>.</param>
    private bool AnyGroupIsNew(IReadOnlyList<CategorySettingsRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Groups is not { Count: > 0 } groups)
                continue;

            foreach (var group in groups)
            {
                // Mirrors DrawGroupCheckboxes's own badge condition exactly: never displayed by this
                // install, or shown once already this session and therefore still wearing its badge.
                if (group.IsNew || seenThisSession.Contains(group.Key))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Draws the per-group consent checkboxes beneath a manifest-driven category and persists both
    /// the toggles and the seen-once flags, optionally badging a group the user has not been shown
    /// before with a "New" chip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seen-marking is the subtle part. A group arrives from <see cref="CategorySettingsView.Build"/>
    /// with <c>IsNew = true</c> until its persisted "seen" flag is set. The first frame we draw it we set
    /// that flag (one write), so every later frame's rebuild reports <c>IsNew = false</c> and this method
    /// stops writing for that group — the config is saved once per batch of newly-seen groups, never per
    /// frame (a per-frame save would be a real bug). Marking seen happens on <b>whichever surface drew
    /// the group</b>, wizard or settings: it records that the user has been shown it, and the wizard's
    /// consent step shows it just as plainly as the settings do.
    /// </para>
    /// <para>
    /// <c>seenThisSession</c> is a separate question — "is this group's badge currently on screen?" — and
    /// only the badge-drawing surface adds to it. The badge would otherwise blink out one frame after it
    /// appeared, since the persisted flag we just set makes the very next rebuild report the group as no
    /// longer new; remembering the key keeps it drawn for the rest of the session, while the persisted
    /// flag guarantees it is gone on the next load. A group first drawn by the WIZARD never enters that
    /// set, which is what leaves the settings screen badge-free for a user who has just finished setup:
    /// they have already seen every group there is.
    /// </para>
    /// </remarks>
    /// <param name="row">The category row whose groups are being drawn.</param>
    /// <param name="showNewChips">
    /// Whether an unseen group wears a "New" badge. False in the wizard, where every group is new by
    /// definition and a chip beside each of them says nothing.
    /// </param>
    private void DrawGroupCheckboxes(CategorySettingsRow row, bool showNewChips)
    {
        // Nothing to draw unless the server sent consent groups for this manifest-driven category.
        if (row.Groups is not { Count: > 0 } groups)
            return;

        // Past this point at least one group checkbox is going on screen — the fact the wizard's Finish
        // handler settles its consent on. See PluginSettings.SettleItemGroupConsent for what rides on it.
        if (!configuration.Settings.OnboardingComplete)
            wizardShowedGroups = true;

        // A further indent nests the group checkboxes beneath their category's description. Measured
        // the same way as the category column, inside the ItemInnerSpacing push DrawCategoryRows opened,
        // so it tracks the same spacing.
        var groupIndent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.Indent(groupIndent);

        // Collected while drawing, then persisted once after the loop. Null until the first genuinely
        // new group is seen, so a row whose groups are all already-seen writes nothing.
        List<string>? newlySeen = null;

        foreach (var group in groups)
        {
            var groupEnabled = group.Enabled;

            // Same `##key` identity trick as the category checkboxes above: the visible label is the
            // group's, but the widget's ImGui id comes from the unique group key, so two groups that
            // chose the same label never cross-wire their clicks.
            if (ImGui.Checkbox($"{group.Label}##group-{group.Key}", ref groupEnabled))
            {
                // The category and its groups have to agree, because neither can send anything without
                // the other. That rule lives in ManifestConsent, where it is unit-tested and names no
                // category; this window only reports the click.
                ManifestConsent.SetGroupConsent(row, group.Key, groupEnabled, configuration.Settings);
                configuration.Save();
            }

            // Drawing a group IS showing it to the user, so it is marked seen regardless of which
            // surface drew it — the wizard's consent step shows a group just as plainly as the
            // settings screen does.
            if (group.IsNew)
                (newlySeen ??= new List<string>()).Add(group.Key);

            if (!showNewChips)
                continue;

            // Remember that this group's badge went up, so it keeps drawing for the rest of the session
            // even though the seen-marking persisted after this loop makes the next rebuild report it
            // as un-new.
            if (group.IsNew)
                seenThisSession.Add(group.Key);

            // The badge shows for a group this install has never displayed, and for one whose badge went
            // up earlier this session. It is a small outlined chip with a leading star (see DrawChip),
            // so "New" reads as a compact badge beside the checkbox rather than another line of body
            // copy; Brand.Gold is the "shiny" accent used for highlights elsewhere.
            if (group.IsNew || seenThisSession.Contains(group.Key))
            {
                ImGui.SameLine();
                DrawChip(FontAwesomeIcon.Star, "New", Brand.Gold);
            }
        }

        // Persist the seen-once flags for every group that was new this frame, in a single save. Because
        // marking them seen makes the next rebuild report IsNew=false, this runs once per batch of
        // newly-seen groups rather than every frame.
        if (newlySeen is not null)
        {
            configuration.Settings.MarkItemGroupsSeen(newlySeen);
            configuration.Save();
        }

        ImGui.Unindent(groupIndent);
    }

    /// <summary>
    /// Everything <see cref="MeasureChip"/> works out about one chip before it is drawn: the icon
    /// glyph string plus every size and style constant <see cref="PaintChip"/> needs to paint it and
    /// <see cref="DrawChip"/> or <see cref="DrawHeaderRightChip"/> need to position it.
    /// </summary>
    /// <remarks>
    /// The measurement and the style constants travel together in one value, which keeps
    /// <see cref="DrawChip"/>'s normal, in-flow chip and <see cref="DrawHeaderRightChip"/>'s
    /// right-aligned overlay chip pixel-identical: both paint from the same <see cref="ChipMetrics"/>,
    /// so padding, rounding, and the two font scales have only one place they can be set.
    /// </remarks>
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
    /// Measures the glyph, the label, and the pill outline a "chip" badge needs, without drawing
    /// anything. Split out of the chip's draw pass so a caller that must position a chip somewhere
    /// other than "wherever the cursor currently is" — see <see cref="DrawHeaderRightChip"/>, which
    /// right-aligns one against a fixed edge — can know its exact footprint first.
    /// </summary>
    /// <remarks>
    /// <c>ImGui.SetWindowFontScale</c> is WINDOW-global state (see <see cref="DrawIcon"/>): it is not
    /// scoped to the icon font push or to any one draw call, so left set it would shrink every widget
    /// drawn afterwards in this window. Every measurement below that needs the shrunk scale pairs it
    /// with an immediate reset back to 1, on every path.
    /// </remarks>
    private ChipMetrics MeasureChip(FontAwesomeIcon icon, string text)
    {
        // Both the text and the padding shrink by this factor. Distinct from InlineIconScale, which
        // shrinks only the icon and leaves the body text beside it at full size; this shrinks an entire
        // self-contained badge, text included, so it reads as a compact tag rather than a full-size
        // label wearing a thin outline.
        const float ChipContentScale = 0.85f;

        // The icon is drawn SMALLER than the chip's own text. A FontAwesome glyph at the text's size
        // carries far more ink than a letter of the same height does, so at parity it reads as a
        // second word competing with the label instead of as a small leading mark before it.
        const float ChipIconScale = ChipContentScale * 0.7f;

        var paddingX = 6f * ChipContentScale * ImGuiHelpers.GlobalScale;
        var paddingY = 2f * ChipContentScale * ImGuiHelpers.GlobalScale;
        var rounding = 3f * ImGuiHelpers.GlobalScale;
        var thickness = 1f * ImGuiHelpers.GlobalScale;

        // The gap between the icon and the text, kept separate from the outline padding so either
        // can be tuned without affecting the other.
        var iconTextGap = 3f * ImGuiHelpers.GlobalScale;

        // Measure the icon at the ICON's scale — the same scale it is drawn at in PaintChip, so the
        // footprint this returns matches the ink exactly. The icon font must stay pushed for this
        // measurement — ToIconString's codepoint only renders as a glyph while that font is active,
        // otherwise it measures (and later draws) as an empty box.
        string glyph;
        Vector2 iconSize;
        using (iconFont.Push())
        {
            glyph = icon.ToIconString();
            ImGui.SetWindowFontScale(ChipIconScale);
            iconSize = ImGui.CalcTextSize(glyph);
            ImGui.SetWindowFontScale(1f);
        }

        // Measure the text at the CONTENT scale — again the scale PaintChip draws it at, and a larger
        // one than the icon's. Measured in the default font, which is what is active out here, outside
        // the icon font's push above.
        ImGui.SetWindowFontScale(ChipContentScale);
        var textSize = ImGui.CalcTextSize(text);
        ImGui.SetWindowFontScale(1f);

        // The chip's content is exactly as tall as its taller piece and exactly as wide as the icon,
        // the gap, and the text laid end to end. Max rather than the text's height alone: the icon is
        // drawn smaller and so is normally the shorter of the two, but the two come from different
        // fonts, and a glyph with a deep descender could still measure taller than the label.
        var contentHeight = Math.Max(iconSize.Y, textSize.Y);
        var contentWidth = iconSize.X + iconTextGap + textSize.X;
        var chipSize = new Vector2(contentWidth + (2f * paddingX), contentHeight + (2f * paddingY));

        return new ChipMetrics(
            glyph, text, iconSize, textSize, chipSize,
            paddingX, paddingY, iconTextGap, rounding, thickness,
            ChipContentScale, ChipIconScale);
    }

    /// <summary>
    /// Paints an already-measured chip's outline, icon, and text at an explicit screen position.
    /// Pure draw-list output: it never reads or moves the layout cursor, so it is safe to call from
    /// anywhere a caller has already worked out where the chip's top-left corner should land, whether
    /// that is "the current cursor" (<see cref="DrawChip"/>) or "overlapping a full-width widget
    /// already drawn this frame" (<see cref="DrawHeaderRightChip"/>).
    /// </summary>
    /// <param name="metrics">The chip's measurements, from <see cref="MeasureChip"/>.</param>
    /// <param name="topLeft">The chip outline's top-left corner, in screen coordinates.</param>
    /// <param name="color">The color shared by the outline, the icon, and the text.</param>
    private void PaintChip(ChipMetrics metrics, Vector2 topLeft, Vector4 color)
    {
        var contentOrigin = topLeft + new Vector2(metrics.PaddingX, metrics.PaddingY);
        var contentHeight = Math.Max(metrics.IconSize.Y, metrics.TextSize.Y);

        // Center the icon and the text independently within the content height, in case their
        // measured heights differ slightly — the same "split the leftover margin" centering DrawIcon
        // uses for a shrunk glyph inside its reserved box.
        var iconPos = contentOrigin + new Vector2(0f, (contentHeight - metrics.IconSize.Y) / 2f);
        var textPos = contentOrigin + new Vector2(
            metrics.IconSize.X + metrics.IconTextGap, (contentHeight - metrics.TextSize.Y) / 2f);

        var drawList = ImGui.GetWindowDrawList();
        var colorU32 = ImGui.GetColorU32(color);

        // Outline only, no fill — a filled or heavy chip would compete with Brand.Gold's other use
        // as a prominent "shiny" accent, when this is meant to read as a light badge.
        drawList.AddRect(
            topLeft, topLeft + metrics.ChipSize, colorU32, metrics.Rounding, ImDrawFlags.None,
            metrics.Thickness);

        // The icon, painted through the icon font at the ICON's scale — AddText draws with whatever
        // font is currently active, which is why this stays inside the same iconFont push and
        // SetWindowFontScale bracket MeasureChip used. The scale here must match the one the
        // measurement used, or the reserved footprint would stop describing what was drawn.
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
    /// A small outlined "chip": an icon then a label, both shrunk (the icon further than the text),
    /// inside a thin rounded rectangle, vertically centered against the standard checkbox row and
    /// flowing in-line at the current cursor position. Used for the "New" badge beside a freshly
    /// added consent group. The icon is a parameter, so any caller can reuse the same compact-tag
    /// look with a glyph of its own.
    /// </summary>
    /// <remarks>
    /// ImGui has no built-in chip/pill widget, so this is built from primitives, the same way
    /// <see cref="BrandToggle"/> hand-draws a switch: <see cref="MeasureChip"/> works out the exact
    /// footprint, then <see cref="PaintChip"/> paints it on the window's draw list — a list of raw
    /// shapes attached to the window, distinct from the layout cursor React-style widgets normally
    /// advance. Draw-list calls never move the layout cursor themselves, so a trailing <c>Dummy</c>
    /// reserves the same footprint by hand; without it, whatever is drawn next would land on top of
    /// the chip instead of after it.
    /// </remarks>
    /// <param name="icon">The glyph drawn before the text, at a smaller scale than the text.</param>
    /// <param name="text">The chip's label.</param>
    /// <param name="color">The color shared by the outline, the icon, and the text.</param>
    private void DrawChip(FontAwesomeIcon icon, string text, Vector4 color)
    {
        var metrics = MeasureChip(icon, text);

        // A checkbox is always drawn at frame height regardless of its label's own font size, so
        // this is the true height of the row the chip sits on even though the chip itself is
        // shorter. Centering the chip within it keeps the badge level with the checkbox and label
        // beside it, instead of hugging the row's top edge.
        var rowHeight = ImGui.GetFrameHeight();

        // SameLine (called by the caller before this) leaves the cursor at the row's top-left, just
        // to the right of whatever was drawn before it — the same Y every item on this line shares,
        // regardless of each item's own height. Offsetting by half the leftover height centers the
        // shorter chip inside that shared row.
        var topLeft = ImGui.GetCursorScreenPos() + new Vector2(0f, (rowHeight - metrics.ChipSize.Y) / 2f);
        PaintChip(metrics, topLeft, color);

        // Reserve the full row height (not just the chip's own, shorter height) so the chip's
        // footprint matches what every other item on this row already occupies — keeping the row's
        // total height, and therefore where the NEXT row starts, unaffected by the chip. The width
        // reserved is exactly chipSize.X, the same icon-plus-gap-plus-text-plus-padding total that
        // was just painted, so a widget placed after this with ImGui.SameLine() lands flush against
        // the chip's real right edge rather than overlapping or leaving a gap.
        ImGui.Dummy(new Vector2(metrics.ChipSize.X, rowHeight));
    }

    /// <summary>
    /// Draws a chip flush against a fixed right edge, overlapping the full-width widget drawn
    /// immediately before it, instead of flowing in-line the way <see cref="DrawChip"/> does. Built
    /// for the "Collections" header's "New" chip (see <see cref="DrawSettings"/>): a
    /// <c>CollapsingHeader</c> spans the entire available width, so its own item rectangle already
    /// reaches the row's right edge, and a plain <c>ImGui.SameLine()</c> after it would offset the
    /// cursor by that FULL width — pushing the chip past the visible window instead of beside the
    /// header's own text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads back the immediately preceding widget's rectangle with <c>GetItemRectMin</c> /
    /// <c>GetItemRectMax</c> — valid only because nothing else is drawn between that widget and this
    /// call — to learn the row's actual height, then paints the chip at an explicit screen position
    /// instead of asking the layout system for a "next" position on that row (there is not one; the
    /// row is already full). The chip is pure draw-list output with no interactive element of its
    /// own (see <see cref="PaintChip"/>), so overlapping the header this way does not steal any of
    /// its clicks — expanding and collapsing the section still works anywhere on the row, chip
    /// included.
    /// </para>
    /// <para>
    /// The layout cursor is left exactly where the preceding widget put it: painting through the
    /// draw list, like <see cref="BrandSeparator"/> and <see cref="DrawSectionLabel"/>, never touches
    /// it, so the caller does not need to save or restore anything around this call.
    /// </para>
    /// </remarks>
    /// <param name="icon">The glyph drawn before the text, at a smaller scale than the text.</param>
    /// <param name="text">The chip's label.</param>
    /// <param name="color">The color shared by the outline, the icon, and the text.</param>
    /// <param name="rightEdgeScreenX">
    /// The screen-space X the chip's own right edge should land just inside of — the enclosing
    /// <see cref="BrandCard"/>'s inner right edge when the preceding widget was drawn inside one, or
    /// the window's own content edge otherwise.
    /// </param>
    private void DrawHeaderRightChip(FontAwesomeIcon icon, string text, Vector4 color, float rightEdgeScreenX)
    {
        var metrics = MeasureChip(icon, text);

        // The preceding item's rectangle — the header this chip overlaps — read back in screen
        // space, so the chip can be vertically centered on the header's OWN measured height rather
        // than an assumed one.
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();
        var rowHeight = rowMax.Y - rowMin.Y;

        // A small scaled gap keeps the chip's outline off the card's own edge, matching the gap
        // DrawSectionLabel leaves between a label and its rule.
        var margin = 8f * ImGuiHelpers.GlobalScale;

        var topLeft = new Vector2(
            rightEdgeScreenX - margin - metrics.ChipSize.X,
            rowMin.Y + ((rowHeight - metrics.ChipSize.Y) / 2f));

        PaintChip(metrics, topLeft, color);
    }

    /// <summary>One checkbox that flips every collection at once.</summary>
    /// <remarks>
    /// <para>
    /// Shown checked only when everything is on, so clicking it always does the obvious thing: from
    /// "all on" it turns everything off, from anything else it turns everything on. It never names a
    /// category — it iterates whatever rows exist. A row the server has switched off is left out of
    /// both the reading and the writing, itself and its groups alike: that category uploads nothing
    /// whatever the boxes say, so this control leaves its consent exactly as the user last set it,
    /// ready to mean something again if the server switches it back on.
    /// </para>
    /// <para>
    /// "Everything" includes the per-group consent checkboxes nested under a manifest-driven row (see
    /// <see cref="DrawGroupCheckboxes"/>), both in what it writes and in whether it reads as checked.
    /// A category whose groups are all off uploads nothing at all, so a control promising "all
    /// collections" that left them off would be promising something it does not deliver — and would
    /// then keep showing itself unchecked, because a group somewhere is still off. The groups stay
    /// individually toggleable afterwards; this only sets a starting point.
    /// </para>
    /// <para>
    /// This says nothing about a group that arrives LATER. A group the server adds after this click
    /// has never appeared in any list the user has looked at, so it starts off and stays off until
    /// they tick it — <see cref="PluginSettings.IsItemGroupEnabled"/> is an allowlist, and only the
    /// groups on screen are ever written here.
    /// </para>
    /// </remarks>
    private void DrawSelectAll(IReadOnlyList<CategorySettingsRow> rows)
    {
        // Whether the box reads as ticked is a rule about consent, not about drawing, so it lives in
        // ManifestConsent with the rest of them and is unit-tested there.
        var allEnabled = ManifestConsent.AllConsentGiven(rows);

        if (ImGui.Checkbox("All collections##selectAll", ref allEnabled))
        {
            foreach (var row in rows)
            {
                if (row.ServerEnabled)
                    ManifestConsent.SetRowConsent(row, allEnabled, configuration.Settings);
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
    /// <remarks>
    /// <para>
    /// An accepted token is also the moment the plugin first has a credential the server will answer
    /// to, so it is where <c>/config</c> is fetched — see
    /// <see cref="SyncManager.RequestOnboardingConfigPoll"/> for why that request is allowed to run
    /// before onboarding is complete. Without it the wizard's consent step could not list the
    /// server's item manifest groups, because nothing would ever have asked for them.
    /// </para>
    /// <para>
    /// A one-shot without needing a flag of its own. <c>Draw</c> calls this sixty times a second, but
    /// <see cref="TokenVerifier.TakeResult"/> hands a probe's answer out exactly once, so the body below
    /// runs on the single frame that answer arrives and does nothing on every other.
    /// </para>
    /// </remarks>
    private void ConsumeTokenProbe()
    {
        if (verifier.TakeResult() is not { } response)
            return;

        onboarding.RecordTokenCheck(response.Status);
        account = response.Value;

        // Two conditions, and both are load-bearing. The token must be one the server actually
        // recognized — asking for a config with a token just rejected would earn a second 401 and
        // tell nobody anything. And onboarding must still be in progress, which is the only
        // situation the gate bypass exists for: once the wizard is done, SyncManager's own
        // interval poll owns /config and runs through the ordinary consent gate like everything
        // else, so there is nothing here left to bypass it for.
        if (onboarding.TokenCheck == TokenCheckState.Valid
            && !configuration.Settings.OnboardingComplete)
        {
            syncManager.RequestOnboardingConfigPoll();
        }
    }
}
