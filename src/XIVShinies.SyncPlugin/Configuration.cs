// `using` pulls a namespace into scope so we can name its types without the full dotted path
// (like `import` in TS). System.* usings sort first by our .editorconfig convention.
using System;
using Dalamud.Configuration;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The plugin's persisted settings. Dalamud serializes this object to a JSON file on disk and
/// hands it back next launch, so anything stored here survives restarts.
/// </summary>
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
    // default. This is roughly `version: number = 0` on a class, but with built-in get/set.
    // IPluginConfiguration requires this so Dalamud can migrate old saved configs across
    // schema changes.
    public int Version { get; set; } = 0;

    /// <summary>
    /// Convenience wrapper so callers can persist changes without reaching through the plugin
    /// interface themselves. Call this after mutating any setting.
    /// </summary>
    // `Plugin.PluginInterface` is a static property on the Plugin class (explained in Plugin.cs)
    // — Dalamud's handle for plugin-level operations, here "save my config object to disk".
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
