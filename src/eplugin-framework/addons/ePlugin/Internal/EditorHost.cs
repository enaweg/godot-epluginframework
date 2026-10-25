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

    /// <summary>The project's folder (<c>res://</c>) as an absolute path, where update journals and backups are kept.</summary>
    string ProjectRoot { get; }

    /// <summary>
    /// The C# assembly the project builds (<c>dotnet/project/assembly_name</c>), which names its solution; empty to find
    /// the solution by itself.
    /// </summary>
    string AssemblyName { get; }

    /// <summary>
    /// The folder of the project's solution as an absolute path: <c>dotnet/project/solution_directory</c>, relative to
    /// <c>res://</c> like GodotTools reads it (e.g. <c>..</c> for a solution in the repository's root), or the project's
    /// folder while it is empty.
    /// </summary>
    string SolutionDirectory { get; }

    /// <summary>The folder holding every plugin's directory, as a path Godot's file APIs can read.</summary>
    string AddonsDirectory { get; }

    /// <summary>
    /// The editor's configuration folder as an absolute path. It belongs to the user and is shared by all projects and
    /// Godot versions, e.g. for the list of local directories.
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>The directory of a plugin, which holds its <c>plugin.cfg</c>, as a path Godot's file APIs can read.</summary>
    string PluginDirectory(string slug);

    /// <summary>The nodes Godot adds the editor plugins to, read after an assembly reload dropped their contexts.</summary>
    IReadOnlyList<Node> PluginNodes();

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

    /// <summary>Whether the editor is scanning the project's files; an update waits until it is done.</summary>
    bool IsScanningFilesystem { get; }

    /// <summary>
    /// Turns off "Import Resources When Unfocused" until the result is disposed. While the editor is unfocused, that
    /// setting rescans every half second; a project whose rescan takes longer never becomes idle.
    /// </summary>
    IDisposable SuspendUnfocusedImports();

    /// <summary>Restarts the editor after the current frame, saving the project first.</summary>
    void RestartEditor();

    /// <summary>The path registered for an autoload, without the leading <c>*</c> of a singleton; null when there is none.</summary>
    string? GetAutoload(string name);

    void AddAutoload(EditorPlugin plugin, string name, string path);

    void RemoveAutoload(EditorPlugin plugin, string name);

    /// <summary>Whether Godot has loaded the GDExtension whose <c>.gdextension</c> file is <paramref name="file"/> (a file system path).</summary>
    bool IsExtensionLoaded(string file);

    /// <summary>Loads the GDExtension of <paramref name="file"/>; false when Godot could not.</summary>
    bool LoadExtension(string file);

    /// <summary>Unloads the GDExtension of <paramref name="file"/>, which closes its libraries; false when Godot could not.</summary>
    bool UnloadExtension(string file);
}

/// <summary>The Godot editor.</summary>
internal sealed class EditorHost : IEditorHost
{
    public bool IsPluginEnabled(string slug) => EditorInterface.Singleton.IsPluginEnabled(slug);

    public void SetPluginEnabled(string slug, bool enabled) => EditorInterface.Singleton.SetPluginEnabled(slug, enabled);

    public IReadOnlySet<string> EnabledPluginSlugs() =>
        ParseEnabledPlugins(ProjectSettings.GetSetting("editor_plugins/enabled", Array.Empty<string>()).AsStringArray());

    public string ProjectRoot => ProjectSettings.GlobalizePath("res://");

    public string AssemblyName => ProjectSettings.GetSetting("dotnet/project/assembly_name").AsString();

    public string SolutionDirectory =>
        ProjectSettings.GlobalizePath(SolutionDirectoryPath(ProjectSettings.GetSetting("dotnet/project/solution_directory", "").AsString()));

    public string AddonsDirectory => "res://addons";

    public string ConfigDirectory => EditorInterface.Singleton.GetEditorPaths().GetConfigDir();

    public string PluginDirectory(string slug) => $"{AddonsDirectory}/{slug}";

    public IReadOnlyList<Node> PluginNodes() => EditorInterface.Singleton.GetBaseControl().GetParent().GetChildren();

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

    public bool IsScanningFilesystem => EditorInterface.Singleton.GetResourceFilesystem().IsScanning();

    public IDisposable SuspendUnfocusedImports() => UnfocusedImportSuspension.Begin();

    public void RestartEditor()
    {
        StopAssemblyWatcher(PluginNodes());
        // Godot asks to save modified resources before it restarts while its global undo history is unsaved, and saving
        // only the unsaved scenes leaves it so. Every plugin that adds an autoload when the editor starts makes it unsaved,
        // so the restart waited for a confirmation behind the progress window and the editor stayed open. Queued first,
        // so it runs right before the restart.
        EditorInterface.Singleton.GetEditorUndoRedo().CallDeferred(EditorUndoRedoManager.MethodName.ClearHistory,
            (int)EditorUndoRedoManager.SpecialHistory.GlobalHistory, false);
        EditorInterface.Singleton.CallDeferred(EditorInterface.MethodName.RestartEditor, true);
    }

    /// <summary>
    /// Frees GodotTools' <c>HotReloadAssemblyWatcher</c> (a child of its editor plugin), which reloads the project
    /// assembly when a newer build is on disk and the editor window gains focus. Restarting closes windows, which focuses
    /// the editor while it shuts down, and Godot 4.7.2 crashed reloading the assembly then, so it never started again.
    /// A restart loads the new build anyway, and nothing else may reload once one was requested; returns whether a
    /// watcher was found.
    /// </summary>
    internal static bool StopAssemblyWatcher(IEnumerable<Node> pluginNodes)
    {
        var stopped = false;
        foreach (var plugin in pluginNodes)
        {
            if (plugin.GetNodeOrNull(AssemblyWatcherName) is not { } watcher) continue;
            plugin.RemoveChild(watcher);
            watcher.Free();
            stopped = true;
        }
        return stopped;
    }

    private const string AssemblyWatcherName = "HotReloadAssemblyWatcher";

    private sealed class UnfocusedImportSuspension : IDisposable
    {
        // Recent Godot versions keep the setting under behavior/; older ones do not.
        private static readonly string[] Keys = ["interface/editor/behavior/import_resources_when_unfocused", "interface/editor/import_resources_when_unfocused"];
        private readonly string[] _suspended;
        private UnfocusedImportSuspension(string[] suspended) => _suspended = suspended;

        public static UnfocusedImportSuspension Begin()
        {
            var settings = EditorInterface.Singleton.GetEditorSettings();
            var suspended = Keys.Where(key => settings.HasSetting(key) && settings.GetSetting(key).AsBool()).ToArray();
            foreach (var key in suspended) settings.SetSetting(key, false);
            return new(suspended);
        }

        public void Dispose()
        {
            var settings = EditorInterface.Singleton.GetEditorSettings();
            foreach (var key in _suspended) settings.SetSetting(key, true);
        }
    }

    public string? GetAutoload(string name) =>
        ProjectSettings.HasSetting("autoload/" + name)
            ? ProjectSettings.GetSetting("autoload/" + name, "").AsString().TrimStart('*')
            : null;

    public void AddAutoload(EditorPlugin plugin, string name, string path) => plugin.AddAutoloadSingleton(name, path);

    public void RemoveAutoload(EditorPlugin plugin, string name) => plugin.RemoveAutoloadSingleton(name);

    public bool IsExtensionLoaded(string file) => GDExtensionManager.IsExtensionLoaded(ResourcePath(file));

    public bool LoadExtension(string file) => GDExtensionManager.LoadExtension(ResourcePath(file)) == GDExtensionManager.LoadStatus.Ok;

    public bool UnloadExtension(string file) => GDExtensionManager.UnloadExtension(ResourcePath(file)) == GDExtensionManager.LoadStatus.Ok;

    // GDExtensionManager knows the extensions it loaded by their res:// path.
    private static string ResourcePath(string file) => ProjectSettings.LocalizePath(file.Replace('\\', '/'));

    /// <summary>
    /// The <c>res://</c> path of a <c>dotnet/project/solution_directory</c> value, as GodotTools reads it: <c>res://</c>
    /// while empty, else prefixed with <c>res://</c> unless it starts with it.
    /// </summary>
    internal static string SolutionDirectoryPath(string setting) =>
        string.IsNullOrEmpty(setting) ? "res://"
        : setting.StartsWith("res://", StringComparison.Ordinal) ? setting
        : "res://" + setting;

    /// <summary>The plugin slugs of <c>editor_plugins/enabled</c> entries (<c>res://addons/&lt;slug&gt;/plugin.cfg</c>).</summary>
    internal static IReadOnlySet<string> ParseEnabledPlugins(IEnumerable<string> paths) =>
        paths.Where(path => path.StartsWith("res://addons/", StringComparison.Ordinal) &&
                            path.EndsWith("/plugin.cfg", StringComparison.Ordinal))
            .Select(path => path["res://addons/".Length..^"/plugin.cfg".Length])
            .Where(slug => slug.Length > 0 && !slug.Contains('/') && !slug.Contains('\\'))
            .ToHashSet(StringComparer.Ordinal);
}
#endif
