#if TOOLS
using Enaweg.Plugin.Internal;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public static class EEditorPluginExtensions
{
    public static IDotnet Cli(this EEditorPlugin editorPlugin)
    {
        var cli = EGlobal.Instance.GetCli(editorPlugin);
        cli.UseLogger(editorPlugin.Logger);
        return cli;
    }

    public static bool AddNuget(this EEditorPlugin plugin, string nugetName, string? version = null,
        string? source = null)
    {
        return plugin.Cli().Call.AddNugetToProject(nugetName, version, source);
    }

    public static void RemoveNuget(this EEditorPlugin plugin, params string[] nugetNames)
    {
        foreach (var nugetName in nugetNames)
        {
            plugin.Cli().Call.RemoveNugetFromProject(nugetName);
        }
    }

    public static void AddProject(this EEditorPlugin plugin, string projectPath, string? virtualFolderName = null,
        bool addReference = true)
    {
        plugin.Cli().Call.AddProjectToSolution(projectPath, virtualFolderName);
        if (addReference)
        {
            plugin.Cli().Call.AddProjectReference(projectPath);
        }
    }

    public static void RemoveProject(this EEditorPlugin plugin, params string[] projectPaths)
    {
        foreach (var projectPath in projectPaths)
        {
            plugin.Cli().Call.RemoveProjectReference(projectPath);
            plugin.Cli().Call.RemoveProjectFromSolution(projectPath);
        }
    }

    public static void AddProjectReference(this EEditorPlugin plugin, params string[] projectReference)
    {
        foreach (var reference in projectReference)
        {
            plugin.Cli().Call.AddProjectReference(reference);
        }
    }

    public static void RemoveProjectReference(this EEditorPlugin plugin, params string[] projectReference)
    {
        foreach (var reference in projectReference)
        {
            plugin.Cli().Call.RemoveProjectReference(reference);
        }
    }

    public static void RebuildAll(this EEditorPlugin plugin)
    {
        plugin.Cli().Call.RebuildSolution();
    }
}
#endif