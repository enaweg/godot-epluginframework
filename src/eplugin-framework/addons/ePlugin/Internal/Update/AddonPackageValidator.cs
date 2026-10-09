#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

internal enum FindingSeverity { Info, Warning, Error }
internal sealed record Finding(string Code, FindingSeverity Severity, string Message, bool RequiresTrust = false);
/// <param name="AllowDowngrade">The user explicitly chose an older version, e.g. to undo a broken update.</param>
internal sealed record ValidationContext(PluginUpdateTarget Installed, UpdateCandidate Candidate, string StagingDir, bool AllowDowngrade = false);
internal interface IAddonRule { IEnumerable<Finding> Check(ValidationContext context); }
internal sealed record ValidatedPackage(UpdateCandidate Candidate, string StagingDir, SemVer NewVersion,
    string? NewUpdateUrl, bool ContainsCSharp, IReadOnlyList<Finding> Findings)
{
    public bool IsValid => Findings.All(f => f.Severity != FindingSeverity.Error);
}
internal sealed class AddonPackageValidator(IEnumerable<IAddonRule>? extraRules = null)
{
    /// <summary>R10 when the files contain a GDExtension, which ePlugin updates cannot replace yet.</summary>
    public static Finding? NativeExtension(IEnumerable<string> files) =>
        files.Any(f => f.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase))
            ? new("R10", FindingSeverity.Error, "GDExtension plugins are not supported by ePlugin updates yet.") : null;

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
        try
        {
            metadata = PluginIni.Parse(File.ReadAllText(Path.Combine(stage, "plugin.cfg")));
            if (metadata.Count == 0) throw new InvalidDataException("Missing [plugin] section.");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { Add("R2", FindingSeverity.Error, "Cannot parse staged plugin.cfg: " + ex.Message); }
        foreach (var key in new[] { "name", "version", "script" })
            if (string.IsNullOrWhiteSpace(metadata.GetValueOrDefault(key))) Add("R3", FindingSeverity.Error, $"plugin.cfg has no {key}.");
        var script = metadata.GetValueOrDefault("script") ?? "";
        try { if (!File.Exists(PackageFiles.Inside(stage, script))) Add("R4", FindingSeverity.Error, "Plugin script is missing."); }
        catch (InvalidDataException) { Add("R4", FindingSeverity.Error, "Plugin script must be a relative path inside the addon."); }
        if (candidate.Slug != installed.Slug || Path.GetFileName(stage) != installed.Slug)
            Add("R5", FindingSeverity.Error, "Staged addon slug differs from the installed slug.");
        var validNew = SemVer.TryParse(metadata.GetValueOrDefault("version"), out var version);
        if (!validNew || !SemVer.TryParse(installed.InstalledVersion, out var old) || version.CompareTo(old) == 0)
            Add("R6", FindingSeverity.Error, "Package version must be newer than the installed version.");
        else if (version.CompareTo(old) < 0)
        {
            // Older framework releases cannot finish or recover the update transaction that installs them.
            if (!context.AllowDowngrade || installed.Slug == "ePlugin") Add("R6", FindingSeverity.Error, "Package version must be newer than the installed version.");
            else Add("R6", FindingSeverity.Info, $"Downgrade from {installed.InstalledVersion} to {version}.");
        }
        if (validNew && SemVer.TryParse(candidate.NewVersion, out var announced) && version.CompareTo(announced) != 0)
            Add("R7", FindingSeverity.Warning, "Package version differs from the announced version.");
        if (metadata.GetValueOrDefault("name") != installed.Name) Add("R8", FindingSeverity.Warning, "Plugin name changed.");
        var newUrl = metadata.GetValueOrDefault("update_url");
        if (newUrl != installed.UpdateUrl)
        {
            var oldHost = Uri.TryCreate(installed.UpdateUrl, UriKind.Absolute, out var before) ? before.Host : installed.UpdateUrl;
            var newHost = Uri.TryCreate(newUrl, UriKind.Absolute, out var after) ? after.Host : newUrl;
            Add("R9", FindingSeverity.Warning, "Update source changed: " + (newUrl ?? "removed"), !string.IsNullOrWhiteSpace(newUrl) && oldHost != newHost);
        }
        var installedFiles = Directory.Exists(installed.Directory) ? PackageFiles.Files(installed.Directory).ToArray() : [];
        if (NativeExtension(files.Concat(installedFiles)) is { } native) findings.Add(native);
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
        catch (IOException) { }
        if (validNew && SemVer.TryParse(installed.InstalledVersion, out var installedVersion) && version.Major != installedVersion.Major)
            Add("R14", FindingSeverity.Warning, "Major version change: your project code may need changes.");
        if (installed.IsBlocked || installed.StoreReadOnly) Add("R17", FindingSeverity.Error, "Resolve local state using Retry failed in the ePlugin Manager first.");
        foreach (var rule in extraRules ?? []) findings.AddRange(rule.Check(context));
        return new(candidate, stage, version, newUrl, files.Concat(installedFiles).Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)), findings);
    }
}
#endif
