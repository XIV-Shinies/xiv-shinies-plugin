using System;
using Dalamud.Plugin.Services;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Enforces that game state is only read from the framework thread.
/// </summary>
/// <remarks>
/// The game changes its memory on the framework thread every frame, so a read from another thread
/// can get a torn value or follow a pointer the game already freed. That access violation crashes the
/// whole game, and .NET never delivers it to a <c>catch</c>. This check throws an ordinary exception
/// first, which the collector runner turns into an omitted category.
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
