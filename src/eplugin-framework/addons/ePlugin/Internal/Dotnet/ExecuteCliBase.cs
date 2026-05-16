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
            var finalArgs = args.SelectMany(a => a.Split(' ')).ToArray();
            var result = new Array();
            if (enableDebugLogging)
            {
                logger?.Log(
                    $"Executing: {cmd} {string.Join(" ", finalArgs).Replace(pathToSolution, $"<project>{Path.DirectorySeparatorChar}")}");
            }

            var exitVal = OS.Execute(cmd, finalArgs, result, true, false);

            var final = result.Select(e => e.ToString()).ToArray();
            result.Dispose();

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