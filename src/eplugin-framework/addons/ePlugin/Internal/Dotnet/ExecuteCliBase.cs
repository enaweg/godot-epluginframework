#if TOOLS
using System;
using System.Linq;
using Enaweg.Plugin.Logging;
using Godot;
using Array = Godot.Collections.Array;

namespace Enaweg.Plugin.Internal.Dotnet;

public abstract class ExecuteCliBase
{
    protected (int, string[]) ExecuteCall(ILogger logger, string cmd, string[] args)
    {
        var pathToSolution = ProjectSettings.GlobalizePath("res://");

        try
        {
            var result = new Array();
            logger.Log($"Executing: {cmd} {string.Join(" ", args).Replace(pathToSolution, "<project>/")}");
            var exitVal = OS.Execute(cmd, args, result, true, false);

            var final = result.Select(e => e.ToString()).ToArray();
            result.Dispose();

            return (exitVal, final);
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
        }

        return (-1, []);
    }
}
#endif