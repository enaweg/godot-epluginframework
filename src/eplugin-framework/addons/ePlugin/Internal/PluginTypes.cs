#if TOOLS
using System;
using System.Linq;
using System.Reflection;
using Enaweg.Plugin.Internal.Manager;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// The types compiled into the project assembly, read once per assembly load. Every C# plugin is compiled into it, so
/// a plugin without an editor instance (disabled, or not loaded yet after a reload) is recognized by its script type.
/// <see cref="Assembly.GetTypes"/> reflects over every type of the game, and it was asked for every check.
/// </summary>
internal static class PluginTypes
{
    private static readonly Lazy<Type[]> AllTypes = new(Load);
    private static readonly Lazy<(Type Type, string[] Scripts)[]> EPluginTypes = new(() => AllTypes.Value
        .Where(type => typeof(EditorPlugin).IsAssignableFrom(type) && typeof(IEEditorPlugin).IsAssignableFrom(type))
        .Select(type => (type, PluginCatalog.ScriptPaths(type).ToArray()))
        .ToArray());

    /// <summary>Every type of the assembly; those that cannot be loaded are left out.</summary>
    public static Type[] All => AllTypes.Value;

    /// <summary>The ePlugin type whose script is <paramref name="script"/> (a res:// path), or null.</summary>
    public static Type? EPluginOf(string script) =>
        EPluginTypes.Value.FirstOrDefault(entry => entry.Scripts.Contains(script, StringComparer.Ordinal)).Type;

    /// <summary>Whether an ePlugin script lies below <paramref name="directory"/> (a res:// path).</summary>
    public static bool HasEPluginBelow(string directory)
    {
        var prefix = directory.TrimEnd('/') + "/";
        return EPluginTypes.Value.Any(entry => entry.Scripts.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static Type[] Load()
    {
        try
        {
            return typeof(PluginTypes).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
    }
}
#endif
