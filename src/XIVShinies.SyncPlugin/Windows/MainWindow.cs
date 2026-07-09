using System;
// Vector2 is a simple (X, Y) float pair from the .NET math library. ImGui uses it everywhere
// for sizes and positions.
using System.Numerics;
// At Dalamud API 15 the ImGui bindings live under Dalamud.Bindings.ImGui (NOT the older
// ImGuiNET package). ImGui is an "immediate mode" GUI: instead of building a retained tree of
// components like React, you re-issue draw calls every frame inside Draw() below.
using Dalamud.Bindings.ImGui;
// The windowing helpers (Window base class, WindowSystem) that manage plugin windows for us.
using Dalamud.Interface.Windowing;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The plugin window opened by the <c>/shinies</c> command.
/// </summary>
// `sealed` means no other class may inherit from this one (a small performance/clarity win;
// there's no reason to subclass a concrete window). We inherit from Dalamud's `Window` base
// class AND implement `IDisposable`. `IDisposable` is the .NET pattern for "I hold something
// that must be cleaned up" — its `Dispose()` method is the rough equivalent of the cleanup
// function you return from a React `useEffect`.
public sealed class MainWindow : Window, IDisposable
{
    // A constructor runs when the object is created with `new MainWindow()`. `: base(...)` calls
    // the parent `Window` constructor first, passing the window's title. The `###XIVShiniesMain`
    // suffix is an ImGui trick: text before `###` is the visible title, and the part from `###`
    // on is a stable internal ID, so we could later change the visible title without ImGui
    // treating it as a different window (and losing its saved position/size).
    public MainWindow() : base($"{PluginMeta.DisplayName}###XIVShiniesMain")
    {
        // Constrain how small/large the user can resize the window. `SizeConstraints` is a
        // property on the base Window class. The object-initializer syntax `new T { A = x, B = y }`
        // sets properties right after construction — similar to spreading a config object in JS.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 180),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    // Required by IDisposable. Nothing to clean up yet (no unmanaged resources, event handlers,
    // or textures held here), so it's intentionally empty — but we keep it so the contract is
    // satisfied and future cleanup has a home.
    public void Dispose() { }

    // `override` means we're replacing the base Window class's virtual Draw() method. The
    // WindowSystem calls this once per frame while the window is open, so everything here runs
    // ~60 times a second. That's the "immediate mode" model: we describe the UI fresh each frame
    // rather than mutating a persistent component tree.
    public override void Draw()
    {
        // Each ImGui.* call appends a widget to the current window, top to bottom.
        ImGui.TextWrapped($"{PluginMeta.DisplayName} is loaded.");
        ImGui.Spacing();
        ImGui.TextWrapped("Account linking and settings aren't available yet. Nothing is " +
                          "sent anywhere yet.");
    }
}
