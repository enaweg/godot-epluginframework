#if TOOLS
using Enaweg.Plugin.Internal;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public static class IEEditorPluginExtensions
{
    public static IDotnet Cli(this IEEditorPlugin editorPlugin)
    {
        var context = EGlobal.Instance.GetContext(editorPlugin);
        
        var cli = EGlobal.Instance.GetCli(editorPlugin);
        cli.UseLogger(context?.Logger);
        return cli;
    }

    public static bool AddNuget(this IEEditorPlugin plugin, string nugetName, string? version = null,
        string? source = null)
    {
        return plugin.Cli().Call.AddNugetToProject(nugetName, version, source);
    }

    public static void RemoveNuget(this IEEditorPlugin plugin, params string[] nugetNames)
    {
        foreach (var nugetName in nugetNames)
        {
            plugin.Cli().Call.RemoveNugetFromProject(nugetName);
        }
    }

    public static void AddProject(this IEEditorPlugin plugin, string projectPath, string? virtualFolderName = null,
        bool addReference = true)
    {
        plugin.Cli().Call.AddProjectToSolution(projectPath, virtualFolderName);
        if (addReference)
        {
            plugin.Cli().Call.AddProjectReference(projectPath);
        }
    }

    public static void RemoveProject(this IEEditorPlugin plugin, params string[] projectPaths)
    {
        foreach (var projectPath in projectPaths)
        {
            plugin.Cli().Call.RemoveProjectReference(projectPath);
            plugin.Cli().Call.RemoveProjectFromSolution(projectPath);
        }
    }

    public static void AddProjectReference(this IEEditorPlugin plugin, params string[] projectReference)
    {
        foreach (var reference in projectReference)
        {
            plugin.Cli().Call.AddProjectReference(reference);
        }
    }

    public static void RemoveProjectReference(this IEEditorPlugin plugin, params string[] projectReference)
    {
        foreach (var reference in projectReference)
        {
            plugin.Cli().Call.RemoveProjectReference(reference);
        }
    }

    public static void RebuildAll(this IEEditorPlugin plugin)
    {
        plugin.Cli().Call.RebuildSolution();
    }
}
#endif