#if TOOLS
using System;
using System.Threading;
using System.IO;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Manager;
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
    private const string ManagerMenuName = "ePlugin Manager...";
    // Engine metadata survives assembly reloads, unlike the fields of this script instance.
    private const string ManagerButtonMeta = "eplugin_manager_button";
    private bool _managerUiAdded;
    private Button? _managerButton;
    private EPluginManagerDialog? _managerDialog;
    private UpdateFailureDialog? _failureDialog;
    private EGlobal? _updateOwner;
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
        AssemblyUnloadCleanup.Register();
        if (!EnsureEarlyUpdateRecovery()) return;
        base._Process(delta);

        if (!EGlobal.Instance.IsValid())
        {
            // after an assembly reload the EnterTree, EnablePlugin and Ready are not triggered anymore but all C#
            // state is lost. This will reinitialize the ePlugin Framework.
            InitializeInternals();
        }

        if (!_managerUiAdded && EGlobal.Instance.IsValid())
        {
            AddManagerUi();
        }
    }

    public override void _DisablePlugin()
    {
        RemoveManagerUi();
        EGlobal.Instance.RecordFrameworkDisabled(this);

        base._DisablePlugin();
    }

    public override void _ExitTree()
    {
        RemoveManagerUi();
        _updateLifetime.Cancel();
        _updateLifetime.Dispose();

        base._ExitTree();
    }
    
    private void AddManagerUi()
    {
        // A C# assembly reload resets this script's fields while the native menu item and toolbar button survive.
        RemoveToolMenuItem(ManagerMenuName);
        AddToolMenuItem(ManagerMenuName, Callable.From(OpenManager));
        RemoveManagerButton();
        _managerButton = new Button
        {
            Name = "EPluginManagerButton", Flat = true, TooltipText = "ePlugin Manager",
            FocusMode = Control.FocusModeEnum.None,
            Icon = ResourceLoader.Exists(EPluginManagerDialog.EPluginIconPath) ? GD.Load<Texture2D>(EPluginManagerDialog.EPluginIconPath) : null,
            Text = ResourceLoader.Exists(EPluginManagerDialog.EPluginIconPath) ? "" : "ePlugin"
        };
        _managerButton.Pressed += OpenManager;
        AddControlToContainer(CustomControlContainer.Toolbar, _managerButton);
        Engine.Singleton.SetMeta(ManagerButtonMeta, _managerButton.GetInstanceId());
        _managerUiAdded = true;
    }
    private void RemoveManagerButton()
    {
        var button = _managerButton;
        if (button is null && Engine.Singleton.HasMeta(ManagerButtonMeta))
            button = GodotObject.InstanceFromId(Engine.Singleton.GetMeta(ManagerButtonMeta).AsUInt64()) as Button;
        if (button is not null && GodotObject.IsInstanceValid(button))
        {
            if (button.IsInsideTree()) RemoveControlFromContainer(CustomControlContainer.Toolbar, button);
            button.QueueFree();
        }
        if (Engine.Singleton.HasMeta(ManagerButtonMeta)) Engine.Singleton.RemoveMeta(ManagerButtonMeta);
        _managerButton = null;
    }
    private void OpenManager()
    {
        if (!EGlobal.Instance.IsValid()) return;
        if (_managerDialog is null || !GodotObject.IsInstanceValid(_managerDialog))
        {
            // A dialog left over from before an assembly reload has lost its state; replace it.
            var parent = EditorInterface.Singleton.GetBaseControl();
            if (parent.GetNodeOrNull(nameof(EPluginManagerDialog)) is { } stale) { parent.RemoveChild(stale); stale.QueueFree(); }
            _managerDialog = EPluginManagerDialog.Create(EGlobal.Instance, UpdateLifetime);
            parent.AddChild(_managerDialog);
        }
        _managerDialog.Open();
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
    private void RemoveManagerUi()
    {
        if (_managerUiAdded) { RemoveToolMenuItem(ManagerMenuName); _managerUiAdded = false; }
        RemoveManagerButton();
        if (_updateOwner is not null) _updateOwner.UpdateDecisionNeeded -= ShowUpdateFailure;
        if (_managerDialog is not null && GodotObject.IsInstanceValid(_managerDialog)) _managerDialog.QueueFree();
        if (_failureDialog is not null && GodotObject.IsInstanceValid(_failureDialog)) _failureDialog.QueueFree();
        _managerDialog = null; _failureDialog = null;
    }

    private static void RestoreEarlyUpdateSettings()
    {
        using var config = new ConfigFile();
        if (config.Load("res://project.godot") != Error.Ok) throw new IOException("Cannot reload restored project settings.");
        foreach (var property in ProjectSettings.Singleton.GetPropertyList())
        {
            var name = property["name"].AsString();
            if (name.StartsWith("autoload/", StringComparison.Ordinal)) ProjectSettings.Clear(name);
        }
        if (config.HasSection("autoload")) foreach (var key in config.GetSectionKeys("autoload")) ProjectSettings.SetSetting("autoload/" + key, config.GetValue("autoload", key));
        if (config.HasSectionKey("editor_plugins", "enabled")) ProjectSettings.SetSetting("editor_plugins/enabled", config.GetValue("editor_plugins", "enabled"));
    }

    private bool EnsureEarlyUpdateRecovery()
    {
        const string recoveryKey = "eplugin_early_update_recovery";
        if (!Engine.Singleton.HasMeta(recoveryKey))
        {
            Engine.Singleton.SetMeta(recoveryKey, false);
            var recovery = UpdateRecovery.RunIfNeeded(ProjectSettings.GlobalizePath("res://"), Logger,
                () => new DotnetVersionManager(Logger, EnableDebugLogging).Create(Logger) is ICheckedDotnetCli cli && cli.TryBuild().ExitCode == 0,
                RestoreEarlyUpdateSettings);
            if (recovery != UpdateRecovery.Outcome.Continue)
            {
                Engine.Singleton.SetMeta(recoveryKey, true);
                if (recovery == UpdateRecovery.Outcome.Restart) EditorInterface.Singleton.CallDeferred(EditorInterface.MethodName.RestartEditor, true);
            }
        }
        return !Engine.Singleton.GetMeta(recoveryKey).AsBool();
    }

    private void InitializeInternals()
    {
        if (!EnsureEarlyUpdateRecovery()) return;
        _managerUiAdded = false;
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
