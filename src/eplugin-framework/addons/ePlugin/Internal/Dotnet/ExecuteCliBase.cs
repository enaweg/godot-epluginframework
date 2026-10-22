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
            var exitCode = OS.Execute(command, OperatingSystem.IsWindows() ? arguments : arguments.Select(QuotedForShell).ToArray(), result, true, false);
            return (exitCode, result.Select(e => e.ToString()).ToArray());
        }
        finally
        {
            result.Dispose();
        }
    }

    /// <summary>
    /// An argument as OS.Execute must be given it outside Windows: when it reads the output, Godot joins the arguments in
    /// double quotes and runs the line through /bin/sh (popen), which would expand <c>$</c> and backticks and end the
    /// quote at <c>"</c>. A backslash makes those four characters literal inside double quotes.
    /// </summary>
    internal static string QuotedForShell(string argument) =>
        argument.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
}

internal abstract class ExecuteCliBase(ILogger? logger, bool enableDebugLogging, IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? GodotProcessRunner.Instance;

    /// <summary>The project directory, which the debug log shortens to <c>&lt;project&gt;</c>; null for commands without one.</summary>
    protected virtual string? LoggedProjectRoot => null;

    /// <param name="args">One argument per element and without quotes, also for paths containing spaces.</param>
    protected (int, string[]) ExecuteCall(string cmd, string[] args)
    {
        try
        {
            if (enableDebugLogging)
            {
                var line = string.Join(" ", args);
                if (LoggedProjectRoot is { } root)
                    line = line.Replace(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, $"<project>{Path.DirectorySeparatorChar}");
                logger?.Log($"Executing: {cmd} {line}");
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
