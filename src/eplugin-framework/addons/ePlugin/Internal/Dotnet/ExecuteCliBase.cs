#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Logging;
using Godot;
using Array = Godot.Collections.Array;

namespace Enaweg.Plugin.Internal.Dotnet;

public abstract class ExecuteCliBase(ILogger? logger, bool enableDebugLogging)
{
    protected (int, string[]) ExecuteCall(string cmd, string[] args)
    {
        var pathToSolution = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));

        try
        {
            // OS.Execute accepts one argument per array element. Splitting on spaces corrupts
            // solution and project paths such as "EPlugin Framework.sln".
            var finalArgs = System.Array.ConvertAll(args, a =>
                a.Length >= 2 && a[0] == '"' && a[^1] == '"' ? a[1..^1] : a);
            var result = new Array();
            if (enableDebugLogging)
            {
                logger?.Log(
                    $"Executing: {cmd} {string.Join(" ", finalArgs).Replace(pathToSolution, $"<project>{Path.DirectorySeparatorChar}")}");
            }

            var exitVal = OS.Execute(cmd, finalArgs, result, true, false);

            var final = result.Select(e => e.ToString()).ToArray();
            result.Dispose();

            if (exitVal != 0)
            {
                logger?.Error($"Command {cmd} failed with exit code {exitVal}: {string.Join("\n", final)}");
            }

            return (exitVal, final);
        }
        catch (Exception ex)
        {
            logger?.Error(ex.Message);
        }

        return (-1, []);
    }
}
#endif
