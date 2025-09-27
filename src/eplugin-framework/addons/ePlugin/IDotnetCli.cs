#if TOOLS
namespace Enaweg.Plugin;

public interface IDotnetCli
{
    void RebuildSolution();
    void RunTests();
    void RemoveProjectFromSolution(string projectPath);
    void AddProjectToSolution(string projectPath, string virtualFolderName = null);
    bool AddNugetToProject(string nugetName, string version = null, string source = null, bool prerelease = false);
    void RemoveNugetFromProject(string nugetName);
    void AddProjectReference(string projectReference);
    void RemoveProjectReference(string projectReference);

    public (int, string[]) Execute(string command, string[] args);
}
#endif