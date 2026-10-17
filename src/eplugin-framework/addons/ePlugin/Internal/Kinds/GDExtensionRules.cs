#if TOOLS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// The validation every kind that ships a GDExtension shares (<see cref="GDExtensionPluginWorkflow"/> and
/// <see cref="EPluginWorkflow"/>): an update can replace only native libraries inside the plugin folder, the package must
/// contain the library the editor here loads, and a linked folder cannot be swapped file by file.
/// </summary>
internal static class GDExtensionRules
{
    public static IEnumerable<Finding> Validate(KindValidation validation)
    {
        var context = validation.Context;
        var slug = context.Installed.Slug;
        if (Directory.Exists(context.Installed.Directory) && (File.GetAttributes(context.Installed.Directory) & FileAttributes.ReparsePoint) != 0)
            yield return new("R20", FindingSeverity.Error, "The plugin folder is a link; update the plugin where it points to.");
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
            var forThisEditor = config.Libraries.Where(l => GDExtensionConfig.MatchesThisEditor(l.Feature)).ToArray();
            if (forThisEditor.Length == 0)
                yield return new("R19", FindingSeverity.Warning, $"{file} has no library for this editor's platform; Godot will not load the extension.");
            else if (!forThisEditor.Any(l => GDExtensionConfig.InsidePlugin(slug, file, l.Path) is { } path && Exists(context.StagingDir, path)))
                yield return new("R19", FindingSeverity.Error, $"The library of {file} for this platform is missing from the package: {forThisEditor[0].Path}.");
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
