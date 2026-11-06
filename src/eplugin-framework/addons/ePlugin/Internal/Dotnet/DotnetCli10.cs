#if TOOLS
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>The dotnet CLI from .NET 10: <c>dotnet package|reference add|remove ... --project &lt;project&gt;</c>.</summary>
internal sealed class DotnetCli10(ILogger? logger, bool enableDebugLogging, string projectRoot, string? assemblyName = null,
    IProcessRunner? runner = null, string? solutionDirectory = null)
    : DotnetCliBase(logger, enableDebugLogging, projectRoot, assemblyName, runner, solutionDirectory)
{
    protected override string[] AddPackageArguments(string nugetName) => ["package", "add", nugetName, "--project", GodotProjectPath];

    protected override string[] RemovePackageArguments(string nugetName) => ["package", "remove", nugetName, "--project", GodotProjectPath];

    protected override string[] AddReferenceArguments(string projectFile) => ["reference", "add", projectFile, "--project", GodotProjectPath];

    protected override string[] RemoveReferenceArguments(string projectFile) => ["reference", "remove", projectFile, "--project", GodotProjectPath];
}
#endif
