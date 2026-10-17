#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>Tells which <see cref="PluginKind"/> a plugin is and hands out the workflow of each kind.</summary>
internal static class PluginKinds
{
    private const string FrameworkSlug = "ePlugin";

    public static PluginKindWorkflow Workflow(PluginKind kind) => kind switch
    {
        PluginKind.Framework => FrameworkPluginWorkflow.Instance,
        PluginKind.EPlugin => EPluginWorkflow.Instance,
        PluginKind.CSharp => CSharpPluginWorkflow.Instance,
        PluginKind.GDScript => GDScriptPluginWorkflow.Instance,
        PluginKind.GDExtension => GDExtensionPluginWorkflow.Instance,
        _ => UnknownPluginWorkflow.Instance
    };

    /// <summary>
    /// The workflow of a plugin in an update journal. Journals written before they recorded the kind (or by a newer
    /// framework, with a kind this one does not know) fall back to what they recorded then: ePlugin or not.
    /// </summary>
    public static PluginKindWorkflow Workflow(UpdatePluginJournal plugin) => Workflow(
        Enum.TryParse<PluginKind>(plugin.Kind, out var kind) && Enum.IsDefined(kind) ? kind
        : plugin.Slug == FrameworkSlug ? PluginKind.Framework : plugin.IsEPlugin ? PluginKind.EPlugin : PluginKind.Unknown);

    public static string Name(PluginKind kind) => Workflow(kind).Name;

    /// <summary>
    /// The kind of an addon, enabled or not. Disabled plugins have no editor instance, so a C# plugin is recognized as an
    /// ePlugin by its compiled script type, which carries the script path Godot generated for it. A plugin that is no
    /// ePlugin but ships a <c>.gdextension</c> file is a GDExtension plugin, whatever its entry script.
    /// </summary>
    public static PluginKind Classify(string? script, bool isFramework, IEnumerable<Type> types, bool hasNativeExtension = false)
    {
        if (isFramework) return PluginKind.Framework;
        if (script is not null && script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var type = types.FirstOrDefault(t => PluginCatalog.ScriptPaths(t).Contains(script));
            if (type is not null && typeof(IEEditorPlugin).IsAssignableFrom(type)) return PluginKind.EPlugin;
        }
        if (hasNativeExtension) return PluginKind.GDExtension;
        return ByScript(script);
    }

    /// <summary>
    /// The kind an update treats a package as: an installed framework or ePlugin keeps its kind; otherwise a
    /// <c>.gdextension</c> in the installed or the staged version makes it a GDExtension plugin, so its libraries are
    /// replaced like one's; otherwise the staged entry script decides.
    /// </summary>
    public static PluginKind ForPackage(PluginUpdateTarget installed, string? stagedScript, IEnumerable<string> stagedFiles, IEnumerable<string> installedFiles)
    {
        if (installed.Kind == PluginKind.Framework || installed.Slug == FrameworkSlug) return PluginKind.Framework;
        if (installed.Kind == PluginKind.EPlugin) return PluginKind.EPlugin;
        if (stagedFiles.Concat(installedFiles).Any(GDExtensionConfig.IsExtensionFile)) return PluginKind.GDExtension;
        return ByScript(stagedScript);
    }

    /// <summary>
    /// The kind an update journal records: the editor's view decides whether the plugin is managed; otherwise the kind
    /// validation found.
    /// </summary>
    public static PluginKind ForJournal(string slug, bool managed, PluginKind validated) =>
        slug == FrameworkSlug ? PluginKind.Framework
        : managed ? PluginKind.EPlugin
        : validated is PluginKind.Framework or PluginKind.EPlugin ? PluginKind.Unknown : validated;

    /// <summary>Whether <paramref name="directory"/> (a file system path) contains a <c>.gdextension</c> file.</summary>
    public static bool HasNativeExtension(string directory)
    {
        try
        {
            return Directory.Exists(directory) &&
                   Directory.EnumerateFiles(directory, "*" + GDExtensionConfig.Extension, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                       .Any(f => !PackageFiles.Skip(Path.GetRelativePath(directory, f).Replace('\\', '/')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static PluginKind ByScript(string? script) =>
        script is null ? PluginKind.Unknown
        : script.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ? PluginKind.GDScript
        : script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? PluginKind.CSharp
        : PluginKind.Unknown;
}
#endif
