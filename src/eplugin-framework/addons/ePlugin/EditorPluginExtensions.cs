#if TOOLS
using System;
using System.IO;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin;

[Tool]
public static class EditorPluginExtensions
{
    public static string GetPluginSlug(this EditorPlugin plugin)
    {
        return Path.GetFileName(GetPluginDirectory(plugin));
    }

    public static string GetPluginDirectory(this EditorPlugin plugin)
    {
        return ((CSharpScript)plugin.GetScript()).GetPath().GetBaseDir();
    }

    /// <summary>
    /// Reads the metadata information of a plugin.
    /// </summary>
    /// <param name="plugin"></param>
    /// <returns></returns>
    public static EEditorPluginMetadata? ReadMetadata(this EditorPlugin plugin)
    {
        var cfgFile = $"{plugin.GetPluginDirectory()}/plugin.cfg";

        if (!FileAccess.FileExists(cfgFile))
        {
            return null;
        }

        EEditorPluginMetadata result;
        var cfg = new ConfigFile();
        try
        {
            var error = cfg.Load(cfgFile);
            if (error is not Error.Ok)
            {
                return null;
            }

            result = new EEditorPluginMetadata
            {
                Name = cfg.GetValue("plugin", "name").AsString(),
                Description = cfg.GetValue("plugin", "description").AsString(),
                Version = cfg.GetValue("plugin", "version").AsString(),
                Author = cfg.GetValue("plugin", "author").AsString(),
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to read metadata of plugin {plugin.Name} [{plugin.GetInstanceId()}]. {ex.Message}");
            return null;
        }
        finally
        {
            cfg.Dispose();
        }

        return result;
    }
}
#endif