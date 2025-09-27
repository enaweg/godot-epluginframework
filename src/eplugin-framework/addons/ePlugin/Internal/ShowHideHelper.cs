#if TOOLS
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

public static class ShowHideHelper
{
    public static void ShowDirectory(EEditorPlugin plugin, string directory, ILogger logger)
    {
        var pathToHideFile = ProjectSettings.GlobalizePath(directory);
        var dirInfo = new DirectoryInfo(pathToHideFile);
        var baseDirectory = ProjectSettings.GlobalizePath(plugin.PluginDirectory);

        if (dirInfo.Exists)
        {
            dirInfo.Attributes = FileAttributes.Normal;
            if (dirInfo.Name.StartsWith('.'))
            {
                // remove for posix systems
                var newPath = Path.Combine(dirInfo.Parent.FullName, dirInfo.Name.Substring(1));
                logger.Log(
                    $"Showing: <plugin>{dirInfo.FullName.Replace(baseDirectory, "")} (to <plugin>{newPath.Replace(baseDirectory, "")})");
                dirInfo.MoveTo(newPath);
            }
        }
    }

    public static void HideDirectory(EEditorPlugin plugin, string directory, ILogger logger)
    {
        var pathToHideFile = ProjectSettings.GlobalizePath(directory);
        var dirname = Path.GetDirectoryName(pathToHideFile);
        var filename = Path.GetFileName(pathToHideFile);
        var baseDirectory = ProjectSettings.GlobalizePath(plugin.PluginDirectory);

        string pathToShownFile;
        if (filename.StartsWith('.'))
        {
            pathToShownFile = Path.Combine(dirname, $"{filename.Substring(1)}");
        }
        else
        {
            pathToShownFile = pathToHideFile;
        }

        var dirInfoShown = new DirectoryInfo(pathToShownFile);
        if (dirInfoShown.Exists)
        {
            dirInfoShown.Attributes = FileAttributes.Hidden;
            if (!dirInfoShown.Name.StartsWith('.'))
            {
                // prefix with '.' for posix systems
                var newPath = Path.Combine(dirInfoShown.Parent.FullName, $".{dirInfoShown.Name}");
                logger.Log(
                    $"Hiding: <plugin>{dirInfoShown.FullName.Replace(baseDirectory, "")} (to <plugin>{newPath.Replace(baseDirectory, "")})");
                dirInfoShown.MoveTo(newPath);
            }
        }
    }
}
#endif