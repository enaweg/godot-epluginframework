#if TOOLS
using System.IO;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

[Tool]
public static class ShowHideHelper
{
    internal static void ShowDirectory(PluginContext plugin, string directory)
    {
        var pathToHideFile = ProjectSettings.GlobalizePath(directory);
        var dirInfo = new DirectoryInfo(pathToHideFile);
        var baseDirectory = ProjectSettings.GlobalizePath(plugin.Directory);

        if (dirInfo.Exists)
        {
            dirInfo.Attributes = FileAttributes.Normal;
            if (dirInfo.Name.StartsWith('.'))
            {
                // remove for posix systems
                var newPath = Path.Combine(dirInfo.Parent.FullName, dirInfo.Name.Substring(1));
                plugin.Logger.Log(
                    $"Showing: <plugin>{dirInfo.FullName.Replace(baseDirectory, "")} (to <plugin>{newPath.Replace(baseDirectory, "")})");
                dirInfo.MoveTo(newPath);
            }
        }
    }

    internal static void HideDirectory(PluginContext plugin, string directory)
    {
        var pathToHideFile = ProjectSettings.GlobalizePath(directory);
        var dirname = Path.GetDirectoryName(pathToHideFile);
        var filename = Path.GetFileName(pathToHideFile);
        var baseDirectory = ProjectSettings.GlobalizePath(plugin.Directory);

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


                plugin.Logger.Log(
                    $"Hiding: <plugin>{dirInfoShown.FullName.Replace(baseDirectory, "")} (to <plugin>{newPath.Replace(baseDirectory, "")})");
                dirInfoShown.MoveTo(newPath);
            }
        }
    }
}
#endif