#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>Where the errors of a failed build are, as far as its output tells.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum BuildFailureCause
{
    /// <summary>An error without a source file, in a project file or in generated code, or no error could be read.</summary>
    Unknown,
    /// <summary>Only errors that may pass on another attempt: a locked output file, an unreachable package source.</summary>
    Transient,
    /// <summary>An error in plugin code: below <c>addons/</c> or in a project a recipe declares.</summary>
    PluginCode,
    /// <summary>Every error is in the project's own C# files.</summary>
    ProjectCode,
}

/// <summary>One error of a build, in MSBuild's canonical format.</summary>
internal sealed record BuildError(string? File, int Line, int Column, string Code, string Message);

internal sealed record BuildDiagnosis(BuildFailureCause Cause, IReadOnlyList<BuildError> Errors, IReadOnlyList<BuildError> ProjectErrors);

/// <summary>
/// Reads the errors of a failed build and tells whether they are all in the project's own code, so an update whose
/// plugins still compile need not be rolled back. The <c>error CODE:</c> part of a diagnostic is not localized, its
/// message is.
/// </summary>
internal static partial class BuildDiagnostics
{
    /// <summary>Project errors kept for the journal and the dialogs; the build log has all of them.</summary>
    public const int MaxProjectErrors = 200;

    // [node>]origin[(line[,col[,endLine,endCol]])] : [subcategory ]error CODE: message [project]; logs of parallel builds
    // start each line with the MSBuild node, e.g. "1:7>".
    [GeneratedRegex(@"^\s*(?:\d+(?::\d+)?>)?(?<origin>.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s*(?:[^:]*\s)?error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+)\])?\s*$")]
    private static partial Regex ErrorLine();

    // Locked output files and unreachable package sources: another attempt may pass.
    private static readonly HashSet<string> TransientCodes = new(StringComparer.OrdinalIgnoreCase) { "MSB3021", "MSB3026", "MSB3027", "CS2012", "NU1301" };

    /// <summary>The distinct errors of a build's output; an element may hold several lines.</summary>
    public static IReadOnlyList<BuildError> Parse(IEnumerable<string> output)
    {
        var errors = new List<BuildError>();
        var seen = new HashSet<BuildError>();
        foreach (var line in output.SelectMany(o => o.Split('\n')))
        {
            var match = ErrorLine().Match(line.TrimStart('﻿').TrimEnd('\r'));
            if (!match.Success) continue;
            var origin = match.Groups["origin"].Value.Trim();
            // A tool name such as CSC, MSBUILD or EXEC rather than a file.
            var file = origin.Length == 0 || !LooksLikePath(origin) ? null : origin;
            var error = new BuildError(file, Number(match.Groups["line"].Value), Number(match.Groups["column"].Value), match.Groups["code"].Value, match.Groups["message"].Value);
            // dotnet build repeats every error in its summary.
            if (seen.Add(error)) errors.Add(error);
        }
        return errors;
    }

    /// <param name="projectRoot">The Godot project directory.</param>
    /// <param name="pluginRoots">Further plugin code directories: where symlinked addons point to, recipe projects
    /// outside <c>addons/</c>.</param>
    /// <param name="fileExists">Errors in files that do not exist are in generated code.</param>
    public static BuildDiagnosis Classify(IEnumerable<string> output, string projectRoot, IEnumerable<string>? pluginRoots = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var errors = Parse(output);
        var roots = new[] { Path.Combine(projectRoot, "addons") }.Concat(pluginRoots ?? []).Select(Directory).ToArray();
        var excluded = new[] { Path.Combine(projectRoot, ".godot") }.Select(Directory).ToArray();
        var causes = errors.Select(e => Cause(e, projectRoot, roots, excluded, fileExists)).ToArray();
        var cause = causes.Length == 0 ? BuildFailureCause.Unknown
            : causes.Contains(BuildFailureCause.PluginCode) ? BuildFailureCause.PluginCode
            : causes.Contains(BuildFailureCause.Unknown) ? BuildFailureCause.Unknown
            : causes.Contains(BuildFailureCause.Transient) ? BuildFailureCause.Transient
            : BuildFailureCause.ProjectCode;
        var project = errors.Where((_, i) => causes[i] == BuildFailureCause.ProjectCode).Take(MaxProjectErrors)
            .Select(e => e with { File = Relative(projectRoot, e.File!) }).ToArray();
        return new(cause, errors, project);
    }

    private static BuildFailureCause Cause(BuildError error, string projectRoot, string[] pluginRoots, string[] excluded, Func<string, bool> fileExists)
    {
        if (TransientCodes.Contains(error.Code)) return BuildFailureCause.Transient;
        if (error.File is null || !error.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return BuildFailureCause.Unknown;
        string path;
        try { path = Path.GetFullPath(Path.IsPathRooted(error.File) ? error.File : Path.Combine(projectRoot, error.File)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return BuildFailureCause.Unknown; }
        if (pluginRoots.Any(root => Under(path, root))) return BuildFailureCause.PluginCode;
        if (excluded.Any(root => Under(path, root)) || IsIntermediate(path) || !fileExists(path)) return BuildFailureCause.Unknown;
        return BuildFailureCause.ProjectCode;
    }

    private static bool LooksLikePath(string origin) =>
        origin.Contains('/') || origin.Contains('\\') || origin.Contains('.');

    // Source generators may write their output below obj/ (EmitCompilerGeneratedFiles).
    private static bool IsIntermediate(string path) =>
        path.Replace('\\', '/').Split('/').Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Directory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static bool Under(string path, string directory) => path.StartsWith(directory, PathComparison);

    private static string Relative(string projectRoot, string file)
    {
        try
        {
            var path = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file));
            return Under(path, Directory(projectRoot)) ? Path.GetRelativePath(projectRoot, path).Replace('\\', '/') : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return file; }
    }

    private static int Number(string value) => int.TryParse(value, out var number) ? number : 0;

    /// <summary>An error as one line: <c>file(line,column): CODE message</c>.</summary>
    public static string Format(BuildError error) =>
        (error.File is null ? "" : error.Line > 0 ? $"{error.File}({error.Line},{error.Column}): " : error.File + ": ") + $"{error.Code} {error.Message}";
}
#endif
