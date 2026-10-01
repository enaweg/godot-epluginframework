#if TOOLS
using System;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public sealed partial class EPluginPlugin : EditorPlugin, IEPlugin
{
    private const string RetryMenuName = "Retry failed ePlugin addons";
    private bool _retryMenuAdded;
    public bool EnableDebugLogging => false;

    private ILogger? _logger = null;

    public ILogger Logger
    {
        get
        {
            _logger ??= new GodotConsoleLogger(this.GetPluginSlug());

            return _logger;
        }
        set => _logger = value;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (!EGlobal.Instance.IsValid())
        {
            // after an assembly reload the EnterTree, EnablePlugin and Ready are not triggered anymore but all C#
            // state is lost. This will reinitialize the ePlugin Framework.
            InitializeInternals();
        }

        if (!_retryMenuAdded && EGlobal.Instance.IsValid())
        {
            // A C# assembly reload recreates this field while the native editor node survives.
            RemoveToolMenuItem(RetryMenuName);
            AddToolMenuItem(RetryMenuName, Callable.From(() => EGlobal.Instance.RetryFailedPlugins()));
            _retryMenuAdded = true;
        }
    }

    public override void _DisablePlugin()
    {
        EGlobal.Instance.RecordFrameworkDisabled(this);
        if (_retryMenuAdded)
        {
            RemoveToolMenuItem(RetryMenuName);
            _retryMenuAdded = false;
        }

        base._DisablePlugin();
    }

    public override void _ExitTree()
    {
        if (_retryMenuAdded)
        {
            RemoveToolMenuItem(RetryMenuName);
            _retryMenuAdded = false;
        }

        base._ExitTree();
    }
    
    private void InitializeInternals()
    {
        EGlobal.Instance.Initialize(this, new GenericLoggerFactory(category => new GodotConsoleLogger(category)));
    }
}
#endif
