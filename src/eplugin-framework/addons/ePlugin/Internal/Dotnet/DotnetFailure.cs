#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Explains why a dotnet command failed: its error lines and, for the failures users meet most, what they can do. Pure,
/// so it is tested without a dotnet installation.
/// </summary>
internal static partial class DotnetFailure
{
    /// <summary>The error lines a description keeps; the Output panel has all of them.</summary>
    public const int MaxLines = 5;

    /// <summary>
    /// What to do when the editor cannot run dotnet. Godot finds the .NET runtime by itself, so the editor runs C# code
    /// although the SDK's <c>dotnet</c> command is not on the PATH it was started with.
    /// </summary>
    public const string DotnetMissingHint =
        "The dotnet command could not be run. Install the .NET SDK (8 or newer) and make sure `dotnet` is on the PATH the " +
        "editor is started with (on macOS, an editor started from the Dock or Finder does not get the PATH of your shell: " +
        "start it from a terminal), then restart the editor and retry.";

    // NuGet, MSBuild and compiler codes; "error" alone also matches the build summary ("0 Error(s)"), which is skipped.
    [GeneratedRegex(@"\b(?:error|NU\d{4}|MSB\d{4}|CS\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"^\s*\d+\s+(?:Error|Warning)\(s\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"\bNU\d{4}\b|\bMSB\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Code();

    // Whether the shell, Windows or the process start reported that the dotnet executable is missing.
    private static readonly string[] MissingExecutable =
    [
        "dotnet: not found", "dotnet: command not found", "'dotnet' is not recognized", "No such file or directory",
        "cannot find the file specified"
    ];

    /// <summary>
    /// The error lines of a failed command and a hint, as plain text; null when the output says nothing. 127 is the
    /// shell's exit code for a command it cannot find, -1 Godot's for a process it cannot start.
    /// </summary>
    public static string? Describe(int exitCode, IReadOnlyList<string> output)
    {
        var lines = output.SelectMany(o => o.Split('\n')).Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToArray();
        var errors = lines.Where(l => ErrorLine().IsMatch(l) && !SummaryLine().IsMatch(l)).Distinct(StringComparer.Ordinal).ToArray();
        // Without a recognizable error line the end of the output is the most likely cause.
        var shown = (errors.Length > 0 ? errors : lines.TakeLast(3)).Take(MaxLines).ToList();
        if (errors.Length > MaxLines) shown.Add($"… and {errors.Length - MaxLines} more, see the Output panel.");
        var hint = Hint(exitCode, lines);
        if (shown.Count == 0 && hint is null) return null;
        return string.Join("\n", hint is null ? shown : shown.Append(hint));
    }

    /// <summary>What the user can do about the failure, or null when the output names no known cause.</summary>
    public static string? Hint(int exitCode, IEnumerable<string> lines)
    {
        var all = lines.ToArray();
        // Godot reports -1 when it cannot start the process at all.
        if (exitCode == -1 || exitCode == 127 && (all.Length == 0 || all.Any(l => MissingExecutable.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)))))
            return DotnetMissingHint;
        var codes = all.SelectMany(l => Code().Matches(l).Select(m => m.Value.ToUpperInvariant())).ToHashSet(StringComparer.Ordinal);
        if (codes.Contains("NU1301"))
            return "A NuGet package source could not be reached. Check your internet connection, proxy and the sources in " +
                   "nuget.config, then retry.";
        if (codes.Contains("NU1101"))
            return "The package was not found on any configured NuGet source. Check that its source is reachable; if it is, " +
                   "the plugin declares a package that does not exist: report it to the plugin's author.";
        if (codes.Contains("NU1102") || codes.Contains("NU1103"))
            return "The package version the plugin needs was not found on any configured NuGet source. Check the sources, " +
                   "or report it to the plugin's author.";
        if (codes.Contains("NU1202"))
            return "The package does not support the project's target framework. Report it to the plugin's author.";
        if (codes.Contains("NU1605") || codes.Contains("NU1107") || codes.Contains("NU1608"))
            return "The package conflicts with a version of a package the project or another plugin uses. Align the versions " +
                   "in the project's .csproj, then retry.";
        if (codes.Contains("MSB1009") || all.Any(l => l.Contains("Could not find project or directory", StringComparison.OrdinalIgnoreCase) ||
                                                       l.Contains("Could not find solution", StringComparison.OrdinalIgnoreCase)))
            return "A project or solution file was not found. Create the C# solution (Project > Tools > C# > Create C# " +
                   "solution) and check the project setting dotnet/project/solution_directory.";
        if (all.Any(l => l.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
                         l.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)))
            return "A file is locked or not writable. Close programs that use the project's files (another editor, a running " +
                   "game, an IDE build), check the file permissions, then retry.";
        return null;
    }
}
#endif
