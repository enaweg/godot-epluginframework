#if TOOLS
using System;
using System.IO;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Keeps the folder the editor loads the project assembly from across a failed non-incremental build. Such a build is a
/// Rebuild, which cleans the outputs before compiling, so a compiler error leaves the folder empty: the running editor
/// keeps the assembly it loaded into memory, but the next start loads no C# at all (no ePlugin, no Retry), silently.
/// </summary>
internal static class BuildOutputGuard
{
    /// <summary>
    /// Runs <paramref name="build"/> after copying <paramref name="outputDirectory"/> to <paramref name="snapshotDirectory"/>.
    /// When the build fails and <paramref name="assemblyFile"/> is gone, the missing files are put back with their
    /// timestamps, so neither Godot's assembly watcher nor <see cref="AssemblyReload"/> takes them for a new build.
    /// Best effort: a snapshot that cannot be taken or restored is logged and never fails the build.
    /// </summary>
    /// <param name="keepNewAssembly">
    /// Whether a failed build that compiled the project's assembly anyway (an error in another project of the solution)
    /// keeps it; false puts the previous one back then too, e.g. when the code it was built with is removed again.
    /// </param>
    public static BuildOutcome Run(string outputDirectory, string snapshotDirectory, string assemblyFile, ILogger? logger, Func<BuildOutcome> build,
        bool keepNewAssembly = true)
    {
        var taken = false;
        try
        {
            if (File.Exists(Path.Combine(outputDirectory, assemblyFile)))
            {
                Delete(snapshotDirectory);
                Copy(outputDirectory, snapshotDirectory, overwrite: true);
                taken = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warn($"Cannot keep a copy of the built assembly before building; a failed build may leave the editor without it after a restart: {ex.Message}");
        }

        BuildOutcome outcome;
        try { outcome = build(); }
        catch { if (taken) Restore(outputDirectory, snapshotDirectory, assemblyFile, logger, keepNewAssembly); throw; }
        if (taken && outcome.ExitCode != 0) Restore(outputDirectory, snapshotDirectory, assemblyFile, logger, keepNewAssembly);
        try { Delete(snapshotDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger?.Warn($"Cannot remove {snapshotDirectory}: {ex.Message}"); }
        return outcome;
    }

    private static void Restore(string outputDirectory, string snapshotDirectory, string assemblyFile, ILogger? logger, bool keepNewAssembly)
    {
        // A failure after the project compiled (another project of the solution) leaves its new assembly, which stays
        // unless the caller takes its code away again.
        var compiled = File.Exists(Path.Combine(outputDirectory, assemblyFile));
        if (compiled && keepNewAssembly) return;
        try
        {
            Copy(snapshotDirectory, outputDirectory, overwrite: compiled);
            if (!compiled) logger?.Warn("The build failed and removed the built assembly; the previous one was put back so the editor still loads it after a restart.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Error(compiled
                ? $"The build failed after compiling the project, and the previous assembly could not be put back ({ex.Message}). The editor may load code that was removed again until the project is built."
                : $"The build failed and removed the built assembly, which could not be put back ({ex.Message}). Fix the build before restarting the editor, or no C# plugin will load.");
        }
    }

    /// <summary>Copies a folder with the files' modification times; <paramref name="overwrite"/> false keeps existing files.</summary>
    private static void Copy(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var to = Path.Combine(target, Path.GetFileName(file));
            if (!overwrite && File.Exists(to)) continue;
            File.Copy(file, to, overwrite);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(file));
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)), overwrite);
        }
    }

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
#endif
