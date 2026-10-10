#if TOOLS
using System;
using System.Threading;
using System.IO;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using System.Runtime.Loader;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin;

[Tool]
public sealed partial class EPluginPlugin : EditorPlugin, IEPlugin, ISerializationListener
{
    private const string ManagerMenuName = "ePlugin Manager...";
    // Engine metadata survives assembly reloads, unlike the fields of this script instance.
    private const string ManagerButtonMeta = "eplugin_manager_button";
    private readonly EditorSignalConnections _managerSignals = new();
    private bool _managerUiAdded;
    private Button? _managerButton;
    private EPluginManagerDialog? _managerDialog;
    private UpdateFailureDialog? _failureDialog;
    private LicenseDialog? _licenseDialog;
    private WelcomeDialog? _welcomeDialog;
    private EGlobal? _updateOwner;
    // An update restarted the editor while the ePlugin Manager was open; it opens again while the progress window of
    // that start is still shown, which closes once the manager had a few frames to draw.
    private bool _reopenManager;
    private IDisposable? _reopenProgress;
    private int _reopenFrames;
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

        ReopenManager();
        ShowLicenseReview();
        ShowWelcomes();
    }

    private static ManagerReopenMarker ManagerReopen => new(ProjectSettings.GlobalizePath("res://.godot/eplugin/manager-open"));

    /// <summary>Opens the ePlugin Manager that was open when an update restarted the editor, then closes the progress window.</summary>
    private void ReopenManager()
    {
        if (_reopenProgress is not null && !_reopenManager && ++_reopenFrames > 2) ReleaseReopenProgress();
        if (!_reopenManager || !_managerUiAdded) return;
        _reopenManager = false;
        ManagerReopen.Take();
        ActivationProgress.SetText("Opening the ePlugin Manager...");
        try { OpenManager(); }
        catch (Exception ex) { Logger.Error($"Cannot reopen the ePlugin Manager: {ex.Message}"); }
    }

    /// <summary>Keeps the ePlugin Manager open across an update's editor restarts.</summary>
    private void RememberManagerForRestart()
    {
        var marker = ManagerReopen;
        // A start that restarts again, e.g. after the interim build, has not reopened the manager yet: renew its marker.
        var open = _managerDialog is not null && GodotObject.IsInstanceValid(_managerDialog) && _managerDialog.Visible;
        if (open || _reopenManager || marker.IsSet) marker.Set();
    }

    private void ReleaseReopenProgress()
    {
        var progress = _reopenProgress;
        _reopenProgress = null; _reopenManager = false; _reopenFrames = 0;
        progress?.Dispose();
    }

    /// <summary>Asks for the licenses of plugins that were enabled outside the ePlugin Manager, one dialog at a time.</summary>
    private void ShowLicenseReview()
    {
        if (!EGlobal.Instance.IsValid() || (_licenseDialog is not null && GodotObject.IsInstanceValid(_licenseDialog))) return;
        if (EGlobal.Instance.TakeLicenseReview() is not { } review) return;
        try
        {
            _licenseDialog = LicenseDialog.Create(review, decided => { _licenseDialog = null; EGlobal.Instance.CompleteLicenseReview(decided); });
            _licenseDialog.Open();
        }
        catch (Exception ex)
        {
            Logger.Error($"Cannot show the license dialog: {ex.Message}");
            _licenseDialog = null;
            review.DeclineRemaining();
            EGlobal.Instance.CompleteLicenseReview(review);
        }
    }

    /// <summary>Shows the welcome pages of newly installed plugins once no other ePlugin dialog asks for a decision.</summary>
    private void ShowWelcomes()
    {
        static bool Open(Window? dialog) => dialog is not null && GodotObject.IsInstanceValid(dialog);
        if (!EGlobal.Instance.IsValid() || Open(_welcomeDialog) || Open(_licenseDialog) || Open(_failureDialog)) return;
        if (EGlobal.Instance.TakeWelcomes() is not { Count: > 0 } welcomes) return;
        try
        {
            _welcomeDialog = WelcomeDialog.Create(welcomes, shown => { _welcomeDialog = null; EGlobal.Instance.CompleteWelcomes(shown); });
            _welcomeDialog.Open();
        }
        catch (Exception ex)
        {
            Logger.Error($"Cannot show the welcome dialog: {ex.Message}");
            _welcomeDialog = null;
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
    
    public void OnBeforeSerialize() => ReleaseManagerCallbacks();
    public void OnAfterDeserialize() { }

    private void ReleaseManagerCallbacks()
    {
        _managerSignals.Dispose();
        if (_updateOwner is not null)
        {
            _updateOwner.UpdateDecisionNeeded -= ShowUpdateFailure;
            _updateOwner.RestartRequested -= RememberManagerForRestart;
        }
        ReleaseReopenProgress();
        try { _updateLifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Godot disposes the script on reload while its native editor controls remain in the tree.
            ReleaseManagerCallbacks();
            _updateLifetime.Dispose();
        }
        base.Dispose(disposing);
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
            Icon = EditorIcons.EPlugin
        };
        if (_managerButton.Icon is null) _managerButton.Text = "ePlugin";
        _managerSignals.Connect(_managerButton, Control.SignalName.ThemeChanged, Callable.From(() => CallDeferred(nameof(RefreshManagerIcon))));
        _managerSignals.Connect(_managerButton, BaseButton.SignalName.Pressed, Callable.From(OpenManager));
        AddControlToContainer(CustomControlContainer.Toolbar, _managerButton);
        Engine.Singleton.SetMeta(ManagerButtonMeta, _managerButton.GetInstanceId());
        _managerUiAdded = true;
        // Toggling a plugin in the manager may rebuild and reload the assembly while the dialog is open.
        if (RemoveStaleManagerDialog()) Callable.From(OpenManager).CallDeferred();
    }

    private void RefreshManagerIcon()
    {
        if (_managerButton is null || !GodotObject.IsInstanceValid(_managerButton)) return;
        _managerButton.Icon = EditorIcons.EPlugin;
        _managerButton.Text = _managerButton.Icon is null ? "ePlugin" : "";
    }
    /// <summary>Frees a dialog whose C# state was lost to an assembly reload; returns whether it was showing.</summary>
    private bool RemoveStaleManagerDialog()
    {
        var parent = EditorInterface.Singleton.GetBaseControl();
        if (parent.GetNodeOrNull(nameof(EPluginManagerDialog)) is not Window stale ||
            stale is EPluginManagerDialog { IsInitialized: true }) return false;
        var visible = stale.Visible;
        stale.Hide(); parent.RemoveChild(stale); stale.QueueFree();
        if (stale == _managerDialog) _managerDialog = null;
        return visible;
    }
    private void RemoveManagerButton()
    {
        _managerSignals.Dispose();
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
        if (_managerDialog is null || !GodotObject.IsInstanceValid(_managerDialog) || !_managerDialog.IsInitialized)
        {
            RemoveStaleManagerDialog();
            _managerDialog = EPluginManagerDialog.Create(EGlobal.Instance, UpdateLifetime);
            EditorInterface.Singleton.GetBaseControl().AddChild(_managerDialog);
        }
        _managerDialog.Open();
    }
    private void ShowUpdateFailure(UpdateJournal journal)
    {
        try
        {
            _failureDialog = UpdateFailureDialog.Create(journal, keep => EGlobal.Instance.DecideUpdate(journal, keep));
            EditorInterface.Singleton.GetBaseControl().AddChild(_failureDialog);
            _failureDialog.Open();
        }
        catch (Exception ex) { Logger.Error($"Cannot show update decision: {ex.Message}"); EGlobal.Instance.DecideUpdate(journal, false); }
    }
    private void RemoveManagerUi()
    {
        if (_managerUiAdded) { RemoveToolMenuItem(ManagerMenuName); _managerUiAdded = false; }
        RemoveManagerButton();
        if (_updateOwner is not null)
        {
            _updateOwner.UpdateDecisionNeeded -= ShowUpdateFailure;
            _updateOwner.RestartRequested -= RememberManagerForRestart;
        }
        ReleaseReopenProgress();
        if (_managerDialog is not null && GodotObject.IsInstanceValid(_managerDialog)) _managerDialog.QueueFree();
        if (_failureDialog is not null && GodotObject.IsInstanceValid(_failureDialog)) _failureDialog.QueueFree();
        if (_licenseDialog is not null && GodotObject.IsInstanceValid(_licenseDialog)) _licenseDialog.QueueFree();
        if (_welcomeDialog is not null && GodotObject.IsInstanceValid(_welcomeDialog)) _welcomeDialog.QueueFree();
        _managerDialog = null; _failureDialog = null; _licenseDialog = null; _welcomeDialog = null;
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
        _updateOwner.RestartRequested -= RememberManagerForRestart;
        _updateOwner.RestartRequested += RememberManagerForRestart;
        var context = AssemblyLoadContext.GetLoadContext(typeof(EPluginPlugin).Assembly);
        if (context is not null) context.Unloading += _ => { try { _updateLifetime.Cancel(); } catch (ObjectDisposedException) { } };
        UpdateSettings.Register();
        LicenseSettings.Register();
        // An update restarted the editor while the ePlugin Manager was open. The progress window spans the whole start,
        // so the manager is already shown when it closes.
        var progress = ManagerReopen.IsSet ? ActivationProgress.Begin("Finishing the plugin update...") : null;
        try { EGlobal.Instance.Initialize(this, new GenericLoggerFactory(category => new GodotConsoleLogger(category))); }
        catch { progress?.Dispose(); throw; }
        if (progress is null) return;
        // Another restart follows, e.g. after the interim build: the manager opens after that one.
        if (EGlobal.Instance.IsRestartPending) { progress.Dispose(); return; }
        ReleaseReopenProgress();
        _reopenProgress = progress; _reopenManager = true;
    }
}
#endif
