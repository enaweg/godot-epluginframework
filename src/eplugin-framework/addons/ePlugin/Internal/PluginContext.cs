#if TOOLS
using System;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

[Tool]
internal sealed class PluginContext(IEEditorPlugin? plugin, EditorPlugin pluginBase, ILogger? logger)
{
    public string Slug { get; init; } = pluginBase.GetPluginSlug();
    
    public string Name { get; init; } = pluginBase.ReadMetadata()?.Name ?? pluginBase.GetPluginSlug();

    public EEditorPluginMetadata? Metadata { get; init; } = pluginBase.ReadMetadata();

    public string? Directory { get; init; } = pluginBase.GetPluginDirectory();

    public ILogger? Logger { get; init; } = logger;

    public IEEditorPlugin? Plugin { get; init; } = plugin;

    public EditorPlugin PluginBase { get; init; } = pluginBase;

    public IDotnetCli? Cli { get; init; } = EGlobal.Instance.GetCli(logger);

    public EEditorPluginState State { get; set; } = EEditorPluginState.Created;
    public EEditorPluginBuilder Builder { get; init; } = EEditorPluginBuilder.Create();

    public bool IsRecipeCreated { get; set; } = false;

    public uint FailedTries { get; set; } = 0;

    public Exception? ErrorDetail { get; set; } = null;
}

#endif