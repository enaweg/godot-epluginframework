#if TOOLS
using System;
using Enaweg.Plugin.Logging;
using GContainer.addons.ePlugin;
using Godot;

namespace Enaweg.Plugin.Internal;

[Tool]
internal sealed class PluginContext(IEEditorPlugin plugin, ILogger? logger)
{
    public string Slug { get; init; } = plugin.GodotPlugin.GetPluginSlug();
    public string Name { get; init; } = plugin.GodotPlugin.ReadMetadata()?.Name ?? plugin.GodotPlugin.GetPluginSlug();
    public string Version { get; init; } = plugin.GodotPlugin.ReadMetadata()?.Version ?? string.Empty;
    public string Description { get; init; } = plugin.GodotPlugin.ReadMetadata()?.Description ?? string.Empty;

    public string Directory { get; init; } = plugin.GodotPlugin.GetPluginDirectory();
    
    public ILogger? Logger { get; init; } = logger;

    public IEEditorPlugin? Plugin { get; set; } = plugin;
    public EEditorPluginState State { get; set; } = EEditorPluginState.Created;
    public EEditorPluginBuilder Builder { get; init; } = EEditorPluginBuilder.Create();

    public bool IsFirstActivation { get; set; }

    public uint FailedTries { get; set; } = 0;

    public Exception? ErrorDetail { get; set; }
}

#endif