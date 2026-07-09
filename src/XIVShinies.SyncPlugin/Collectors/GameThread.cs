using System;
using Dalamud.Plugin.Services;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Enforces that game state is only read from the framework thread.
/// </summary>
/// <remarks>
/// <para>
/// The game mutates its own memory on the framework thread every frame. Reading it from any other
/// thread races that mutation: at best a torn value, at worst a dereference of a pointer the game
/// has already freed — an access violation that takes the whole game down, not just the plugin.
/// </para>
/// <para>
/// This has to be a <b>runtime</b> check, not a comment. An <c>AccessViolationException</c> is a
/// corrupted-state exception that .NET refuses to deliver to a <c>catch</c>, so nothing downstream
/// can rescue a bad read. Failing loudly here converts an unrecoverable crash into an ordinary
/// exception, which the collector runner turns into a safely-omitted category.
/// </para>
/// </remarks>
public static class GameThread
{
    /// <summary>Throws unless the caller is on the framework thread.</summary>
    /// <exception cref="InvalidOperationException">Called from any other thread.</exception>
    public static void EnsureFrameworkThread(IFramework framework, string caller)
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException(
                $"{caller} reads game state and must run on the framework thread. " +
                "Marshal the call with IFramework.RunOnFrameworkThread.");
        }
    }
}
