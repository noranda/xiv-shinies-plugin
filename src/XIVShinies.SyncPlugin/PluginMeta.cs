// A "namespace" groups related types under a dotted name, a bit like a module path in
// JS/TS. This is a *file-scoped* namespace (note the semicolon instead of wrapping braces):
// everything below it belongs to XIVShinies.SyncPlugin. Other files reach these types with
// `using XIVShinies.SyncPlugin;` — the C# equivalent of an import.
namespace XIVShinies.SyncPlugin;

// Comments that start with three slashes (///) are "XML doc comments" — the C# version of
// JSDoc/TSDoc. Editors show them on hover, and they can be compiled into API docs.
/// <summary>
/// Pure, game-independent plugin constants and helpers. Deliberately free of Dalamud/game
/// types so the xUnit test project can exercise it without a running game (see the CLAUDE.md
/// "pure logic vs. game surfaces" split).
/// </summary>
// `static class` means this class can never be instantiated with `new` and holds no
// per-instance data — it's just a bucket of shared constants and functions, like a plain TS
// module that only exports consts and functions. `public` makes it visible to other
// projects/files (the default in C# is otherwise `internal`, i.e. this assembly only).
public static class PluginMeta
{
    // `const` is a compile-time constant: its value is fixed here and baked into every place
    // that uses it (like a TS `const` that can never be reassigned, but stricter — it must be
    // a literal known at compile time). Naming convention: C# uses PascalCase for public
    // members, not camelCase.
    /// <summary>The primary in-game slash command that opens the plugin UI.</summary>
    public const string CommandName = "/shinies";

    /// <summary>
    /// A longer alias for <see cref="CommandName"/>, for players who reach for the full name.
    /// Registered as a hidden alias (not shown in <c>/xlhelp</c>) so the help list stays clean.
    /// </summary>
    public const string CommandAlias = "/xivshinies";

    /// <summary>Human-facing plugin name (mirrors the manifest <c>Name</c> field).</summary>
    public const string DisplayName = "XIV Shinies Sync";

    /// <summary>Where the plugin's source lives (mirrors the manifest <c>RepoUrl</c> field).</summary>
    public const string SourceUrl = "https://github.com/noranda/xiv-shinies-plugin";

    /// <summary>The community Discord invite (the same one the website links to).</summary>
    public const string DiscordUrl = "https://discord.gg/UuNe5BwAGG";

    /// <summary>The maintainer's sponsor page (the same one the website links to).</summary>
    public const string SponsorUrl = "https://www.patreon.com/c/noranda/";

    /// <summary>
    /// Builds the <c>User-Agent</c> header the API client sends on every request, in the format
    /// <c>XIVShinies.SyncPlugin/&lt;version&gt;</c>.
    /// </summary>
    // This is an "expression-bodied method": `=> expr` is shorthand for `{ return expr; }`,
    // just like a concise arrow function `(version) => ...` in JS. The `$"..."` is an
    // interpolated string — C#'s template literal, where `{version}` is spliced in.
    public static string UserAgent(string version) => $"XIVShinies.SyncPlugin/{version}";
}
