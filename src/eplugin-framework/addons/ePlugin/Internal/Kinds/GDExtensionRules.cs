#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// The validation every kind that ships a GDExtension shares (<see cref="GDExtensionPluginWorkflow"/> and
/// <see cref="EPluginWorkflow"/>): an update can replace only native libraries inside the plugin folder, the package must
/// contain the library the editor here loads and suit its Godot version, and it should not drop a platform the installed
/// version supports.
/// </summary>
internal static class GDExtensionRules
{
    public static IEnumerable<Finding> Validate(KindValidation validation)
    {
        var context = validation.Context;
        var slug = context.Installed.Slug;
        var installed = GDExtensionConfig.ReadAll(context.Installed.Directory);
        foreach (var (file, config) in GDExtensionConfig.ReadAll(context.StagingDir))
        {
            if (config is null)
            {
                yield return new("R18", FindingSeverity.Error, $"Cannot read {file}.");
                continue;
            }
            foreach (var library in config.Libraries.Concat(config.Dependencies))
                if (GDExtensionConfig.InsidePlugin(slug, file, library.Path) is null)
                    yield return new("R18", FindingSeverity.Error, $"{file} loads {library.Path} from outside the plugin folder; an update cannot replace it.");
            foreach (var icon in config.Icons)
                if (GDExtensionConfig.InsidePlugin(slug, file, icon.Path) is null)
                    yield return new("R18", FindingSeverity.Warning, $"{file} uses the icon {icon.Path} of {icon.Feature} from outside the plugin folder; an update does not replace it.");
            if (config.CompatibilityProblem() is { } problem)
                yield return new("R22", FindingSeverity.Error, $"This editor cannot load {file}: {problem}.");
            var forThisEditor = config.Libraries.Where(l => GDExtensionConfig.MatchesThisEditor(l.Feature)).ToArray();
            if (forThisEditor.Length == 0)
                yield return new("R19", FindingSeverity.Warning, $"{file} has no library for this editor's platform; Godot will not load the extension.");
            else if (!forThisEditor.Any(l => GDExtensionConfig.InsidePlugin(slug, file, l.Path) is { } path && Exists(context.StagingDir, path)))
                yield return new("R19", FindingSeverity.Error, $"The library of {file} for this platform is missing from the package: {forThisEditor[0].Path}.");
            // Other team members, or the exported game, may run on a platform this editor does not check.
            if (installed.GetValueOrDefault(file) is { } before)
                foreach (var platform in GDExtensionConfig.DroppedPlatforms(before, config))
                    yield return new("R23", FindingSeverity.Warning, $"{file} no longer ships {platform}; Godot there cannot load the extension after the update.");
        }
    }

    private static bool Exists(string root, string relative)
    {
        try
        {
            var path = PackageFiles.Inside(root, relative);
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (InvalidDataException) { return false; }
    }
}
#endif
