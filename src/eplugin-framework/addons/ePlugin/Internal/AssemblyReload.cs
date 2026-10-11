#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Whether the editor is about to replace this C# assembly. Godot's hot-reload watcher loads a newer build within half
/// a second, in place; until then the editor still runs the old code. Starting another plugin operation in between runs
/// it on that old code and stacks another build and reload onto the pending one, and Godot 4.7.2's in-place reload is
/// unreliable enough that this has crashed the editor.
/// </summary>
internal static class AssemblyReload
{
    // GodotTools' own check, the one its watcher reloads on. It is internal to the editor's C# tools, so without it
    // (another Godot version) the build's timestamp is compared with the one this assembly was loaded from.
    private static readonly Func<bool>? GodotCheck = BindGodotCheck();
    private static DateTime? _loadedBuild;

    /// <summary>Remembers the build this assembly was loaded from; called once the framework initializes.</summary>
    public static void Capture() => _loadedBuild ??= BuildTime();

    /// <summary>True while a newer build of this assembly waits to be loaded. Editor thread only.</summary>
    public static bool IsPending
    {
        get
        {
            try
            {
                if (GodotCheck is not null) return GodotCheck();
                return _loadedBuild is { } loaded && BuildTime() is { } built && built > loaded;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static Func<bool>? BindGodotCheck()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "GodotTools")
                ?.GetType("GodotTools.Internals.Internal")
                ?.GetMethod("IsAssembliesReloadingNeeded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)
                ?.CreateDelegate<Func<bool>>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? BuildTime()
    {
        var name = typeof(AssemblyReload).Assembly.GetName().Name;
        var path = ProjectSettings.GlobalizePath($"res://.godot/mono/temp/bin/Debug/{name}.dll");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }
}
#endif
