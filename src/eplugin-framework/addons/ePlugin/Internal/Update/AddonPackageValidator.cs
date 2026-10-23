#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;

namespace Enaweg.Plugin.Internal.Update;

internal enum FindingSeverity { Info, Warning, Error }
internal sealed record Finding(string Code, FindingSeverity Severity, string Message, bool RequiresTrust = false);
/// <param name="AllowDowngrade">The user explicitly chose an older version, e.g. to undo a broken update.</param>
/// <param name="AllowReinstall">
/// The user explicitly chose the installed version again, e.g. to get new commits of a pinned git branch or to repair
/// changed addon files.
/// </param>
internal sealed record ValidationContext(PluginUpdateTarget Installed, UpdateCandidate Candidate, string StagingDir, bool AllowDowngrade = false,
    bool AllowReinstall = false);
internal interface IAddonRule { IEnumerable<Finding> Check(ValidationContext context); }
/// <param name="Kind">The kind whose workflow installs the package, see <see cref="PluginKinds.ForPackage"/>.</param>
internal sealed record ValidatedPackage(UpdateCandidate Candidate, string StagingDir, SemVer NewVersion,
    string? NewUpdateUrl, bool ContainsCSharp, IReadOnlyList<Finding> Findings, PluginKind Kind = PluginKind.Unknown)
{
    public bool IsValid => Findings.All(f => f.Severity != FindingSeverity.Error);
}
internal sealed class AddonPackageValidator(IEnumerable<IAddonRule>? extraRules = null)
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
        if (files.Length == 0) Add("R1", FindingSeverity.Error, "Staging folder is missing or empty.");
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
            if (addon) Add("R24", FindingSeverity.Info, "The new version has a plugin.cfg, so it becomes a plugin. Enable it in the ePlugin Manager after the update.");
        }
        var script = metadata.GetValueOrDefault("script") ?? "";
        try { if (!configless && !File.Exists(PackageFiles.Inside(stage, script))) Add("R4", FindingSeverity.Error, "Plugin script is missing."); }
        catch (InvalidDataException) { Add("R4", FindingSeverity.Error, "Plugin script must be a relative path inside the addon."); }
        if (candidate.Slug != installed.Slug || Path.GetFileName(stage) != installed.Slug)
            Add("R5", FindingSeverity.Error, "Staged addon slug differs from the installed slug.");
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
        if (addon && string.IsNullOrWhiteSpace(installed.InstalledVersion))
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
        if (validNew && validAnnounced && version.CompareTo(announced) != 0 && !mislabeled)
            Add("R7", FindingSeverity.Warning, "Package version differs from the announced version.");
        // An add-on's name is its slug or the built-in list's name, never one from its files.
        if (!addon && metadata.GetValueOrDefault("name") != installed.Name) Add("R8", FindingSeverity.Warning, "Plugin name changed.");
        var newUrl = metadata.GetValueOrDefault("update_url");
        var ownRepository = UpdateSourceFactory.Repository(installed.UpdateUrl);
        if (newUrl != installed.UpdateUrl)
        {
            // Checks keep using an update site the project sets, so the new plugin.cfg source is not trusted for anything.
            // Otherwise later updates come from the new one; another repository may belong to someone else, also on the
            // same host.
            if (installed.OverrideUrl is { } site)
                Add("R9", FindingSeverity.Warning, $"Update source changed: {newUrl ?? "removed"}. Updates still come from the project's update site {site}.");
            else
                Add("R9", FindingSeverity.Warning, "Update source changed: " + (newUrl ?? "removed"),
                    !string.IsNullOrWhiteSpace(newUrl) && UpdateSourceFactory.Repository(newUrl) != ownRepository);
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
        findings.AddRange(PluginKinds.Workflow(kind).Validate(new(context, files, installedFiles)));
        long bytes = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(stage, file);
            var name = Path.GetFileName(file);
            if (name.Equals("project.godot", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) ||
                (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) && !File.Exists(Path.Combine(installed.Directory, relative)))
                Add("R11", FindingSeverity.Error, "Package introduces a forbidden project/config file: " + relative);
            var length = new FileInfo(file).Length;
            if (length > PackageFiles.MaximumFile || (bytes += length) > PackageFiles.MaximumTotal)
                Add("R12", FindingSeverity.Error, "Package exceeds size limits.");
        }
        if (files.Length > PackageFiles.MaximumFiles) Add("R12", FindingSeverity.Error, "Package contains too many files.");
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
}
#endif
