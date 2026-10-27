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
using Enaweg.Plugin.Internal.Welcomes;
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
    private ManagerStatus _managerStatus;
    private EPluginManagerDialog? _managerDialog;
    private UpdateFailureDialog? _failureDialog;
    private LicenseDialog? _licenseDialog;
    private WelcomeDialog? _welcomeDialog;
    private CleanupSuggestionDialog? _cleanupDialog;
    private EGlobal? _updateOwner;
    // A resumed update keeps its progress window until startup finishes and the manager has a few frames to draw.
    private bool _reopenManager;
    private ActivationProgress.Scope? _reopenProgress;
    private int _reopenFrames;
    private CancellationTokenSource _updateLifetime = new();
    private static readonly TimeSpan InitializeRetryDelay = TimeSpan.FromSeconds(10);
    private DateTime _nextInitializeAttempt;
    private bool _unloadingObserved;
    // Set when a project setting changed, e.g. the enabled plugins: reading them every frame would cost for nothing.
    private bool _pluginTogglesDue = true;
    internal CancellationToken UpdateLifetime => _updateLifetime.Token;
    /// <summary>The <c>eplugin/logging/debug</c> project setting.</summary>
    public bool EnableDebugLogging => LoggingSettings.Debug;

    private ILogger? _logger;

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

        EGlobal.RunPosted();
        // Plain plugins enabled or disabled in Project Settings run no framework code; their licenses, dependants and
        // optional recipes are handled here, before the dialogs below look at them.
        if (_pluginTogglesDue) _pluginTogglesDue = !EGlobal.Instance.ObservePluginToggles();

        ShowManagerStatus();
        ReopenManager();
        ShowLicenseReview();
        ShowWelcomes();
        ShowCleanupSuggestion();
    }

    private static ManagerReopenMarker ManagerReopen => new(Path.Combine(EGlobal.Instance.Host.ProjectRoot, ".godot/eplugin/manager-open"));

    /// <summary>Opens the ePlugin Manager that was open when an update restarted the editor, then closes the progress window.</summary>
    private void ReopenManager()
    {
        if (_reopenProgress is not null && !_reopenManager && ++_reopenFrames > 2) ReleaseReopenProgress();
        if (!_reopenManager || !_managerUiAdded) return;
        _reopenManager = false;
        ManagerReopen.Take();
        _reopenProgress?.SetText("Opening the ePlugin Manager...");
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

    private static bool IsOpen(Window? dialog) => dialog is not null && GodotObject.IsInstanceValid(dialog);

    /// <summary>Shows the welcome pages of newly installed plugins once no other ePlugin dialog asks for a decision.</summary>
    private void ShowWelcomes()
    {
        if (!EGlobal.Instance.IsValid() || IsOpen(_welcomeDialog) || IsOpen(_licenseDialog) || IsOpen(_failureDialog)) return;
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

    /// <summary>
    /// Suggests cleaning up a local directory that holds more than the user set, after the scheduled update check and
    /// once no other ePlugin dialog is open.
    /// </summary>
    private void ShowCleanupSuggestion()
    {
        if (!EGlobal.Instance.IsValid() || IsOpen(_cleanupDialog) || IsOpen(_welcomeDialog) || IsOpen(_licenseDialog) || IsOpen(_failureDialog)) return;
        if (EGlobal.Instance.TakeCleanupSuggestion() is not { } suggestion) return;
        try
        {
            _cleanupDialog = CleanupSuggestionDialog.Create(suggestion, cleanUp =>
            {
                _cleanupDialog = null;
                // Opened once this dialog is gone; a method callable, which Godot resolves after an assembly reload too.
                if (cleanUp) CallDeferred(MethodName.OpenLocalCleanup, suggestion.Directory);
            });
            _cleanupDialog.Open();
        }
        catch (Exception ex)
        {
            Logger.Error($"Cannot show the cleanup suggestion: {ex.Message}");
            _cleanupDialog = null;
        }
    }

    /// <summary>Opens the ePlugin Manager and its settings on the Tools tab, cleaning up <paramref name="directory"/>.</summary>
    private void OpenLocalCleanup(string directory)
    {
        OpenManager();
        if (_managerDialog is not null && GodotObject.IsInstanceValid(_managerDialog)) _managerDialog.OpenSettings(directory);
    }

    public override void _DisablePlugin()
    {
        RemoveManagerUi();
        EGlobal.Instance.RecordFrameworkDisabled(this);
        // plugins toggled while the framework is disabled are not handled when it is enabled again
        EGlobal.ForgetPluginToggles();
        EGlobal.ForgetLicenseDecisions();
        EGlobal.ForgetUnsavedState();
        // Godot frees this instance; the one it creates when the framework is enabled again initializes it anew.
        EGlobal.Detach(this);

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
        // A method callable rather than a delegate: Godot resolves it on the script instance an assembly reload creates.
        AddToolMenuItem(ManagerMenuName, new Callable(this, MethodName.OpenManager));
        RemoveManagerButton();
        _managerButton = new Button
        {
            Name = "EPluginManagerButton", Flat = true, TooltipText = ManagerTooltip(_managerStatus),
            FocusMode = Control.FocusModeEnum.None,
            Icon = EditorIcons.Manager(_managerStatus)
        };
        if (_managerButton.Icon is null) _managerButton.Text = "ePlugin";
        _managerSignals.Connect(_managerButton, Control.SignalName.ThemeChanged, Callable.From(() => CallDeferred(nameof(RefreshManagerIcon))));
        _managerSignals.Connect(_managerButton, BaseButton.SignalName.Pressed, Callable.From(OpenManager));
        var settingsChanged = new Callable(this, MethodName.OnSettingsChanged);
        if (!ProjectSettings.Singleton.IsConnected(ProjectSettings.SignalName.SettingsChanged, settingsChanged))
            _managerSignals.Connect(ProjectSettings.Singleton, ProjectSettings.SignalName.SettingsChanged, settingsChanged);
        AddControlToContainer(CustomControlContainer.Toolbar, _managerButton);
        Engine.Singleton.SetMeta(ManagerButtonMeta, _managerButton.GetInstanceId());
        _managerUiAdded = true;
        // Toggling a plugin in the manager may rebuild and reload the assembly while the dialog is open.
        if (RemoveStaleManagerDialog()) CallDeferred(MethodName.OpenManager);
    }

    /// <summary>Godot emits settings_changed once in the frame after settings changed, also when a plugin was toggled.</summary>
    private void OnSettingsChanged() => _pluginTogglesDue = true;

    private void RefreshManagerIcon()
    {
        if (_managerButton is null || !GodotObject.IsInstanceValid(_managerButton)) return;
        _managerButton.Icon = EditorIcons.Manager(_managerStatus);
        _managerButton.Text = _managerButton.Icon is null ? "ePlugin" : "";
        _managerButton.TooltipText = ManagerTooltip(_managerStatus);
    }

    /// <summary>Colors the puzzle piece of the toolbar button while updates can be installed or a plugin needs attention.</summary>
    private void ShowManagerStatus()
    {
        if (!_managerUiAdded || !EGlobal.Instance.IsValid()) return;
        var status = EGlobal.Instance.CurrentManagerStatus();
        if (status == _managerStatus) return;
        _managerStatus = status;
        RefreshManagerIcon();
    }

    private static string ManagerTooltip(ManagerStatus status) => status switch
    {
        ManagerStatus.UpdatesAvailable => "ePlugin Manager\nPlugin updates are available.",
        ManagerStatus.NeedsAttention => "ePlugin Manager\nA plugin needs attention.",
        _ => "ePlugin Manager"
    };
    /// <summary>Releases a dialog whose C# state was lost to an assembly reload; returns whether it was showing.</summary>
    private bool RemoveStaleManagerDialog()
    {
        var parent = EditorInterface.Singleton.GetBaseControl();
        if (parent.GetNodeOrNull(nameof(EPluginManagerDialog)) is not Window stale ||
            stale is EPluginManagerDialog { IsInitialized: true }) return false;
        var visible = stale.Visible;
        // It stays in the tree until it is freed; renamed, so the dialog that replaces it gets the name this looks up.
        stale.Name = nameof(EPluginManagerDialog) + "Released";
        EditorWindows.Release(stale);
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
        foreach (var dialog in new Window?[] { _managerDialog, _failureDialog, _licenseDialog, _welcomeDialog, _cleanupDialog })
            if (dialog is not null) EditorWindows.Release(dialog);
        _managerDialog = null; _failureDialog = null; _licenseDialog = null; _welcomeDialog = null; _cleanupDialog = null;
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
            if (ActivationProgress.ResumeAfterRestart())
                _reopenProgress = ActivationProgress.Begin("Finishing the plugin update...");
            Engine.Singleton.SetMeta(recoveryKey, false);
            var host = EGlobal.Instance.Host;
            var recovery = UpdateRecovery.RunIfNeeded(host.ProjectRoot, Logger,
                () => new DotnetVersionManager(Logger, EnableDebugLogging).Create(Logger, host.ProjectRoot, host.AssemblyName, host.SolutionDirectory) is ICheckedDotnetCli cli && cli.TryBuild().ExitCode == 0,
                RestoreEarlyUpdateSettings);
            if (recovery != UpdateRecovery.Outcome.Continue)
            {
                Engine.Singleton.SetMeta(recoveryKey, true);
                if (recovery == UpdateRecovery.Outcome.Restart)
                {
                    ActivationProgress.PrepareForRestart();
                    host.RestartEditor();
                }
                ReleaseReopenProgress();
            }
        }
        return !Engine.Singleton.GetMeta(recoveryKey).AsBool();
    }

    private void InitializeInternals()
    {
        if (!EnsureEarlyUpdateRecovery() || DateTime.UtcNow < _nextInitializeAttempt) return;
        _managerUiAdded = false;
        // a plugin toggled between a build and its reload is found against the toggles kept in Engine metadata
        _pluginTogglesDue = true;
        _updateOwner = EGlobal.Instance;
        _updateOwner.UpdateDecisionNeeded -= ShowUpdateFailure;
        _updateOwner.UpdateDecisionNeeded += ShowUpdateFailure;
        _updateOwner.RestartRequested -= RememberManagerForRestart;
        _updateOwner.RestartRequested += RememberManagerForRestart;
        var context = AssemblyLoadContext.GetLoadContext(typeof(EPluginPlugin).Assembly);
        if (context is not null && !_unloadingObserved)
        {
            _unloadingObserved = true; // once per assembly, also when a failed start is tried again
            context.Unloading += _ => { try { _updateLifetime.Cancel(); } catch (ObjectDisposedException) { } };
        }
        UpdateSettings.Register();
        LicenseSettings.Register();
        WelcomeSettings.Register();
        LoggingSettings.Register();
        // A reconnected window spans recovery and initialization. If the manager was open, let it draw before closing.
        var progress = _reopenProgress ?? (ManagerReopen.IsSet ? ActivationProgress.Begin("Finishing the plugin update...") : null);
        _reopenProgress = null;
        try { EGlobal.Instance.Initialize(this, new GenericLoggerFactory(category => new GodotConsoleLogger(category))); }
        catch (Exception ex)
        {
            // EGlobal stays invalid; trying again every frame would only repeat the error.
            progress?.Dispose();
            _nextInitializeAttempt = DateTime.UtcNow + InitializeRetryDelay;
            Logger.Error($"ePlugin could not start and tries again in {InitializeRetryDelay.TotalSeconds:0} seconds: {ex}");
            return;
        }
        if (progress is null) return;
        // Another restart follows, e.g. after the interim build: the manager opens after that one.
        if (EGlobal.Instance.IsRestartPending) { progress.Dispose(); return; }
        ReleaseReopenProgress();
        _reopenProgress = progress; _reopenManager = ManagerReopen.IsSet;
    }
}
#endif
