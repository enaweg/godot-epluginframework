#if TOOLS
namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>Exit-status reporting for recipe operations without changing the public IDotnetCli API.</summary>
internal interface ICheckedDotnetCli
{
    bool TryRebuildSolution();
    bool TryAddProjectToSolution(string projectPath, string? virtualFolderName);
    bool TryRemoveProjectFromSolution(string projectPath);
    bool TryAddProjectReference(string projectReference);
    bool TryRemoveProjectReference(string projectReference);
    bool TryRemoveNugetFromProject(string nugetName);
}
#endif
