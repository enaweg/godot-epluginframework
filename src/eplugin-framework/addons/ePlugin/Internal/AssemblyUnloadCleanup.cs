#if TOOLS
using System;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Releases references that runtime libraries outside the project assembly hold onto its types, so Godot can
/// unload the assembly on a rebuild.
/// </summary>
/// <remarks>
/// System.Text.Json lives in the non-collectible default context and caches reflection metadata and emitted
/// accessors for every type it (de)serializes. The state stores, update cache and journals serialize types of
/// this assembly, so without clearing that cache every rebuild fails with "Failed to unload assemblies"
/// (godotengine/godot#78513). The handler used here is the one System.Text.Json provides for hot reload.
/// </remarks>
internal static class AssemblyUnloadCleanup
{
    private static bool _registered;

    public static void Register()
    {
        // Static state is reset with each assembly reload, so this registers once per load context.
        if (_registered) return;
        _registered = true;
        var context = AssemblyLoadContext.GetLoadContext(typeof(AssemblyUnloadCleanup).Assembly);
        if (context is null || !context.IsCollectible) return;
        context.Unloading += _ => ClearJsonCaches();
    }

    private static void ClearJsonCaches()
    {
        try
        {
            typeof(JsonSerializerOptions).Assembly
                .GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler")
                ?.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public)
                ?.Invoke(null, [null]);
        }
        catch (Exception)
        {
            // Best effort: a missing or changed handler only means the reload may fail as before.
        }
    }
}
#endif
