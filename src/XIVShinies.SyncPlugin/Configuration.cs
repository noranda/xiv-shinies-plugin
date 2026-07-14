// `using` pulls a namespace into scope so we can name its types without the full dotted path
// (like `import` in TS). System.* usings sort first by our .editorconfig convention.
using System;
using Dalamud.Configuration;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The object Dalamud serializes to disk and hands back on the next launch.
/// </summary>
/// <remarks>
/// A deliberately thin shell. Implementing <c>IPluginConfiguration</c> ties this type to
/// <c>Dalamud.dll</c>, which only exists inside the running game — so it cannot be constructed in
/// a unit test. All the actual settings and their rules live in <see cref="PluginSettings"/>,
/// which is Dalamud-free and therefore testable.
/// </remarks>
// `[Serializable]` is an "attribute" — metadata attached to a type, written in square brackets
// above it. There's no direct React analog; think of it as a compile-time tag/decorator that
// other code can read via reflection. Here it marks the class as safe to serialize to disk.
[Serializable]
// A `class` here is a normal reference type (allocated on the heap; variables hold a reference
// to it, like objects in JS). `: IPluginConfiguration` means this class *implements* that
// interface — the same idea as `class Foo implements Bar` in TS. Dalamud requires the type it
// persists to implement IPluginConfiguration, whose one member is the `Version` property below.
public class Configuration : IPluginConfiguration
{
    // A C# "property" looks like a field but is really a get/set pair. `{ get; set; }` is an
    // "auto-property" — the compiler generates the backing storage for you. `= 0` sets the
    // default. IPluginConfiguration requires this so Dalamud can migrate old saved configs
    // across schema changes.
    public int Version { get; set; } = 0;

    /// <summary>Everything the user can configure. Persisted as a nested object.</summary>
    public PluginSettings Settings { get; set; } = new();

    /// <summary>
    /// Convenience wrapper so callers can persist changes without reaching through the plugin
    /// interface themselves. Call this after mutating any setting.
    /// </summary>
    /// <remarks>
    /// Saving means serializing, and serializing means walking every list and dictionary in
    /// <see cref="PluginSettings"/> — which makes this the largest READER of the collections that class
    /// guards. It runs under the same lock as the writers do: a save on the draw thread (the user
    /// ticked a checkbox) would otherwise be free to walk a list the framework thread was adding to,
    /// and a collection walked while it is being added to throws.
    /// </remarks>
    // `Plugin.PluginInterface` is a static property on the Plugin class (explained in Plugin.cs)
    // — Dalamud's handle for plugin-level operations, here "save my config object to disk".
    public void Save() => Settings.RunLocked(() => Plugin.PluginInterface.SavePluginConfig(this));
}
