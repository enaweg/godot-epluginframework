#if TOOLS
using System;
using Enaweg.Plugin.Logging;
using GContainer.addons.ePlugin;

namespace Enaweg.Plugin.Internal;

internal sealed class PluginContext(EEditorPlugin plugin)
{
    public string Slug { get; init; } = plugin.PluginSlug;
    public string Name { get; init; } = plugin.Metadata?.Name ?? plugin.PluginSlug;
    public string Version { get; init; } = plugin.Metadata?.Version ?? string.Empty;
    public string Description { get; init; } = plugin.Metadata?.Description ?? string.Empty;

    public ILogger Logger { get; init; } = plugin.Logger;

    public EEditorPlugin? Plugin { get; set; } = plugin;
    public EEditorPluginState State { get; set; } = EEditorPluginState.Created;
    public EEditorPluginBuilder Builder { get; init; } = EEditorPluginBuilder.Create();

    public bool IsFirstActivation { get; set; }

    public uint FailedTries { get; set; } = 0;

    public Exception? ErrorDetail { get; set; }
}

#endif