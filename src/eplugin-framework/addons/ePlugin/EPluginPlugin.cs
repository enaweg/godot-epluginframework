#if TOOLS
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public sealed partial class EPluginPlugin : EditorPlugin
{
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

    public override void _EnterTree()
    {
        base._EnterTree();
        InitializeInternals();
    }

    public override void _DisablePlugin()
    {
        EGlobal.Instance.DeactivateAllEEditorPlugins();
        base._DisablePlugin();
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

        EGlobal.Instance.GlobalProcessor();
    }

    private void InitializeInternals()
    {
        EGlobal.Instance.Initialize(this, new GenericLoggerFactory(category => new GodotConsoleLogger(category)));
    }
}
#endif