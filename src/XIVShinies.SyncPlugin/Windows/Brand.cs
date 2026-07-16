using System.Numerics;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The XIV Shinies brand accents, for the plugin to visually match the website.
/// </summary>
/// <remarks>
/// <para>
/// These mirror the site's dark-theme design tokens: <c>--primary: hsl(174 90% 45%)</c> (the teal
/// of the mascot's gem) and <c>--gold-bright: hsl(45 95% 56%)</c> (the "shiny" accent), converted
/// to RGB once here rather than computed at runtime.
/// </para>
/// <para>
/// Hardcoding these is the deliberate exception to the "colors come from the user's Dalamud theme"
/// rule: brand identity stays fixed, while body and muted text keep following the active style.
/// Use these for accents — headings, rules, icons, the primary button — never for body copy.
/// </para>
/// </remarks>
public static class Brand
{
    /// <summary>The primary brand teal — headings, accents, the primary button.</summary>
    public static readonly Vector4 Teal = new(0.05f, 0.86f, 0.77f, 1f);

    /// <summary>
    /// A clearly darker teal for the primary button's hovered state — far enough from the resting
    /// teal that the hover is unmistakable, while the pressed state below goes darker still.
    /// </summary>
    public static readonly Vector4 TealHover = new(0.04f, 0.72f, 0.64f, 1f);

    /// <summary>The darkest teal, for the primary button while pressed.</summary>
    public static readonly Vector4 TealDark = new(0.03f, 0.61f, 0.55f, 1f);

    /// <summary>
    /// Hover tint for secondary buttons: translucent teal, so it blends over the theme's button
    /// color instead of replacing it. Themes commonly hover red, which reads as destructive.
    /// </summary>
    public static readonly Vector4 SecondaryHover = new(0.05f, 0.86f, 0.77f, 0.25f);

    /// <summary>Pressed tint for secondary buttons — the hover tint, stronger.</summary>
    public static readonly Vector4 SecondaryActive = new(0.05f, 0.86f, 0.77f, 0.40f);

    /// <summary>
    /// Near-black text for on-teal surfaces — the site's <c>--primary-foreground</c>. A bright
    /// button needs dark text; white on teal has too little contrast.
    /// </summary>
    public static readonly Vector4 TealForeground = new(0.07f, 0.09f, 0.12f, 1f);

    /// <summary>The brand teal at reduced opacity, for rules, dividers, and underlines.</summary>
    public static readonly Vector4 TealRule = new(0.05f, 0.86f, 0.77f, 0.45f);

    /// <summary>
    /// The face of a primary button that cannot be pressed: a flat, desaturated slate.
    /// </summary>
    /// <remarks>
    /// A color, not a fade. ImGui's disabled state only multiplies the alpha, and the brand teal is
    /// vivid enough that a dimmed teal still reads as a live button someone simply has to click harder.
    /// Draining the color out of it is what says "not yet" at a glance — the button keeps its shape and
    /// its place, and loses the thing that invited the press.
    /// </remarks>
    public static readonly Vector4 DisabledSurface = new(0.26f, 0.29f, 0.33f, 1f);

    /// <summary>Text on <see cref="DisabledSurface"/> — legible, clearly not the live foreground.</summary>
    public static readonly Vector4 DisabledForeground = new(0.62f, 0.65f, 0.70f, 1f);

    /// <summary>The "shiny" gold — sparkle and gem accents.</summary>
    public static readonly Vector4 Gold = new(0.98f, 0.77f, 0.14f, 1f);
}
