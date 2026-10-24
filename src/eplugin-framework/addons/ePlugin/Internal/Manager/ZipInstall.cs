#if TOOLS
using System.Globalization;
using System.Linq;
using System.Text;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// What a ZIP file chosen with Install from ZIP installs: a new add-on (both null), a version of the installed plugin,
/// or nothing, with the reason.
/// </summary>
/// <param name="Version">The archive as a version of the installed plugin, installed like one chosen in its details.</param>
/// <param name="Refused">Why the archive cannot be installed.</param>
internal sealed record ZipChoice(VersionOption? Version, string? Refused)
{
    public bool IsNew => Version is null && Refused is null;
}

/// <summary>
/// Installing an add-on from a ZIP file, like the Godot Asset Library's Import: the decision what the file installs and
/// the confirmation's text. Pure, so it is tested without the editor.
/// </summary>
internal static class ZipInstall
{
    /// <param name="row">The listed plugin of the archive's slug; null when the project has none.</param>
    /// <param name="target">The plugin as the update system sees it; null unless it is enabled with a readable plugin.cfg.</param>
    public static ZipChoice Choose(AddonArchive archive, PluginRow? row, PluginUpdateTarget? target)
    {
        if (row is null) return new(null, null);
        var name = row.Plugin.Name;
        if (row.Plugin.Missing) return new(null, $"res://addons/{archive.Slug} is missing, but {name} has a failed attempt; resolve it under Needs attention first.");
        if (archive.IsExtensionAddon)
            return new(null, $"{name} is installed in res://addons/{archive.Slug}. The ZIP file has no plugin.cfg, so it names no version to install over it; " +
                             "choose a release in the plugin's details instead.");
        // An archive's version is always comparable; an installed add-on's may be unknown.
        var known = SemVer.TryParse(row.Plugin.Version, out var installed) & SemVer.TryParse(archive.Version, out var version);
        var option = new VersionOption(archive.Version, archive.Candidate(target?.Name ?? name, target?.InstalledVersion ?? row.Plugin.Version),
            known && version.CompareTo(installed) == 0, known && version.CompareTo(installed) < 0,
            row.Plugin.Kind == PluginKind.GDExtensionAddon && row.Plugin.Version.Length == 0);
        if (PluginManagerViewModel.VersionChangeBlocked(row, option) is { } blocked)
            return new(null, $"{name} is installed in res://addons/{archive.Slug}, and the ZIP file can only replace its version: {blocked}");
        if (target is null) return new(null, $"{name} is installed in res://addons/{archive.Slug}, but its plugin.cfg cannot be read.");
        return new(option, null);
    }

    /// <summary>The confirmation of a new add-on as BBCode. All package supplied text is escaped.</summary>
    public static string Describe(ValidatedPackage package, AddonArchive archive)
    {
        var text = new StringBuilder();
        var version = archive.Version.Length == 0 ? "" : " " + archive.Version;
        text.Append(CultureInfo.CurrentCulture, $"Install [b]{Escape(archive.Name)}{Escape(version)}[/b] into res://addons/{Escape(archive.Slug)}?\n\n");
        text.Append(CultureInfo.CurrentCulture, $"[b]ZIP file:[/b] {Escape(archive.ZipPath)}\n");
        text.Append(CultureInfo.CurrentCulture, $"[b]Type:[/b] {Escape(KindName(package.Kind))}\n");
        text.Append(CultureInfo.CurrentCulture, $"[b]Files:[/b] {archive.Files} from {Escape(archive.Root)}");
        if (archive.LeftOut > 0)
            text.Append(CultureInfo.CurrentCulture, $"; {archive.LeftOut} other file{(archive.LeftOut == 1 ? "" : "s")} of the ZIP file {(archive.LeftOut == 1 ? "lies" : "lie")} outside it and {(archive.LeftOut == 1 ? "is" : "are")} not installed");
        text.Append("\n\n");
        text.Append(package.Kind == PluginKind.GDExtensionAddon
            ? "Godot always loads a GDExtension without plugin.cfg; there is nothing to enable."
            : "The plugin is installed disabled: enable it in the list afterwards, which shows its license first if it asks for one.");
        if (package.ContainsCSharp)
            text.Append(" It contains C# code, so the project is built right away. If the project does not build with it, it is removed again.");
        text.Append('\n');
        var findings = package.Findings.Where(f => f.Severity != FindingSeverity.Error).ToArray();
        if (findings.Length > 0) text.Append('\n');
        foreach (var finding in findings)
            text.Append(CultureInfo.CurrentCulture, $"[color={(finding.Severity == FindingSeverity.Warning ? TextColors.Warning : TextColors.Info)}]{finding.Severity}:[/color] {Escape(finding.Message)}\n");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The kind validation could tell from the files: an ePlugin is a C# plugin until its code is compiled, which is when
    /// the list shows it as one.
    /// </summary>
    private static string KindName(PluginKind kind) => kind is PluginKind.CSharp ? "C# plugin or ePlugin" : PluginCatalog.KindName(kind);

    private static string Escape(string value) => PluginManagerViewModel.Escape(value);
}
#endif
