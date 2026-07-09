using System.Collections.Generic;
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
// Our HTTPS client and its DTOs. A child namespace is not visible automatically — only enclosing
// namespaces are searched — so it needs an explicit using.
using XIVShinies.SyncPlugin.Api;
// The registered fact sources.
using XIVShinies.SyncPlugin.Collectors;
// The upload orchestrator and its supporting policy classes.
using XIVShinies.SyncPlugin.Sync;
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

    /// <summary>Reads the game's static data sheets (quests, mounts, achievements, …).</summary>
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    /// <summary>
    /// Answers what the <b>local</b> player has unlocked or completed. It exposes only the local
    /// player's state — there is no way to ask it about anyone else.
    /// </summary>
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;

    /// <summary>
    /// The game's per-frame loop. Used to prove a collector is on the framework thread before it
    /// reads game memory, and to marshal work onto that thread.
    /// </summary>
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    /// <summary>Login and logout events for the local session.</summary>
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    /// <summary>
    /// The <b>local</b> character's identity — content id, name, home world. Dalamud's rules forbid
    /// collecting identifiers for any other player, and this service exposes no way to.
    /// </summary>
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    // --- Plugin state --------------------------------------------------------------------

    /// <summary>The persisted settings object (see Configuration.cs).</summary>
    // `{ get; init; }` is an auto-property that can be set only during construction, then becomes
    // read-only — like a `readonly` field you can still assign in the constructor.
    public Configuration Configuration { get; init; }

    // `readonly` fields can be assigned only here or in the constructor, then never reassigned.
    // The WindowSystem owns/draws our windows; the string is just a unique namespace for it.
    private readonly WindowSystem windowSystem = new("XIVShiniesSync");
    private readonly MainWindow mainWindow;

    // The HTTPS client for the XIV Shinies API. Constructed here but never called on its own —
    // nothing is uploaded until the user explicitly opts in. It owns an HttpClient, so it must be
    // disposed below.
    private readonly ApiClient apiClient;

    // Every registered fact source. Constructed once; nothing runs them yet. They hold no
    // unmanaged resources, so there is nothing to dispose.
    private readonly IReadOnlyList<ICollector> collectors;

    // Listens for login/unlock/interval and drives the uploads. Subscribes to game events, so it
    // must be disposed — and disposed BEFORE the ApiClient it borrows.
    private readonly SyncManager syncManager;

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

        // Build the API client from the persisted settings. The manifest carries the version the
        // build stamped in, which becomes the User-Agent and the payload's pluginVersion field.
        var version = PluginInterface.Manifest.AssemblyVersion?.ToString() ?? "0.0.0";
        apiClient = new ApiClient(Configuration.Settings, version);

        // Build the fact sources. Nothing reads the game until something explicitly runs them.
        collectors = CollectorRegistry.Create(DataManager, UnlockState, Framework);

        // Start listening. The manager subscribes to login and unlock events immediately, but every
        // path out of them checks the upload gate first, so a user who has not opted in sends
        // nothing and the plugin never contacts the server.
        syncManager = new SyncManager(
            Framework, ClientState, PlayerState, UnlockState, Log,
            apiClient, Configuration.Settings, collectors, version);

        // Create our window and hand it to the WindowSystem so it gets drawn each frame.
        mainWindow = new MainWindow();
        windowSystem.AddWindow(mainWindow);

        // Register the /shinies command. CommandInfo takes the handler method (OnCommand); the
        // object-initializer sets the help text shown in /xlhelp.
        CommandManager.AddHandler(PluginMeta.CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Toggle the {PluginMeta.DisplayName} window.",
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

        // Before the ApiClient, deliberately: this unsubscribes the game events and cancels any
        // upload in flight, so nothing is still reaching for the client when it goes away.
        syncManager.Dispose();

        // Releases the underlying HttpClient and its connection pool.
        apiClient.Dispose();

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
