#if TOOLS
using System;
using System.IO;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Whether the editor is about to replace this C# assembly. Godot's hot-reload watcher loads a newer build within half
/// a second, in place; until then the editor still runs the old code. Starting another plugin operation in between runs
/// it on that old code and stacks another build and reload onto the pending one, and Godot 4.7.2's in-place reload is
/// unreliable enough that this has crashed the editor.
/// </summary>
/// <remarks>
/// Compares the build on disk with the one this assembly was loaded from. GodotTools' own check is not used: it can
/// report a reload as needed while no newer build exists, which locked the ePlugin Manager.
/// </remarks>
internal static class AssemblyReload
{
    /// <summary>
    /// The watcher reloads within half a second and a reload takes a few seconds. A build still not loaded after this
    /// long will not be (e.g. the reload failed); waiting longer would only lock the ePlugin Manager.
    /// </summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(15);

    private static DateTime? _loadedBuild;
    private static DateTime? _pendingSince;

    /// <summary>Remembers the build this assembly was loaded from; called once the framework initializes.</summary>
    public static void Capture() => _loadedBuild ??= BuildTime();

    /// <summary>True while a newer build of this assembly waits to be loaded.</summary>
    public static bool IsPending
    {
        get
        {
            try
            {
                var state = Decide(ref _loadedBuild, BuildTime(), DateTime.UtcNow, ref _pendingSince);
                if (state == State.GaveUp)
                    EPlugin.Instance?.Logger.Warn("The editor did not load the rebuilt C# code. Restart the editor if plugins behave unexpectedly.");
                return state == State.Pending;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal enum State { Current, Pending, GaveUp }

    /// <summary>
    /// Whether <paramref name="built"/> still waits to replace <paramref name="loaded"/>. Giving up accepts the build as
    /// loaded, so a later build is waited for again.
    /// </summary>
    internal static State Decide(ref DateTime? loaded, DateTime? built, DateTime now, ref DateTime? pendingSince)
    {
        if (loaded is null || built is null || built <= loaded)
        {
            pendingSince = null;
            return State.Current;
        }

        pendingSince ??= now;
        if (now - pendingSince < GiveUpAfter) return State.Pending;
        loaded = built;
        pendingSince = null;
        return State.GaveUp;
    }

    private static DateTime? BuildTime()
    {
        var name = typeof(AssemblyReload).Assembly.GetName().Name;
        var path = ProjectSettings.GlobalizePath($"res://.godot/mono/temp/bin/Debug/{name}.dll");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }
}
#endif
