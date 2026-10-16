#if TOOLS
namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>Exit-status reporting for recipe operations without changing the public IDotnetCli API.</summary>
internal sealed record BuildOutcome(int ExitCode, string[] Output);

internal interface ICheckedDotnetCli
{
    BuildOutcome TryBuild();
    /// <summary>Builds the Godot project with only plugin code, see <see cref="PluginsOnlyBuild"/>.</summary>
    BuildOutcome TryBuildPluginsOnly(PluginsOnlyBuild build);
    bool TryRebuildSolution();
    bool TryAddProjectToSolution(string projectPath, string? virtualFolderName);
    bool TryRemoveProjectFromSolution(string projectPath);
    bool TryAddProjectReference(string projectReference);
    bool TryRemoveProjectReference(string projectReference);
    bool TryRemoveNugetFromProject(string nugetName);
}
#endif
