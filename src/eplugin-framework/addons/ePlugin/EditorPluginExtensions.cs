#if TOOLS
using System;
using System.IO;
using Enaweg.Plugin.Internal;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin;

/// <summary>
/// Helper extensions for <see cref="EditorPlugin"/> that resolve plugin identity and metadata
/// from the plugin's on-disk layout (its <c>res://addons/&lt;slug&gt;/</c> directory and
/// <c>plugin.cfg</c>).
/// </summary>
[Tool]
public static class EditorPluginExtensions
{
    /// <summary>
    /// Returns the plugin's slug — the directory name under <c>res://addons/</c> that contains it
    /// (e.g. <c>"my-plugin"</c> for <c>res://addons/my-plugin/MyPlugin.cs</c>).
    /// </summary>
    /// <param name="plugin">The editor plugin to inspect.</param>
    /// <returns>The leaf directory name of the plugin's install location.</returns>
    public static string GetPluginSlug(this EditorPlugin plugin)
    {
        var path = GetPluginDirectory(plugin);
        if (path is not null)
        {
            return Path.GetFileName(path);
        }

        return string.IsNullOrEmpty(plugin.Name) ? $"plugin-{plugin.GetInstanceId()}" : plugin.Name;
    }

    /// <summary>
    /// Returns the absolute <c>res://</c> directory that hosts the plugin, derived from the
    /// plugin's <see cref="CSharpScript"/> location.
    /// </summary>
    /// <param name="plugin">The editor plugin to inspect.</param>
    /// <returns>
    /// The directory portion of the script path (e.g. <c>res://addons/my-plugin</c> for a script
    /// at <c>res://addons/my-plugin/MyPlugin.cs</c>).
    /// </returns>
    public static string? GetPluginDirectory(this EditorPlugin plugin)
    {
        try
        {
            var scriptVar = plugin.GetScript();
            if (scriptVar.VariantType is Variant.Type.Nil)
            {
                return null;
            }

            var script = (Script)scriptVar;
            if (script is not null)
            {
                return script.GetPath().GetBaseDir();
            }

            return null;
        }
        catch (Exception)
        {
            // Ignore
        }

        return null;
    }

    /// <summary>
    /// Returns the <see cref="IDotnetCli"/> wrapper bound to this plugin's context.
    /// </summary>
    /// <param name="plugin">The Godot editor plugin requesting CLI access.</param>
    /// <returns>
    /// An <see cref="IDotnetCli"/> instance scoped to the plugin's logger, or <see langword="null"/>
    /// when the framework has not yet been initialized (for example, during the very early stages
    /// of an assembly reload before <c>_Process</c> re-runs the bootstrap).
    /// </returns>
    public static IDotnetCli? Cli(this EditorPlugin plugin)
    {
        var context = EGlobal.Instance.GetOrCreateContext(plugin);
        return context.Cli;
    }

    /// <summary>
    /// Reads the <c>plugin.cfg</c> sitting alongside the plugin and returns its metadata.
    /// </summary>
    /// <param name="plugin">The editor plugin whose <c>plugin.cfg</c> should be parsed.</param>
    /// <returns>
    /// The parsed <see cref="EEditorPluginMetadata"/>, or <see langword="null"/> when the file is
    /// missing, fails to load, or throws while being read. Read failures are logged via
    /// <see cref="GD.PrintErr(object[])"/>.
    /// </returns>
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