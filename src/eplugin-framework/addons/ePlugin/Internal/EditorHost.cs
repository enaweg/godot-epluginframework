#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// What the plugin lifecycle in <see cref="EGlobal"/> needs from the Godot editor. <see cref="EditorHost"/> is the
/// editor; tests use a fake that runs the enable and disable callbacks of their plugins like Godot does.
/// </summary>
internal interface IEditorHost
{
    bool IsPluginEnabled(string slug);

    /// <summary>
    /// Enables or disables a plugin. Godot runs its <c>_EnablePlugin</c> or <c>_DisablePlugin</c> before this returns,
    /// so an ePlugin calls back into <see cref="EGlobal"/> from inside this call.
    /// </summary>
    void SetPluginEnabled(string slug, bool enabled);

    /// <summary>The slugs of the plugins below <c>res://addons</c> that <c>editor_plugins/enabled</c> lists.</summary>
    IReadOnlySet<string> EnabledPluginSlugs();

    /// <summary>The directory of a plugin, which holds its <c>plugin.cfg</c>, as a path Godot's file APIs can read.</summary>
    string PluginDirectory(string slug);

    /// <summary>
    /// Whether the plugin is an ePlugin. Disabled plugins have no editor instance, so this is told by their compiled
    /// script type.
    /// </summary>
    bool IsEPlugin(string slug);

    /// <summary>
    /// Creates the recipe of an ePlugin that has no editor instance, from a throw-away instance of its script type.
    /// False when the plugin is no ePlugin; throws what <see cref="IEEditorPlugin.CreateRecipe"/> throws.
    /// </summary>
    bool CreateRecipeOfDisabledPlugin(string slug, IEEditorPluginBuilder builder);

    /// <summary>Whether dialogs can be shown: the editor has a display and the framework node is in its tree.</summary>
    bool CanShowDialogs(Node framework);

    /// <summary>Asks the editor to look at the project's files again, unless it is scanning already.</summary>
    void ScanFilesystem();

    /// <summary>The path registered for an autoload, without the leading <c>*</c> of a singleton; null when there is none.</summary>
    string? GetAutoload(string name);

    void AddAutoload(EditorPlugin plugin, string name, string path);

    void RemoveAutoload(EditorPlugin plugin, string name);
}

/// <summary>The Godot editor.</summary>
internal sealed class EditorHost : IEditorHost
{
    public bool IsPluginEnabled(string slug) => EditorInterface.Singleton.IsPluginEnabled(slug);

    public void SetPluginEnabled(string slug, bool enabled) => EditorInterface.Singleton.SetPluginEnabled(slug, enabled);

    public IReadOnlySet<string> EnabledPluginSlugs() =>
        ParseEnabledPlugins(ProjectSettings.GetSetting("editor_plugins/enabled", Array.Empty<string>()).AsStringArray());

    public string PluginDirectory(string slug) => $"res://addons/{slug}";

    public bool IsEPlugin(string slug) => PluginTypes.HasEPluginBelow(PluginDirectory(slug));

    public bool CreateRecipeOfDisabledPlugin(string slug, IEEditorPluginBuilder builder)
    {
        var directory = PluginDirectory(slug);
        using var config = new ConfigFile();
        if (config.Load(directory + "/plugin.cfg") != Error.Ok)
        {
            return false;
        }

        var script = Manager.PluginCatalog.ResolveScript(directory, config.GetValue("plugin", "script", "").AsString().Trim());
        var type = script is null || !script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? null : PluginTypes.EPluginOf(script);
        if (type is null)
        {
            return false;
        }

        // CreateRecipe is deterministic and side-effect free, so a throw-away instance gives the plugin's recipe.
        var instance = (EditorPlugin)Activator.CreateInstance(type)!;
        try
        {
            ((IEEditorPlugin)instance).CreateRecipe(builder);
            return true;
        }
        finally
        {
            instance.Free();
        }
    }

    public bool CanShowDialogs(Node framework) => DisplayServer.GetName() != "headless" && framework.IsInsideTree();

    public void ScanFilesystem()
    {
        var filesystem = EditorInterface.Singleton.GetResourceFilesystem();
        if (!filesystem.IsScanning())
        {
            filesystem.Scan();
        }
    }

    public string? GetAutoload(string name) =>
        ProjectSettings.HasSetting("autoload/" + name)
            ? ProjectSettings.GetSetting("autoload/" + name, "").AsString().TrimStart('*')
            : null;

    public void AddAutoload(EditorPlugin plugin, string name, string path) => plugin.AddAutoloadSingleton(name, path);

    public void RemoveAutoload(EditorPlugin plugin, string name) => plugin.RemoveAutoloadSingleton(name);

    /// <summary>The plugin slugs of <c>editor_plugins/enabled</c> entries (<c>res://addons/&lt;slug&gt;/plugin.cfg</c>).</summary>
    internal static IReadOnlySet<string> ParseEnabledPlugins(IEnumerable<string> paths) =>
        paths.Where(path => path.StartsWith("res://addons/", StringComparison.Ordinal) &&
                            path.EndsWith("/plugin.cfg", StringComparison.Ordinal))
            .Select(path => path["res://addons/".Length..^"/plugin.cfg".Length])
            .Where(slug => slug.Length > 0 && !slug.Contains('/') && !slug.Contains('\\'))
            .ToHashSet(StringComparer.Ordinal);
}
#endif
