namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// How one collection identifies and describes itself: its wire key, its name, and what it sends.
/// </summary>
/// <remarks>
/// <para>
/// Grouped into a single value rather than passed as three loose strings, because three adjacent
/// <c>string</c> parameters are trivially easy to hand over in the wrong order — and the compiler
/// would never notice. Here the call site names each one.
/// </para>
/// <para>
/// This is the whole of a collection's user-facing identity. The settings window renders it without
/// knowing which collection it is looking at, which is what lets a new collection appear in the UI
/// by existing rather than by being added to a list somewhere.
/// </para>
/// </remarks>
public sealed record CategoryInfo
{
    /// <summary>The payload key, the opt-in key, and the server's kill-switch key, all at once.</summary>
    public required string Key { get; init; }

    /// <summary>The name a person reads, for example <c>"Mounts"</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// A plain-language sentence naming exactly what leaves the machine for this category.
    /// </summary>
    /// <remarks>
    /// A compliance surface: Dalamud requires the user be told what is collected before consenting.
    /// It must describe the real payload, and must be revised whenever the collector starts sending
    /// something new.
    /// </remarks>
    public required string WhatGetsSent { get; init; }

    /// <summary>
    /// True when this collection's scope is driven by the server's item manifest, rather than being
    /// fixed at compile time (as quests, mounts, minions, and achievements are).
    /// </summary>
    /// <remarks>
    /// This is <b>self-description, not a category-name branch</b>: the settings window asks a
    /// collector "do you want group rows?" through this flag instead of asking "are you the items
    /// collector?" by comparing keys. A future manifest-driven collection sets this to true on its own
    /// <see cref="CategoryInfo"/> and gets the same group-row treatment automatically — nothing
    /// downstream needs to learn its name. Defaults to <c>false</c>, matching every existing
    /// fixed-scope collection.
    /// </remarks>
    public bool UsesItemManifest { get; init; }
}
