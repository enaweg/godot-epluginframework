#if TOOLS
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>The dotnet CLI before .NET 10: <c>dotnet add|remove &lt;project&gt; package|reference</c>.</summary>
internal sealed class DotnetCli9(ILogger? logger, bool enableDebugLogging, string projectRoot, string? assemblyName = null,
    IProcessRunner? runner = null, string? solutionDirectory = null)
    : DotnetCliBase(logger, enableDebugLogging, projectRoot, assemblyName, runner, solutionDirectory)
{
    protected override string[] AddPackageArguments(string nugetName) => ["add", GodotProjectPath, "package", nugetName];

    protected override string[] RemovePackageArguments(string nugetName) => ["remove", GodotProjectPath, "package", nugetName];

    protected override string[] AddReferenceArguments(string projectFile) => ["add", GodotProjectPath, "reference", projectFile];

    protected override string[] RemoveReferenceArguments(string projectFile) => ["remove", GodotProjectPath, "reference", projectFile];
}
#endif
