#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Enaweg.Plugin.Internal.Kinds;

namespace Enaweg.Plugin.Internal.Update;

internal enum FindingSeverity { Info, Warning, Error }
internal sealed record Finding(string Code, FindingSeverity Severity, string Message, bool RequiresTrust = false);
/// <param name="AllowDowngrade">The user explicitly chose an older version, e.g. to undo a broken update.</param>
/// <param name="AllowReinstall">
/// The user explicitly chose the installed version again, e.g. to get new commits of a pinned git branch or to repair
/// changed addon files.
/// </param>
/// <param name="NewInstall">
/// The package installs an add-on that is not in the project yet, chosen as a ZIP file in the ePlugin Manager:
/// <paramref name="Installed"/> describes the folder it goes to, with no version, and nothing is compared with it.
/// </param>
internal sealed record ValidationContext(PluginUpdateTarget Installed, UpdateCandidate Candidate, string StagingDir, bool AllowDowngrade = false,
    bool AllowReinstall = false, bool NewInstall = false);
internal interface IAddonRule { IEnumerable<Finding> Check(ValidationContext context); }
/// <param name="Kind">The kind whose workflow installs the package, see <see cref="PluginKinds.ForPackage"/>.</param>
/// <param name="AcceptedLicense">
/// The source of the new version's license the user accepted before installing it, recorded once the update commits;
/// null when none was asked about.
/// </param>
internal sealed record ValidatedPackage(UpdateCandidate Candidate, string StagingDir, SemVer NewVersion,
    string? NewUpdateUrl, bool ContainsCSharp, IReadOnlyList<Finding> Findings, PluginKind Kind = PluginKind.Unknown,
    string? AcceptedLicense = null)
{
    public bool IsValid => Findings.All(f => f.Severity != FindingSeverity.Error);
}
internal sealed partial class AddonPackageValidator(IEnumerable<IAddonRule>? extraRules = null)
{
    public ValidatedPackage Validate(ValidationContext context)
    {
        var findings = new List<Finding>();
        void Add(string code, FindingSeverity severity, string message, bool trust = false) => findings.Add(new(code, severity, message, trust));
        var installed = context.Installed;
        var stage = context.StagingDir;
        var candidate = context.Candidate;
        var metadata = new Dictionary<string, string>();
        var files = Directory.Exists(stage) ? PackageFiles.Files(stage).ToArray() : [];
        if (files.Length == 0) Add("R1", FindingSeverity.Error, "The package has no files for the plugin. " + ReportToAuthor);
        // A GDExtension add-on has no plugin.cfg: its version is the announced one, and it has no entry script.
        var addon = installed.Kind == PluginKind.GDExtensionAddon;
        var configless = addon && !File.Exists(Path.Combine(stage, "plugin.cfg"));
        if (configless)
        {
            if (!files.Any(GDExtensionConfig.IsExtensionFile)) Add("R2", FindingSeverity.Error, "The package has neither a plugin.cfg nor a .gdextension file.");
        }
        else
        {
            try
            {
                metadata = PluginIni.Parse(File.ReadAllText(Path.Combine(stage, "plugin.cfg")));
                if (metadata.Count == 0) throw new InvalidDataException("Missing [plugin] section.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { Add("R2", FindingSeverity.Error, "Cannot parse staged plugin.cfg: " + ex.Message); }
            foreach (var key in new[] { "name", "version", "script" })
                if (string.IsNullOrWhiteSpace(metadata.GetValueOrDefault(key))) Add("R3", FindingSeverity.Error, $"plugin.cfg has no {key}.");
            if (addon && !context.NewInstall) Add("R24", FindingSeverity.Info, "The new version has a plugin.cfg, so it becomes a plugin. Enable it in the ePlugin Manager after the update.");
        }
        var script = metadata.GetValueOrDefault("script") ?? "";
        try { if (!configless && !File.Exists(PackageFiles.Inside(stage, script))) Add("R4", FindingSeverity.Error, "Plugin script is missing."); }
        catch (InvalidDataException) { Add("R4", FindingSeverity.Error, "Plugin script must be a relative path inside the addon."); }
        if (candidate.Slug != installed.Slug || Path.GetFileName(stage) != installed.Slug)
            Add("R5", FindingSeverity.Error, $"The package's plugin folder is not named '{installed.Slug}' like the installed plugin. " + ReportToAuthor);
        var packageVersion = configless ? candidate.NewVersion : metadata.GetValueOrDefault("version");
        var validNew = SemVer.TryParse(packageVersion, out var version);
        var versionProblem = configless
            ? $"Announced version '{candidate.NewVersion}' must be newer than installed version '{installed.InstalledVersion}'."
            : $"Package plugin.cfg version '{packageVersion ?? "missing"}' must be newer than installed version '{installed.InstalledVersion}' (announced: '{candidate.NewVersion}').";
        var validAnnounced = SemVer.TryParse(candidate.NewVersion, out var announced);
        var validOld = SemVer.TryParse(installed.InstalledVersion, out var old);
        // A release announced as an update whose plugin.cfg still names the installed or an older version, usually because
        // its author forgot to raise it. It is installed only after the review confirms it; an older framework never is,
        // because it cannot finish or recover the update transaction that installs it.
        var mislabeled = validNew && validOld && validAnnounced && announced.CompareTo(old) > 0 && version.CompareTo(old) <= 0 &&
                         !context.AllowReinstall && !context.AllowDowngrade && (version.CompareTo(old) == 0 || installed.Slug != "ePlugin");
        if (context.NewInstall)
        {
            if (configless) Add("R6", FindingSeverity.Info, "The add-on has no plugin.cfg, so its version stays unknown until an update installs a release of it.");
            else if (!validNew) Add("R6", FindingSeverity.Error, $"Package plugin.cfg version '{packageVersion ?? "missing"}' cannot be compared.");
            // Another add-on in the folder would be replaced without a backup.
            if (Directory.Exists(installed.Directory) || File.Exists(installed.Directory))
                Add("R26", FindingSeverity.Error, $"res://addons/{installed.Slug} exists already; install the ZIP file over the installed plugin instead.");
        }
        else if (addon && string.IsNullOrWhiteSpace(installed.InstalledVersion))
        {
            // Installed by hand: ePlugin never recorded its version, so any version may replace it.
            if (validNew) Add("R6", FindingSeverity.Info, $"The installed version is unknown, because the add-on has no plugin.cfg and ePlugin did not install it. Installing {version} replaces it.");
            else Add("R6", FindingSeverity.Error, $"Package version '{packageVersion ?? "missing"}' cannot be compared.");
        }
        else if (!validNew || !validOld)
            Add("R6", FindingSeverity.Error, versionProblem);
        else if (mislabeled)
            Add("R6", FindingSeverity.Warning, version.CompareTo(old) == 0
                ? $"The release is announced as version {candidate.NewVersion}, but its plugin.cfg still names the installed version {version}. Installing it replaces the installed files, and the version stays {version}."
                : $"The release is announced as version {candidate.NewVersion}, but its plugin.cfg names version {version}, older than the installed {installed.InstalledVersion}. Installing it downgrades the plugin to {version}.");
        else if (version.CompareTo(old) == 0)
        {
            if (!context.AllowReinstall) Add("R6", FindingSeverity.Error, versionProblem);
            else Add("R6", FindingSeverity.Info, $"Reinstall of version {version}.");
        }
        else if (version.CompareTo(old) < 0)
        {
            // Older framework releases cannot finish or recover the update transaction that installs them.
            if (!context.AllowDowngrade || installed.Slug == "ePlugin") Add("R6", FindingSeverity.Error, versionProblem);
            else Add("R6", FindingSeverity.Info, $"Downgrade from {installed.InstalledVersion} to {version}.");
        }
        // The R6 warning of a mislabeled release already says how the versions differ.
        if (validNew && validAnnounced && version.CompareTo(announced) != 0 && !mislabeled && !context.NewInstall)
            Add("R7", FindingSeverity.Warning, "Package version differs from the announced version.");
        // An add-on's name is its slug or the built-in list's name, never one from its files.
        if (!addon && !context.NewInstall && metadata.GetValueOrDefault("name") != installed.Name) Add("R8", FindingSeverity.Warning, "Plugin name changed.");
        var newUrl = metadata.GetValueOrDefault("update_url");
        var ownRepository = UpdateSourceFactory.Repository(installed.UpdateUrl);
        if (newUrl != installed.UpdateUrl && !context.NewInstall)
        {
            // Later updates come from the new update_url, with an update site the project sets whenever that one fails, and
            // R25 compares with it once it is installed. Another repository may belong to someone else, also on the same
            // host. Without one, they come from the built-in list's site for the slug, which may be another add-on.
            var removed = string.IsNullOrWhiteSpace(newUrl);
            var trust = removed ? ownRepository is not null : UpdateSourceFactory.Repository(newUrl) != ownRepository;
            var change = "Update source changed: " + (removed ? "removed" : newUrl);
            Add("R9", FindingSeverity.Warning, installed.OverrideUrl is { } site
                ? $"{change}. Updates still come from the project's update site {site} first, and from the new source when it fails."
                : change, trust);
        }
        // A package from the built-in list's site, used because the plugin's own one failed, may be someone else's plugin
        // with the same slug. The project's update site is the project's choice, and a plugin without an update site of
        // its own has only the built-in one.
        if (candidate.SourceUrl is { } source && ownRepository is not null && source != installed.OverrideUrl &&
            UpdateSourceFactory.Repository(source) != ownRepository)
            Add("R25", FindingSeverity.Warning, $"The update comes from {source}, not from the plugin's update site {installed.UpdateUrl}.", true);
        // A plugin folder linked to a working copy would be replaced by the downloaded copy, the link and all.
        if (Directory.Exists(installed.Directory) && (File.GetAttributes(installed.Directory) & FileAttributes.ReparsePoint) != 0)
            Add("R20", FindingSeverity.Error, "The plugin folder is a link; update the plugin where it points to.");
        var installedFiles = Directory.Exists(installed.Directory) ? PackageFiles.Files(installed.Directory).ToArray() : [];
        // What differs between plugin kinds, e.g. the native libraries of a GDExtension plugin.
        var kind = PluginKinds.ForPackage(installed, script, files, installedFiles);
        // R21 says how an update loads new native libraries; a new add-on has no old ones to replace.
        findings.AddRange(PluginKinds.Workflow(kind).Validate(new(context, files, installedFiles)).Where(f => !context.NewInstall || f.Code != "R21"));
        // E.g. the Godot 3 build a project releases next to its Godot 4 one, under the same version.
        if (Godot3Evidence(stage, files) is { } godot3) Add("R27", FindingSeverity.Error, $"The package is made for Godot 3, which Godot 4 cannot load: {godot3}.");
        long bytes = 0;
        var forbidden = new List<string>();
        string? tooLarge = null;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(stage, file);
            var name = Path.GetFileName(file);
            if (name.Equals("project.godot", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) ||
                (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) && !File.Exists(Path.Combine(installed.Directory, relative)))
                forbidden.Add(relative.Replace('\\', '/'));
            var length = new FileInfo(file).Length;
            if (length > PackageFiles.MaximumFile) tooLarge ??= $"{relative.Replace('\\', '/')} is larger than {Megabytes(PackageFiles.MaximumFile)}";
            bytes += length;
        }
        if (forbidden.Count > 0)
            Add("R11", FindingSeverity.Error, $"The package brings project or configuration files that would change the whole project: {string.Join(", ", forbidden)}. " + ReportToAuthor);
        // One finding for all limits, however many files exceed them.
        var limits = new[]
        {
            tooLarge,
            bytes > PackageFiles.MaximumTotal ? $"its files take {Megabytes(bytes)}, more than {Megabytes(PackageFiles.MaximumTotal)}" : null,
            files.Length > PackageFiles.MaximumFiles ? $"it has {files.Length} files, more than {PackageFiles.MaximumFiles}" : null
        }.OfType<string>().ToArray();
        if (limits.Length > 0)
            Add("R12", FindingSeverity.Error, $"The package exceeds the size limits of {PackageFiles.MaximumFiles} files, {Megabytes(PackageFiles.MaximumFile)} per file " +
                                              $"and {Megabytes(PackageFiles.MaximumTotal)} in total: {string.Join("; ", limits)}. " + ReportToAuthor);
        try
        {
            var oldMetadata = PluginIni.Parse(File.ReadAllText(Path.Combine(installed.Directory, "plugin.cfg")));
            if (Path.GetExtension(script) != Path.GetExtension(oldMetadata.GetValueOrDefault("script")))
                Add("R13", FindingSeverity.Warning, "Plugin entry-point language changed.");
        }
        // an installed plugin.cfg that cannot be read only skips this comparison
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        if (validNew && SemVer.TryParse(installed.InstalledVersion, out var installedVersion) && version.Major != installedVersion.Major)
            Add("R14", FindingSeverity.Warning, "Major version change: your project code may need changes.");
        if (installed.IsBlocked || installed.StoreReadOnly) Add("R17", FindingSeverity.Error, "Resolve the plugin's problem under Needs attention in the ePlugin Manager first.");
        foreach (var rule in extraRules ?? []) findings.AddRange(rule.Check(context));
        return new(candidate, stage, version, newUrl, files.Concat(installedFiles).Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)), findings, kind);
    }

    /// <summary>Added to refusals that only the plugin's author can fix, as the package itself is faulty.</summary>
    internal const string ReportToAuthor = "This is a packaging problem of the plugin; report it to the plugin's author.";

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0.#} MB";

    // GDScript that Godot 4 refuses to parse: a bare tool line and export and onready without the '@' of an annotation.
    [GeneratedRegex(@"^(?:tool[ \t]*\r?$|[ \t]*(?:export(?:\([^)\r\n]*\))?|onready)[ \t]+var\b)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Godot3Script();

    /// <summary>
    /// Why the staged package is made for Godot 3, or null: a GDNative library, which Godot 4 replaced by GDExtension, or
    /// a script in Godot 3's GDScript. ePlugin runs in Godot 4 only.
    /// </summary>
    internal static string? Godot3Evidence(string stage, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".gdnlib" or ".gdns") return $"{relative} is a GDNative library";
            if (extension != ".gd") continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (Godot3Script().Match(text) is { Success: true } match)
                return $"{relative} has the line \"{match.Value.Trim()}\", which Godot 4 writes with an annotation (@)";
        }
        return null;
    }
}
#endif
