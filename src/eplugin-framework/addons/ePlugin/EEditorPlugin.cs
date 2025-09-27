#if TOOLS
using System.IO;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin;

/// <summary>
/// ePlugin Editor Plugin class. This represents the entry point for simple plugins.
/// </summary>
[Tool]
public abstract partial class EEditorPlugin : EEditorPluginBase
{
    internal abstract void Bootstrap(IEEditorPluginBuilder builder);

    public override void _EnterTree()
    {
        base._EnterTree();

        EGlobal.Instance.EnsureEEditorPluginEnabled();
        EGlobal.Instance.Register(this);
    }

    public override void _EnablePlugin()
    {
        base._EnablePlugin();

        EGlobal.Instance.Activate(this);
    }

    public override void _DisablePlugin()
    {
        EGlobal.Instance.Deactivate(this);
        base._DisablePlugin();
    }
}

[Tool]
public abstract partial class EEditorPluginBase : EditorPlugin
{
    private ILogger? _logger;
    public string PluginSlug { get; private set; }

    public string PluginDirectory { get; private set; }

    private EEditorPluginMetadata? _metadata = null;

    public EEditorPluginMetadata? Metadata
    {
        get
        {
            _metadata ??= ReadMetadata();
            return _metadata;
        }
    }

    public ILogger Logger
    {
        get
        {
            if (_logger is null)
            {
                _logger = this.InitializeLogging();
            }

            return _logger;
        }
        private set => _logger = value;
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        PluginDirectory = ((CSharpScript)GetScript()).GetPath().GetBaseDir();
        PluginSlug = Path.GetFileName(PluginDirectory);
    }

    public override void _ExitTree()
    {
        base._ExitTree();
    }

    protected virtual ILogger InitializeLogging()
    {
        return new NullLogger();
    }

    private EEditorPluginMetadata? ReadMetadata()
    {
        var cfgFile = $"{PluginDirectory}/plugin.cfg";

        if (!FileAccess.FileExists(cfgFile))
        {
            return null;
        }

        var cfg = new ConfigFile();
        var error = cfg.Load(cfgFile);
        if (error is not Error.Ok)
        {
            return null;
        }

        var result = new EEditorPluginMetadata
        {
            Name = cfg.GetValue("plugin", "name").AsString(),
            Description = cfg.GetValue("plugin", "description").AsString(),
            Version = cfg.GetValue("plugin", "version").AsString(),
        };

        cfg.Dispose();

        return result;
    }
}
#endif