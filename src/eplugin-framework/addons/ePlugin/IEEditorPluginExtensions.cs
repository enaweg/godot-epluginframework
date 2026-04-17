#if TOOLS
using Enaweg.Plugin.Internal;
using EPluginFramework.addons.ePlugin.Internal;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public static class IEEditorPluginExtensions
{
    extension(IEEditorPlugin ePlugin)
    {
        public IEEditorPluginService EPluginService => new IntegrationWrapper(ePlugin);
        
        public IDotnet Cli()
        {
            var context = EGlobal.Instance.GetContext(ePlugin);

            var cli = EGlobal.Instance.GetCli(ePlugin);
            cli.UseLogger(context?.Logger);
            return cli;
        }

        public bool AddNuget(string nugetName, string? version = null,
            string? source = null)
        {
            return ePlugin.Cli().Call.AddNugetToProject(nugetName, version, source);
        }

        public void RemoveNuget(params string[] nugetNames)
        {
            foreach (var nugetName in nugetNames)
            {
                ePlugin.Cli().Call.RemoveNugetFromProject(nugetName);
            }
        }

        public void AddProject(string projectPath, string? virtualFolderName = null,
            bool addReference = true)
        {
            ePlugin.Cli().Call.AddProjectToSolution(projectPath, virtualFolderName);
            if (addReference)
            {
                ePlugin.Cli().Call.AddProjectReference(projectPath);
            }
        }

        public void RemoveProject(params string[] projectPaths)
        {
            foreach (var projectPath in projectPaths)
            {
                ePlugin.Cli().Call.RemoveProjectReference(projectPath);
                ePlugin.Cli().Call.RemoveProjectFromSolution(projectPath);
            }
        }

        public void AddProjectReference(params string[] projectReference)
        {
            foreach (var reference in projectReference)
            {
                ePlugin.Cli().Call.AddProjectReference(reference);
            }
        }

        public void RemoveProjectReference(params string[] projectReference)
        {
            foreach (var reference in projectReference)
            {
                ePlugin.Cli().Call.RemoveProjectReference(reference);
            }
        }

        public void RebuildAll()
        {
            ePlugin.Cli().Call.RebuildSolution();
        }
    }
}
#endif