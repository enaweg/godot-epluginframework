#if TOOLS
using System.Runtime.Loader;
using Godot;

namespace Enaweg.Plugin.Internal;

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
        GD.Print("Unloading of AssemblyContext triggered!");
        _shouldProcess = false;
        _ePluginContext = null;
        if (_eventsRegistered)
        {
            _eventsRegistered = false;
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
}
#endif