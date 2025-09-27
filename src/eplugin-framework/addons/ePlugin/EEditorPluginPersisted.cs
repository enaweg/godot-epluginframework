#if TOOLS
using System;
using System.Text.Json;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin;

/// <summary>
/// ePlugin Editor Plugin class. This represents the entry point for more complex plugins. This additionally supports
/// persisted settings and version migration.
/// </summary>
/// <typeparam name="TPluginSettings"></typeparam>
[Tool]
public abstract partial class EEditorPluginPersisted<TPluginSettings> : EEditorPlugin
    where TPluginSettings : PluginSettingsBase, new()
{
    /// <summary>
    /// Persisted plugin settings
    /// </summary>
    public TPluginSettings? PluginSettings { get; protected set; }

    private string PluginDataFile => $"user://addons/{PluginSlug}/plugin.json";

    protected virtual void MigratePluginTo(string? oldVersion, string newVersion)
    {
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        LoadPluginSettings();
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();

        if (DidVersionChange())
        {
            var currentVersion = GetPluginVersion();
            Logger.Log($"Starting migration from plugin version {PluginSettings?.Version} to {currentVersion}.");
            MigratePluginTo(PluginSettings?.Version, currentVersion);
        }
    }

    public override void _DisablePlugin()
    {
        base._DisablePlugin();
    }

    public override void _ExitTree()
    {
        SavePluginSettings(GetPluginVersion());
        base._ExitTree();
    }

    private bool DidVersionChange()
    {
        return string.Equals(PluginSettings?.Version, GetPluginVersion(), StringComparison.Ordinal);
    }

    private void LoadPluginSettings()
    {
        using var dir = FileAccess.Open(PluginDataFile, FileAccess.ModeFlags.Read);
        PluginSettings = JsonSerializer.Deserialize<TPluginSettings>(dir?.GetAsText() ?? "{}");
        Logger.Log($"Loaded data from: <plugin>{PluginDataFile.Replace(PluginDirectory, "")}");
    }

    private void SavePluginSettings(string pluginVersion)
    {
        PluginSettings ??= new TPluginSettings();
        PluginSettings.Version = pluginVersion;

        using var dir = FileAccess.Open(PluginDataFile, FileAccess.ModeFlags.Write);
        dir?.StoreString(JsonSerializer.Serialize(PluginSettings));

        Logger.Log($"Saved data to: <plugin>{PluginDataFile.Replace(PluginDirectory, "")}");
    }
}
#endif