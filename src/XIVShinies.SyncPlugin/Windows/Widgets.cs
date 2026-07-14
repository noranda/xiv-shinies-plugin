using System;
using System.Numerics;
// At Dalamud API 15 the ImGui bindings live under Dalamud.Bindings.ImGui (NOT the older
// ImGuiNET package). ImGui is an "immediate mode" GUI: instead of building a retained tree of
// components like React, callers re-issue draw calls every frame.
using Dalamud.Bindings.ImGui;
// FontAwesomeIcon (the icon glyphs) and its ToIconString extension.
using Dalamud.Interface;
// Dalamud's standard palette (DalamudRed, HealerGreen, …), designed against its own themes —
// preferred over hand-picked literals, which ignore the user's chosen style.
using Dalamud.Interface.Colors;
// Font handles — a caller's icon or bold font is pushed and popped around the text it styles;
// fonts are a stack in ImGui, exactly like colors.
using Dalamud.Interface.ManagedFontAtlas;
// Scaling helpers: users on high-DPI displays run Dalamud at 1.5–2x, and raw pixel sizes ignore it.
using Dalamud.Interface.Utility;
// Scoped wrappers for ImGui's push/pop pairs. `using (ImRaii.Disabled(...))` guarantees the matching
// End/Pop even if the block throws — a whole class of unbalanced-stack bugs stops existing.
using Dalamud.Interface.Utility.Raii;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The shared drawing primitives every window in this plugin composes its UI from: icons, brand
/// buttons, chips, wrapped text, separators, and the toggle switch.
/// </summary>
/// <remarks>
/// <para>
/// Stateless by design: a primitive that needs a font takes the caller's <see cref="IFontHandle"/>
/// as a parameter, and one that wraps text takes the caller's wrap edge (a card's inner right, or
/// null for the window edge). The window owns its fonts and its card state; this class owns only
/// how things draw — which is what lets a second window reuse the same look without inheriting
/// anything.
/// </para>
/// <para>
/// Colors here come from Dalamud's own palette (and, for muted text, from the active style)
/// rather than hardcoded literals: the user picks a Dalamud theme, including light ones, and a
/// hand-picked gray that reads fine on dark is mush on light. Brand accents are the deliberate
/// exception and live in <see cref="Brand"/>.
/// </para>
/// <para>
/// Muted text (<c>ImGuiCol.TextDisabled</c>, a low-contrast gray in Dalamud's default themes) is
/// for incidental text only — relative timestamps, empty-state filler, captions over the value
/// they label, hints that restate a disabled control's state. Anything the user must read to
/// decide something or to understand what the plugin does, consent copy above all, draws at the
/// normal text color (<c>ImGuiCol.Text</c>).
/// </para>
/// </remarks>
internal static class Widgets
{
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
    internal const float InlineIconScale = 0.82f;

    /// <summary>The red for warnings the user should act on.</summary>
    internal static readonly Vector4 ErrorColor = ImGuiColors.DalamudRed;

    /// <summary>The green for good news — an accepted upload, a verified token.</summary>
    internal static readonly Vector4 SuccessColor = ImGuiColors.HealerGreen;

    /// <summary>
    /// The yellow for "working, but worth knowing" states — a cached (possibly-stale) source note,
    /// for instance. From the Dalamud palette like the other two, so it reads correctly against
    /// whatever theme — including light ones — the user has chosen.
    /// </summary>
    internal static readonly Vector4 CautionColor = ImGuiColors.DalamudYellow;

    /// <summary>A consistent vertical gap between sections — one place to tune the page rhythm.</summary>
    internal static void SectionGap() => ImGui.Dummy(new Vector2(0f, 10f * ImGuiHelpers.GlobalScale));

    /// <summary>
    /// Moves the cursor so a widget of the given width ends at the right edge — the current
    /// line's content edge by default, or an explicit edge such as a card's inner right.
    /// </summary>
    /// <remarks>
    /// Never moves the cursor LEFT: on a window too narrow for the widget, the cursor stays put
    /// and the row simply runs long, instead of the widget overlapping whatever was already
    /// drawn on the line. A window's minimum size makes that case rare; this makes it safe.
    /// </remarks>
    internal static void AlignRight(float width, float? rightEdge = null)
    {
        var edge = rightEdge ?? (ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), edge - width));
    }

    /// <summary>A branded divider: a 2px teal rule instead of ImGui's gray separator.</summary>
    /// <param name="innerRight">
    /// A card's inner right edge in cursor space, so the rule spans the card rather than the
    /// window — or null outside any card, where the window's content edge is the span.
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

    /// <summary>Draws wrapped text in a color, without unbalancing anything.</summary>
    /// <remarks>
    /// Needed because <c>TextWrapped</c> has no colored variant and <c>TextColored</c> does not
    /// wrap — long descriptions would run straight off the window edge. Pushing
    /// <c>ImGuiCol.Text</c> recolors whatever text is drawn inside the scope. The wrap position is
    /// pushed by hand rather than via <c>TextWrapped</c>, because TextWrapped always wraps at the
    /// window edge — inside a card the text must break at the card's inner edge instead. A wrap
    /// position of 0 means "the window edge", so a null <paramref name="wrapEdge"/> behaves
    /// exactly like TextWrapped.
    /// </remarks>
    internal static void DrawWrapped(string text, Vector4 color, float? wrapEdge)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        using (ImRaii.TextWrapPos(wrapEdge ?? 0f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>Draws wrapped text in one of the current style's own colors.</summary>
    internal static void DrawWrapped(string text, ImGuiCol styleColor, float? wrapEdge)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(styleColor)))
        using (ImRaii.TextWrapPos(wrapEdge ?? 0f))
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
    internal static void DrawWrappedSpans(params (string Text, Vector4? Color)[] spans)
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

    /// <summary>
    /// Draws one FontAwesome glyph in the given color, optionally shrunk relative to the
    /// surrounding line's text.
    /// </summary>
    /// <param name="iconFont">The caller's icon font — the glyph renders as an empty box outside it.</param>
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
    internal static void DrawIcon(IFontHandle iconFont, FontAwesomeIcon icon, Vector4 color, float scale = 1f)
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

    /// <summary>
    /// An icon beside wrapped text, both in the same color — the shared layout for every icon-led
    /// line of body copy: red warnings, the green "Token accepted" line, the "Reading from:" source
    /// notes. (A card header's icon and title share a color too, but that is a header row with a
    /// layout of its own.)
    /// </summary>
    /// <remarks>
    /// The icon and the text are two separate ImGui items on the same line (<c>SameLine</c> puts
    /// them side by side, the way flex-direction: row would). Without grouping them, a second
    /// wrapped line would start back at the window's left edge — underneath the icon — instead of
    /// lining up with the first line's text. <c>ImRaii.Group()</c> treats everything drawn inside
    /// it as one block for layout purposes, which is what keeps wrapped lines flush with the text
    /// above them rather than the icon beside it.
    /// </remarks>
    /// <param name="iconFont">The caller's icon font.</param>
    /// <param name="icon">The icon to draw.</param>
    /// <param name="color">The color shared by the icon and the text.</param>
    /// <param name="text">The wrapped text beside the icon.</param>
    /// <param name="wrapEdge">The caller's wrap edge — a card's inner right, or null for the window edge.</param>
    /// <param name="iconScale">
    /// Forwarded to <see cref="DrawIcon"/> unchanged. The default of 1 (full size) suits a standalone
    /// icon+text line at normal weight; every status line passes <see cref="InlineIconScale"/>.
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
    /// A secondary button: bold label, theme colors at rest, and a soft teal tint on hover — every
    /// non-primary button in this plugin goes through here.
    /// </summary>
    /// <remarks>
    /// The hover override exists because the resting look should follow the user's theme, but many
    /// themes hover buttons red — and red on a harmless button reads as "this will do something
    /// bad". The tint is translucent, so it layers over any theme rather than fighting it.
    /// </remarks>
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
    /// The branded primary button: near-black bold text on the bright teal, the site's own
    /// primary-button pattern. One per screen — it marks the action that moves the user forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not built on <see cref="BoldButton(IFontHandle, string)"/>: color pushes are a
    /// stack where the most recent push wins, so an inner secondary-hover push would override the
    /// primary's hover.
    /// </para>
    /// <para>
    /// It owns its disabled state rather than leaving that to the caller, so the color and the behavior
    /// can never disagree: the face, its hover, and its active color all go to
    /// <see cref="Brand.DisabledSurface"/> (see there for why a flat slate rather than a dimmed teal), so
    /// nothing lights up under the cursor to contradict it, and <c>ImRaii.Disabled</c> swallows the press.
    /// </para>
    /// </remarks>
    /// <param name="buttonFont">The caller's bold button font.</param>
    /// <param name="label">The button's label.</param>
    /// <param name="size">Its size.</param>
    /// <param name="enabled">False to draw it dead and swallow the press — see the remarks.</param>
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
    /// The width for a comfortably padded button: the label (measured in the bold button font)
    /// plus generous side padding. ImGui's default button width hugs the text.
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
    /// Draws an icon-plus-text pair centered over an already-drawn button whose visible label is
    /// blank — transient in-button feedback like "Copied" or "Syncing".
    /// </summary>
    /// <remarks>
    /// A button label lives in a single font, and this needs two: the icon font for the glyph and
    /// the bold font for the word. Text items are not interactive, so overlapping the button is
    /// harmless. The layout cursor is teleported onto the button, the two pieces drawn, and the
    /// cursor restored so normal layout resumes.
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

        // A negative wrap position means "never wrap" — a button's face is a single line, and
        // without this a card's wrap scope would fold a right-aligned button's label at the
        // card edge running through it.
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
    /// A sliding on/off switch whose hover color previews the click's outcome: green when
    /// clicking would turn it on, red when clicking would turn it off. The knob stays white.
    /// </summary>
    /// <remarks>
    /// Drawn by hand (an invisible button for the interaction, draw-list shapes for the look)
    /// rather than using Dalamud's ToggleButton, whose colors come from the active theme with no
    /// way to override the hover per state. At rest the track is brand teal when on and gray
    /// when off. Returns true on the frame the value flips, like ImGui.Checkbox.
    /// </remarks>
    internal static bool BrandToggle(string id, ref bool value)
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
    /// Draws a muted section label followed by a thin horizontal rule that fills the rest of the
    /// row, so the label reads as a section divider ("Label ────────") instead of a plain caption
    /// floating above the lines it introduces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ImGui has no built-in "label plus rule" widget, so the rule is hand-drawn on the window's
    /// <em>draw list</em> — a list of raw shapes attached to the window, separate from the layout
    /// cursor that ordinary widgets like <c>TextDisabled</c> advance. This is the same primitive
    /// <see cref="BrandSeparator"/> and <see cref="DrawChip"/> use. Because draw-list calls never
    /// move the cursor themselves, the label's own <c>TextDisabled</c> call is what actually
    /// reserves layout space; the rule is simply painted over empty space to its right, and
    /// <c>NewLine</c> at the end restores normal top-to-bottom flow for whatever the caller draws
    /// next.
    /// </para>
    /// <para>
    /// The rule spans from just right of the label text to the row's right edge — the caller's
    /// <paramref name="innerRight"/> (a card's inner edge) when inside a card, or the window's own
    /// content edge otherwise, the same fallback <see cref="BrandSeparator"/> uses, so the rule can
    /// never overrun whichever container it is in. It is centered on the label's text line (half of
    /// <c>GetTextLineHeight</c> below the line's top, where the cursor sits) and colored from the
    /// active Dalamud theme's disabled-text color with its alpha thinned further, rather than a
    /// literal color, so it reads as a quiet divider beneath the label instead of competing with
    /// it — and still matches whatever theme the user has chosen.
    /// </para>
    /// </remarks>
    /// <param name="label">The section heading drawn before the rule.</param>
    /// <param name="innerRight">A card's inner right edge in cursor space, or null outside any card.</param>
    internal static void DrawSectionLabel(string label, float? innerRight)
    {
        // The muted heading itself. This is the ONLY call that advances the layout cursor; the rule
        // drawn below is pure decoration painted beside it, not a second widget.
        ImGui.TextDisabled(label);
        ImGui.SameLine();

        // GetCursorPosX() is in cursor-space (relative to the window's content region), the same
        // coordinate space innerRight arrives in. Subtracting one from the other gives a plain
        // pixel width that stays correct no matter where on screen this row ends up, which is why
        // BrandSeparator computes its own width the same way.
        var afterLabelX = ImGui.GetCursorPosX();
        var rightEdge = innerRight ?? (afterLabelX + ImGui.GetContentRegionAvail().X);

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
        // log reads ImGuiCol.TextDisabled for its own muted color. The alpha is thinned further on
        // top of that so the rule sits visually behind the label instead of reading as more text.
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
    private static ChipMetrics MeasureChip(IFontHandle iconFont, FontAwesomeIcon icon, string text)
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
    /// <param name="iconFont">The caller's icon font.</param>
    /// <param name="metrics">The chip's measurements, from <see cref="MeasureChip"/>.</param>
    /// <param name="topLeft">The chip outline's top-left corner, in screen coordinates.</param>
    /// <param name="color">The color shared by the outline, the icon, and the text.</param>
    private static void PaintChip(IFontHandle iconFont, ChipMetrics metrics, Vector2 topLeft, Vector4 color)
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
    /// <param name="iconFont">The caller's icon font.</param>
    /// <param name="icon">The glyph drawn before the text, at a smaller scale than the text.</param>
    /// <param name="text">The chip's label.</param>
    /// <param name="color">The color shared by the outline, the icon, and the text.</param>
    internal static void DrawChip(IFontHandle iconFont, FontAwesomeIcon icon, string text, Vector4 color)
    {
        var metrics = MeasureChip(iconFont, icon, text);

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
        PaintChip(iconFont, metrics, topLeft, color);

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
    /// for a chip beside a <c>CollapsingHeader</c>: the header spans the entire available width, so
    /// its own item rectangle already reaches the row's right edge, and a plain
    /// <c>ImGui.SameLine()</c> after it would offset the cursor by that FULL width — pushing the
    /// chip past the visible window instead of beside the header's own text.
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
    /// <param name="iconFont">The caller's icon font.</param>
    /// <param name="icon">The glyph drawn before the text, at a smaller scale than the text.</param>
    /// <param name="text">The chip's label.</param>
    /// <param name="color">The color shared by the outline, the icon, and the text.</param>
    /// <param name="rightEdgeScreenX">
    /// The screen-space X the chip's own right edge should land just inside of — the enclosing
    /// card's inner right edge when the preceding widget was drawn inside one, or the window's own
    /// content edge otherwise.
    /// </param>
    internal static void DrawHeaderRightChip(
        IFontHandle iconFont, FontAwesomeIcon icon, string text, Vector4 color, float rightEdgeScreenX)
    {
        var metrics = MeasureChip(iconFont, icon, text);

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

        PaintChip(iconFont, metrics, topLeft, color);
    }
}
