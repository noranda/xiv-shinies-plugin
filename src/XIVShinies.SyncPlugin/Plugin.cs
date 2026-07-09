// Dalamud's command system (registering the /shinies slash command).
using Dalamud.Game.Command;
// The windowing system that draws and manages our ImGui windows.
using Dalamud.Interface.Windowing;
// Provides the [PluginService] attribute used for dependency injection below.
using Dalamud.IoC;
// Core plugin interfaces, including IDalamudPlugin and IDalamudPluginInterface.
using Dalamud.Plugin;
// The injectable Dalamud "services" live here (ICommandManager, IPluginLog, etc.).
using Dalamud.Plugin.Services;
// Our own window classes.
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The plugin entry point. Dalamud discovers the one class that implements
/// <see cref="IDalamudPlugin"/>, constructs it on load, and calls <see cref="Dispose"/> on
/// unload. Think of the constructor as the plugin's "mount" and Dispose as its "unmount".
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // --- Injected Dalamud services -------------------------------------------------------
    // Dalamud uses dependency injection: rather than us importing/creating these services, the
    // framework *sets* them for us. Any static property tagged with [PluginService] gets filled
    // in by Dalamud before our constructor runs. It's conceptually like React context/props
    // being provided from above, except the values arrive via reflection into static slots.
    //
    // `internal` = visible anywhere in this project but not to other assemblies. `static` = one
    // shared slot for the whole plugin (there's only ever one Plugin instance). `= null!` is a
    // promise to the compiler: "this is non-null in practice (Dalamud fills it) — trust me, don't
    // warn." The `!` is the null-forgiving operator, the C# cousin of TS's `!` non-null assertion.

    /// <summary>Dalamud's per-plugin handle: config persistence, UI builder hooks, manifest, etc.</summary>
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>Registers and routes slash commands like <c>/shinies</c>.</summary>
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;

    /// <summary>Writes to the Dalamud log (view in-game with <c>/xllog</c>).</summary>
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    // --- Plugin state --------------------------------------------------------------------

    /// <summary>The persisted settings object (see Configuration.cs).</summary>
    // `{ get; init; }` is an auto-property that can be set only during construction, then becomes
    // read-only — like a `readonly` field you can still assign in the constructor.
    public Configuration Configuration { get; init; }

    // `readonly` fields can be assigned only here or in the constructor, then never reassigned.
    // The WindowSystem owns/draws our windows; the string is just a unique namespace for it.
    private readonly WindowSystem windowSystem = new("XIVShiniesSync");
    private readonly MainWindow mainWindow;

    /// <summary>
    /// Constructor — Dalamud calls this once on load. Wire everything up here, and be sure to
    /// tear down in Dispose whatever you set up here (handlers, events, windows).
    /// </summary>
    public Plugin()
    {
        // Load previously-saved settings, or start fresh. `as Configuration` is a safe cast that
        // yields null on type mismatch; `?? new Configuration()` is the null-coalescing operator
        // (identical to JS `??`) supplying a default. Net effect: "use the saved config if there
        // is one, otherwise a new default config".
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Create our window and hand it to the WindowSystem so it gets drawn each frame.
        mainWindow = new MainWindow();
        windowSystem.AddWindow(mainWindow);

        // Register the /shinies command. CommandInfo takes the handler method (OnCommand); the
        // object-initializer sets the help text shown in /xlhelp.
        CommandManager.AddHandler(PluginMeta.CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Open {PluginMeta.DisplayName}.",
        });

        // Register a longer alias that runs the same handler. ShowInHelp = false keeps /xlhelp
        // to a single entry instead of listing the command twice.
        CommandManager.AddHandler(PluginMeta.CommandAlias, new CommandInfo(OnCommand)
        {
            ShowInHelp = false,
        });

        // Subscribe to UI events. `+=` adds a handler to a C# "event" (a built-in
        // publisher/subscriber list); there's no exact React analog, but it's like
        // addEventListener. Every `+=` here MUST be matched by a `-=` in Dispose, or we'd leak
        // the handler after the plugin unloads.
        // - Draw: fires every frame; we forward it to the WindowSystem to render our windows.
        // - OpenMainUi: the "open" button next to the plugin in the installer.
        // - OpenConfigUi: the "settings" gear next to the plugin in the installer.
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information($"{PluginMeta.DisplayName} loaded.");
    }

    /// <summary>
    /// Cleanup on unload (Dalamud calls this). Mirror of the constructor: unsubscribe every
    /// event, remove every window and command handler. This is the plugin's "unmount" — the
    /// same discipline as returning a cleanup function from useEffect so nothing lingers.
    /// </summary>
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        CommandManager.RemoveHandler(PluginMeta.CommandName);
        CommandManager.RemoveHandler(PluginMeta.CommandAlias);
    }

    // The command handler. Its signature (string command, string args) is what CommandInfo
    // expects: `command` is what was typed (/shinies), `args` is anything after it. We ignore
    // both and just toggle the window open/closed.
    private void OnCommand(string command, string args) => mainWindow.Toggle();

    // Small helper wired to the installer's open/config buttons above. `Toggle()` comes from the
    // Window base class (show if hidden, hide if shown).
    private void ToggleMainUi() => mainWindow.Toggle();
}
