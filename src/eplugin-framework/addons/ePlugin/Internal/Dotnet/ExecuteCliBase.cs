#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Logging;
using Godot;
using Array = Godot.Collections.Array;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>Runs a command and returns its exit code and output lines; tests replace it.</summary>
internal interface IProcessRunner
{
    /// <param name="arguments">One argument per element, unquoted: the runner quotes them for the platform.</param>
    (int ExitCode, string[] Output) Run(string command, string[] arguments);
}

/// <summary>Runs commands through <see cref="OS.Execute"/>, which blocks the editor until they end.</summary>
internal sealed class GodotProcessRunner : IProcessRunner
{
    public static GodotProcessRunner Instance { get; } = new();

    public (int ExitCode, string[] Output) Run(string command, string[] arguments)
    {
        var result = new Array();
        try
        {
            // OS.Execute takes one argument per element and quotes those containing spaces itself.
            var exitCode = OS.Execute(command, arguments, result, true, false);
            return (exitCode, result.Select(e => e.ToString()).ToArray());
        }
        finally
        {
            result.Dispose();
        }
    }
}

internal abstract class ExecuteCliBase(ILogger? logger, bool enableDebugLogging, IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? GodotProcessRunner.Instance;

    /// <param name="args">One argument per element and without quotes, also for paths containing spaces.</param>
    protected (int, string[]) ExecuteCall(string cmd, string[] args)
    {
        try
        {
            if (enableDebugLogging)
            {
                var pathToSolution = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
                logger?.Log(
                    $"Executing: {cmd} {string.Join(" ", args).Replace(pathToSolution, $"<project>{Path.DirectorySeparatorChar}")}");
            }

            var (exitVal, output) = _runner.Run(cmd, args);
            if (exitVal != 0)
            {
                logger?.Error($"Command {cmd} failed with exit code {exitVal}: {string.Join("\n", output)}");
            }

            return (exitVal, output);
        }
        catch (Exception ex)
        {
            logger?.Error(ex.Message);
        }

        return (-1, []);
    }
}
#endif
