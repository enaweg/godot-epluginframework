#if TOOLS
using System;
using System.Threading;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Update.UI;
using System.Runtime.Loader;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public sealed partial class EPluginPlugin : EditorPlugin, IEPlugin
{
    private const string UpdateMenuName = "Update ePlugin addons...";
    private const string CheckMenuName = "Check for ePlugin addon updates";
    private bool _updateMenuAdded;
    private UpdateDialog? _updateDialog;
    private UpdateFailureDialog? _failureDialog;
    private EGlobal? _updateOwner;
    private const string RetryMenuName = "Retry failed ePlugin addons";
    private bool _retryMenuAdded;
    private CancellationTokenSource _updateLifetime = new();
    internal CancellationToken UpdateLifetime => _updateLifetime.Token;
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
        if (!_updateMenuAdded && EGlobal.Instance.IsValid())
        {
            RemoveToolMenuItem(UpdateMenuName); RemoveToolMenuItem(CheckMenuName);
            AddToolMenuItem(UpdateMenuName, Callable.From(OpenUpdates));
            AddToolMenuItem(CheckMenuName, Callable.From(CheckUpdates));
            _updateMenuAdded = true;
        }
    }

    public override void _DisablePlugin()
    {
        RemoveUpdateMenus();
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
        RemoveUpdateMenus();
        _updateLifetime.Cancel();
        _updateLifetime.Dispose();
        if (_retryMenuAdded)
        {
            RemoveToolMenuItem(RetryMenuName);
            _retryMenuAdded = false;
        }

        base._ExitTree();
    }
    
    private void OpenUpdates()
    {
        if (_updateDialog is null || !GodotObject.IsInstanceValid(_updateDialog))
        {
            _updateDialog = new UpdateDialog();
            _updateDialog.Initialize(EGlobal.Instance, UpdateLifetime);
            EditorInterface.Singleton.GetBaseControl().AddChild(_updateDialog);
        }
        _updateDialog.Open();
    }
    private async void CheckUpdates()
    {
        await EGlobal.Instance.CheckForUpdatesAsync(true);
        if (_updateDialog is not null && GodotObject.IsInstanceValid(_updateDialog)) _updateDialog.Refresh();
    }
    private void ShowUpdateFailure(UpdateJournal journal)
    {
        try
        {
            _failureDialog = new UpdateFailureDialog();
            _failureDialog.Initialize(journal, keep => EGlobal.Instance.DecideUpdate(journal, keep));
            EditorInterface.Singleton.GetBaseControl().AddChild(_failureDialog);
            _failureDialog.Open();
        }
        catch (Exception ex) { Logger.Error($"Cannot show update decision: {ex.Message}"); EGlobal.Instance.DecideUpdate(journal, false); }
    }
    private void RemoveUpdateMenus()
    {
        if (_updateMenuAdded) { RemoveToolMenuItem(UpdateMenuName); RemoveToolMenuItem(CheckMenuName); _updateMenuAdded = false; }
        if (_updateOwner is not null) _updateOwner.UpdateDecisionNeeded -= ShowUpdateFailure;
        if (_updateDialog is not null && GodotObject.IsInstanceValid(_updateDialog)) _updateDialog.QueueFree();
        if (_failureDialog is not null && GodotObject.IsInstanceValid(_failureDialog)) _failureDialog.QueueFree();
        _updateDialog = null; _failureDialog = null;
    }

    private void InitializeInternals()
    {
        _retryMenuAdded = false; _updateMenuAdded = false;
        _updateOwner = EGlobal.Instance;
        _updateOwner.UpdateDecisionNeeded -= ShowUpdateFailure;
        _updateOwner.UpdateDecisionNeeded += ShowUpdateFailure;
        var context = AssemblyLoadContext.GetLoadContext(typeof(EPluginPlugin).Assembly);
        if (context is not null) context.Unloading += _ => { try { _updateLifetime.Cancel(); } catch (ObjectDisposedException) { } };
        UpdateSettings.Register();
        EGlobal.Instance.Initialize(this, new GenericLoggerFactory(category => new GodotConsoleLogger(category)));
    }
}
#endif
