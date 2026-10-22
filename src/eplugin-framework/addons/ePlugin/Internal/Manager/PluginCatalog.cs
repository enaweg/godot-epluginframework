#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>What the ePlugin Manager knows about one addon, enabled or not.</summary>
internal sealed record PluginInfo(string Slug, string Name, PluginKind Kind, bool Enabled)
{
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string? UpdateUrl { get; init; }
    /// <summary>The update site the project sets for the plugin, which replaces <see cref="UpdateUrl"/>.</summary>
    public string? UpdateSite { get; init; }
    /// <summary>The plugin's entry in ePlugin's built-in list, while that list is used.</summary>
    public KnownPlugin? Known { get; init; }
    /// <summary>The <c>documentation_url</c> of plugin.cfg, linked in the details.</summary>
    public string? DocumentationUrl { get; init; }
    /// <summary>The <c>source_url</c> of plugin.cfg, linked in the details.</summary>
    public string? SourceUrl { get; init; }
    /// <summary>How many packages of this plugin the local directories hold, of any version.</summary>
    public int LocalPackages { get; init; }
    public string? Script { get; init; }
    /// <summary>Lifecycle state of an ePlugin that has an editor instance; null for every other plugin.</summary>
    public EEditorPluginState? State { get; init; }
    public string? Error { get; init; }
    /// <summary>A local attempt that failed or was interrupted and is retried, rolled back or given up on under Needs attention.</summary>
    public LocalPluginAttempt? FailedAttempt { get; init; }
    /// <summary>The recipe, when the plugin already created it in this session.</summary>
    public EEditorPluginRecipe? Recipe { get; init; }
    public bool Missing { get; init; }
    /// <summary>The GDExtensions the plugin ships, as the editor runs them.</summary>
    public IReadOnlyList<NativeExtensionStatus> Extensions { get; init; } = [];
}

internal static class PluginCatalog
{
    public static string KindName(PluginKind kind) => PluginKinds.Name(kind);

    /// <summary>The <c>script</c> key of plugin.cfg is relative to the plugin directory unless it is a res:// path.</summary>
    public static string? ResolveScript(string directory, string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        return script.StartsWith("res://", StringComparison.Ordinal) ? script : directory.TrimEnd('/') + "/" + script.TrimStart('/');
    }

    /// <summary>
    /// The script paths Godot generated for <paramref name="type"/> itself. The attribute allows multiple instances
    /// and is inherited, so a script class deriving from another script class would also report its base's path.
    /// </summary>
    public static IEnumerable<string> ScriptPaths(Type type) =>
        type.GetCustomAttributes<ScriptPathAttribute>(inherit: false).Select(a => a.Path);
}
#endif
