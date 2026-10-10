#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

internal enum PluginKind { Framework, EPlugin, CSharp, GDScript, Unknown }

/// <summary>What the ePlugin Manager knows about one addon, enabled or not.</summary>
internal sealed record PluginInfo(string Slug, string Name, PluginKind Kind, bool Enabled)
{
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string? UpdateUrl { get; init; }
    /// <summary>The update site the project sets for the plugin, which replaces <see cref="UpdateUrl"/>.</summary>
    public string? UpdateSite { get; init; }
    /// <summary>The <c>documentation_url</c> of plugin.cfg, linked in the details.</summary>
    public string? DocumentationUrl { get; init; }
    /// <summary>The <c>source_url</c> of plugin.cfg, linked in the details.</summary>
    public string? SourceUrl { get; init; }
    /// <summary>How many packages of this plugin the local plugin directories hold, of any version.</summary>
    public int LocalPackages { get; init; }
    public string? Script { get; init; }
    /// <summary>Lifecycle state of an ePlugin that has an editor instance; null for every other plugin.</summary>
    public EEditorPluginState? State { get; init; }
    public string? Error { get; init; }
    /// <summary>A local attempt that failed or was interrupted and needs Retry failed.</summary>
    public LocalPluginAttempt? FailedAttempt { get; init; }
    /// <summary>The recipe, when the plugin already created it in this session.</summary>
    public EEditorPluginRecipe? Recipe { get; init; }
    public bool Missing { get; init; }
}

internal static class PluginCatalog
{
    public static string KindName(PluginKind kind) => kind switch
    {
        PluginKind.Framework => "ePlugin Framework",
        PluginKind.EPlugin => "ePlugin",
        PluginKind.CSharp => "C# plugin",
        PluginKind.GDScript => "GDScript plugin",
        _ => "Plugin"
    };

    /// <summary>The <c>script</c> key of plugin.cfg is relative to the plugin directory unless it is a res:// path.</summary>
    public static string? ResolveScript(string directory, string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        return script.StartsWith("res://", StringComparison.Ordinal) ? script : directory.TrimEnd('/') + "/" + script.TrimStart('/');
    }

    /// <summary>
    /// Disabled plugins have no editor instance, so a C# plugin is recognized as an ePlugin by its compiled script
    /// type, which carries the script path Godot generated for it.
    /// </summary>
    public static PluginKind Classify(string? script, bool isFramework, IEnumerable<Type> types)
    {
        if (isFramework) return PluginKind.Framework;
        if (script is null) return PluginKind.Unknown;
        if (script.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)) return PluginKind.GDScript;
        if (!script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return PluginKind.Unknown;
        var type = types.FirstOrDefault(t => t.GetCustomAttribute<ScriptPathAttribute>()?.Path == script);
        return type is not null && typeof(IEEditorPlugin).IsAssignableFrom(type) ? PluginKind.EPlugin : PluginKind.CSharp;
    }
}
#endif
