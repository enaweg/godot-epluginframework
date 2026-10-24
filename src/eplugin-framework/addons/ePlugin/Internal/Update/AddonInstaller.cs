#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Enaweg.Plugin.Internal.Dotnet;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>What installing a new add-on needs from the editor; <see cref="EGlobal"/> provides it, tests fake it.</summary>
internal interface IAddonInstallHost
{
    /// <summary>Builds the project; only called for a package with C# code.</summary>
    BuildOutcome Build();
    void Scan();
    void Log(string message);
}

/// <param name="Installed">The add-on is in <c>res://addons</c> now; otherwise nothing of it is.</param>
/// <param name="Failure">Why it was not installed; null when it was.</param>
/// <param name="Built">The project was built with the add-on's C# code, so the editor reloads the assembly.</param>
/// <param name="BuildLog">The log of a failed build, kept in the staging folder.</param>
internal sealed record AddonInstallResult(bool Installed, string? Failure = null, bool Built = false, string? BuildLog = null);

/// <summary>
/// Installs an add-on that is not in the project yet from a ZIP file the user chose in the ePlugin Manager: staged and
/// validated like an update's package, then moved to <c>res://addons/&lt;slug&gt;</c> in one step. Nothing in the project
/// refers to a new add-on and it stays disabled, so there is no recipe to reconcile and nothing to back up; a package
/// with C# code is built at once, and removed again when the project does not build with it.
/// </summary>
internal sealed class AddonInstaller(string addonsDirectory, string projectRoot, IAddonInstallHost host)
{
    /// <summary>The folder the add-on of <paramref name="slug"/> is installed to.</summary>
    public static string Target(string addonsDirectory, string slug) => PackageFiles.Inside(addonsDirectory, slug);

    /// <summary>Extracts the archive into the transaction folder's staging and validates it as a new add-on.</summary>
    /// <param name="target">The add-on's folder below <c>res://addons</c>, without a version (see <see cref="ValidationContext.NewInstall"/>).</param>
    public static ValidatedPackage Stage(AddonArchive archive, PluginUpdateTarget target, string transactionDirectory, CancellationToken ct)
    {
        var stage = PackageFiles.Inside(Path.Combine(transactionDirectory, "staging"), archive.Slug);
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        SafeZipExtractor.Extract(archive.ZipPath, archive.Slug, stage, ct, requireSlugFolder: true);
        return new AddonPackageValidator().Validate(new(target, archive.Candidate(archive.Name, ""), stage, NewInstall: true));
    }

    public AddonInstallResult Install(ValidatedPackage package, string transactionDirectory)
    {
        if (!package.IsValid) throw new InvalidOperationException("The package was refused; it cannot be installed.");
        var slug = package.Candidate.Slug;
        var name = package.Candidate.PluginName;
        var target = Target(addonsDirectory, slug);
        // Validation saw no folder there, but it ran in the background; nothing installed meanwhile is replaced.
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"res://addons/{slug} exists already; nothing was installed.");
        Directory.CreateDirectory(addonsDirectory);
        AddonFiles.MoveDirectory(package.StagingDir, target);
        var version = package.Candidate.NewVersion.Length == 0 ? "" : " " + package.Candidate.NewVersion;
        host.Log($"Installed {name}{version} into res://addons/{slug}.");
        if (package.ContainsCSharp)
        {
            var build = host.Build();
            if (build.ExitCode != 0)
            {
                var log = Path.Combine(transactionDirectory, "build-install.log");
                try { File.WriteAllLines(log, build.Output); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log = null; }
                // The failed build kept the editor's assembly (BuildKeepingOutput), so taking the files away is enough.
                var removed = Remove(target, package.StagingDir);
                var failure = BuildFailure(name, build.Output, target) + (removed ? " It was removed again." : $" Remove res://addons/{slug} by hand.");
                host.Log(failure + (log is null ? "" : " Build log: " + log));
                return new(false, failure, BuildLog: log);
            }
        }
        host.Scan();
        return new(true, Built: package.ContainsCSharp);
    }

    /// <summary>Moves the add-on back to its staging folder, which is deleted with the transaction folder.</summary>
    private bool Remove(string target, string staging)
    {
        try
        {
            AddonFiles.MoveDirectory(target, staging);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                AddonFiles.DeleteTree(target);
                return true;
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                host.Log($"Cannot remove {target}: {ex.Message}; {again.Message}");
                return false;
            }
        }
    }

    /// <summary>Why the project does not build with the new add-on: errors in its own code, or elsewhere.</summary>
    private string BuildFailure(string name, IReadOnlyList<string> output, string target)
    {
        var errors = BuildDiagnostics.Parse(output);
        var inside = errors.Where(e => e.File is { } file && IsBelow(file, target)).ToArray();
        if (inside.Length > 0) return $"{name} does not compile in this project ({Count(inside.Length)}), e.g. {Describe(inside[0])}.";
        if (errors.Count > 0)
            return $"The project does not build with {name}: {Count(errors.Count)} outside the plugin, e.g. {Describe(errors[0])}. " +
                   "Fix them, then install the plugin again.";
        return $"The project does not build with {name}; see the build log.";
    }

    private bool IsBelow(string file, string directory)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, PackageFiles.PathComparison);
    }

    private string Describe(BuildError error)
    {
        var file = error.File is null ? null
            : Path.GetRelativePath(projectRoot, Path.IsPathRooted(error.File) ? error.File : Path.Combine(projectRoot, error.File)).Replace('\\', '/');
        var location = file is null ? "" : error.Line > 0 ? $"{file}({error.Line}): " : file + ": ";
        // The sentence it is put in ends with its own period.
        return $"{location}{error.Code}: {error.Message.TrimEnd('.')}";
    }

    private static string Count(int errors) => errors == 1 ? "1 error" : $"{errors} errors";
}
#endif
