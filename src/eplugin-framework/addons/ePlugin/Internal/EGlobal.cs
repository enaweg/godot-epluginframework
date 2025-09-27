#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// This is a internally used class by Enaweg.Plugin and should not be used by anything else. It manages plugin states
/// as well as provides some global values.
/// </summary>
[Tool]
internal sealed class EGlobal
{
    private static EGlobal? _instance = null;

    public static EGlobal Instance
    {
        get
        {
            _instance ??= new EGlobal();

            return _instance;
        }
    }


    private readonly List<PluginContext> _contexts = [];
    private EPluginPlugin? _ePluginContext = null;

    private bool _eventsRegistered = false;
    private bool _shouldProcess = false;


    private EGlobal()
    {
    }

    internal void StartProcessing(EPluginPlugin ePlugin)
    {
        if (_ePluginContext is not null)
        {
            // already running
            return;
        }

        _ePluginContext = ePlugin;
        StartProcessing();
    }

    /// <summary>
    /// Free all references as these will prevent proper reload of assembly after changes.
    /// </summary>
    /// <param name="context"></param>
    private void StopProcessingInternal(AssemblyLoadContext? context)
    {
        
        _shouldProcess = false;
        _ePluginContext = null;
        _contexts.Clear();
        if (_eventsRegistered)
        {
            _eventsRegistered = false;
            EditorInterface.Singleton.GetEditorMainScreen().GetTree().ProcessFrame -= GlobalProcessor;
            AssemblyLoadContext.GetLoadContext(GetType().Assembly).Unloading -= StopProcessingInternal;
        }
    }

    private void StartProcessing()
    {
        if (!_eventsRegistered)
        {
            AssemblyLoadContext.GetLoadContext(GetType().Assembly).Unloading += StopProcessingInternal;
            EditorInterface.Singleton.GetEditorMainScreen().GetTree().ProcessFrame += GlobalProcessor;
            _eventsRegistered = true;
        }

        _shouldProcess = true;
    }

    internal void StopProcessing()
    {
        _shouldProcess = false;
    }

    private void GlobalProcessor()
    {
        if (_ePluginContext is null)
        {
            return;
        }

        if (!_shouldProcess)
        {
            StopProcessingInternal(null);
        }
    }

    internal void EnsureEEditorPluginEnabled()
    {
        if (!EditorInterface.Singleton.IsPluginEnabled("ePlugin"))
        {
            EditorInterface.Singleton.SetPluginEnabled("ePlugin", true);
        }
    }
    
    /// <summary>
    /// If assembly reload is triggered based on code changes the contexts are lost. Need to rebuild it from active plugins.
    /// </summary>
    private void ReloadContexts()
    {
        _contexts.Clear();

        var parentNode = EditorInterface.Singleton.GetBaseControl().GetParent();
        var children = parentNode.GetChildren();

        // get ePlugin base
        var ePluginNode = children.FirstOrDefault(c => c is EPluginPlugin);
        if (ePluginNode is null)
        {
            return;
        }

        _ePluginContext = (EPluginPlugin)ePluginNode;

        // handle other plugins
        foreach (var childNode in children)
        {
            if (childNode is null)
            {
                continue;
            }

            if (childNode is EEditorPlugin editorPlugin)
            {
                var context = new PluginContext(editorPlugin);
                context.State = EEditorPluginState.Activated;
                _contexts.Add(context);
            }
        }
    }
}
#endif