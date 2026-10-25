#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// This is an internally used class by Enaweg.Plugin and should not be used by anything else. It manages plugin states
/// as well as provides some global values.
/// </summary>
[Tool]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The singleton lives as long as the assembly; its indexing token sources have no timer or wait handle to release.")]
internal sealed partial class EGlobal
{
    private static EGlobal? _instance;

    public static EGlobal Instance
    {
        get
        {
            _instance ??= new EGlobal();

            return _instance;
        }
    }

    private readonly List<PluginContext> _contexts = [];
    private EPluginPlugin? _ePluginContext;
    private PluginStateStore? _stateStore;
    private PlainPluginObserver? _plainPluginObserver;

    public DotnetVersionManager? CliService { get; private set; }

    private ILoggerFactory? _loggerFactory;

    private readonly Stack<PluginContext> _toCheckEnable = new();
    // Plugins that enabled the framework because it was disabled: unlike the ones a start finds enabled, they still
    // have to be installed once the framework is initialized.
    private readonly HashSet<PluginContext> _enabledWithFramework = [];
    private readonly Stack<PluginContext> _toCheckDisable = new();
    private readonly Queue<IInitialize> _toInitialize = new();
    private PluginTransition? _transition;
    private bool _transitionFailed;
    // While above zero the transition stays open, so several plugins toggled one after another are built once.
    private int _transitionHold;

    private readonly IEditorHost _host;

    /// <summary>The shared plugin state, <c>addons/eplugin-state.json</c> of the host's project.</summary>
    private string StateFilePath => Path.GetFullPath(Path.Combine(_host.ProjectRoot, "addons", "eplugin-state.json"));

    private EGlobal() : this(new EditorHost())
    {
    }

    /// <summary>An instance with its own editor, for tests; the framework uses <see cref="Instance"/>.</summary>
    internal EGlobal(IEditorHost host)
    {
        _host = host;
    }

    /// <summary>
    /// Makes <paramref name="global"/> the <see cref="Instance"/> until the result is disposed, for tests of the public
    /// API, which reaches the framework only through <see cref="Instance"/>.
    /// </summary>
    internal static IDisposable UseInstance(EGlobal global)
    {
        var previous = _instance;
        _instance = global;
        return new Deferral(() => _instance = previous);
    }

    /// <summary>The editor this instance works with, e.g. for the project's folder.</summary>
    internal IEditorHost Host => _host;

    /// <summary>The plugin contexts, for tests.</summary>
    internal List<PluginContext> Contexts => _contexts;

    /// <summary>The plugin state files, for tests that do not run <see cref="Initialize"/>.</summary>
    internal PluginStateStore? StateStore
    {
        get => _stateStore;
        set => _stateStore = value;
    }

    /// <summary>The observer of plain plugins' state, for tests that do not run <see cref="Initialize"/>.</summary>
    internal PlainPluginObserver? PlainPluginObserver
    {
        set => _plainPluginObserver = value;
    }

    /// <summary>While above zero a transition stays open after its last participant finished.</summary>
    internal int TransitionHold
    {
        get => _transitionHold;
        set => _transitionHold = value;
    }

    /// <summary>
    /// Makes the instance valid without the editor: what <see cref="Initialize"/> does first, for tests of the plugin
    /// lifecycle. The framework's own context gets <paramref name="cli"/>, which builds the solution.
    /// </summary>
    internal void Attach(EPluginPlugin plugin, ILoggerFactory loggerFactory, IDotnetCli? cli)
    {
        _loggerFactory = loggerFactory;
        _ePluginContext = plugin;
        plugin.Logger = loggerFactory.CreateLogger(plugin.GetType().FullName ?? "UNKNOWN");
        GetOrCreateContext(plugin).Cli = cli;
    }

    /// <summary>
    /// This needs to be called first to initialize the ePlugin system.
    /// </summary>
    /// <param name="plugin"></param>
    /// <param name="loggerFactory"></param>
    public void Initialize(EPluginPlugin plugin, ILoggerFactory loggerFactory)
    {
        try
        {
            InitializeCore(plugin, loggerFactory);
        }
        catch
        {
            // An initialization that threw leaves the framework invalid, although it was valid while it ran: half set up,
            // it would carry on with what is missing. EPluginPlugin tries again later.
            _ePluginContext = null;
            throw;
        }
    }

    private void InitializeCore(EPluginPlugin plugin, ILoggerFactory loggerFactory)
    {
        AssemblyReload.Capture();
        using var progress = _toCheckEnable.Count > 0 ? ActivationProgress.Begin("Activating plugins...") : null;
        _loggerFactory = loggerFactory;
        _ePluginContext = plugin;
        plugin.Logger = _loggerFactory.CreateLogger(_ePluginContext.GetType().FullName ?? "UNKNOWN");

        CliService = new DotnetVersionManager(plugin.Logger, plugin.EnableDebugLogging);
        foreach (var context in _contexts)
        {
            // a plugin enabled before the framework (e.g. one that enabled it) got its context without a logger
            context.Logger ??= _loggerFactory.CreateLogger(context.PluginBase.GetType().FullName ?? "UNKNOWN");
            context.Cli = GetCli(context.Logger);
        }

        _stateStore = new PluginStateStore(StateFilePath, plugin.Logger);
        _stateStore.Load();
        InitializeUpdateJournals();
        ReloadContexts(_loggerFactory);
        CreateStateBaseline();
        RecordFrameworkEnabled(plugin);
        _plainPluginObserver = new PlainPluginObserver(_stateStore, GetEnabledPluginSlugs,
            InstalledVersion,
            IsManagedPlugin,
            // a GDExtension add-on is recorded as activated, but never enabled
            slug => _updateJournals?.OwnedSlugs.Contains(slug) == true || IsExtensionAddon(slug),
            plugin.Logger);
        RefreshPlainPlugins();
        InitializePluginToggles();
        ResumeWaitingPlugins();
        ResumeUpdates();
        ReviewEnabledLicenses();
        InitializeUpdates(plugin);
    }

    /// <summary>
    /// Switch logging factory. This is used by logging plugins to switch ePlugin logging to their own.
    /// </summary>
    /// <param name="loggerFactory"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void SwitchLogging(ILoggerFactory loggerFactory)
    {
        if (!IsValid())
        {
            throw new InvalidOperationException("EGlobal is not initialized, cannot switch logging");
        }

        _loggerFactory = loggerFactory;
        _ePluginContext!.Logger = _loggerFactory.CreateLogger(_ePluginContext.GetType().FullName ?? "UNKNOWN");

        CliService = new DotnetVersionManager(_ePluginContext.Logger, _ePluginContext.EnableDebugLogging);

        // every plugin logs through the new factory too, also the dotnet commands run for it
        foreach (var context in _contexts)
        {
            context.Logger = _loggerFactory.CreateLogger(context.PluginBase.GetType().FullName ?? "UNKNOWN");
            context.Cli = GetCli(context.Logger);
        }
    }

    /// <summary>
    /// Returns true if EGlobal is initialized. After an assembly reload all state is lost, this will be false then and
    /// a new initialize needs to happen.
    /// </summary>
    /// <returns></returns>
    public bool IsValid()
    {
        return _ePluginContext is not null;
    }

    internal void CreateStateBaseline()
    {
        if (_stateStore is null || _stateStore.HasSharedFile || _stateStore.IsReadOnly)
        {
            return;
        }

        var active = _contexts.Where(c => c.State == EEditorPluginState.Activated)
            .ToArray();
        var states = new List<SharedPluginState>();
        foreach (var context in active)
        {
            var version = context.Metadata?.Version;
            if (string.IsNullOrWhiteSpace(version))
            {
                _stateStore.TryRecordInvalid(context.Slug, version, "invalid_plugin_version");
                context.State = EEditorPluginState.Error;
                context.Logger?.Error($"Cannot record {context.Slug}: plugin.cfg has no usable version.");
                continue;
            }

            states.Add(new SharedPluginState(context.Slug, version, PersistedPluginState.Activated));
        }

        _stateStore.TryCreateBaseline(states);
    }

    public void RecordFrameworkDisabled(EPluginPlugin plugin)
    {
        _stateStore ??= new PluginStateStore(StateFilePath, plugin.Logger);
        if (!_stateStore.HasSharedFile && !_stateStore.IsReadOnly)
        {
            _stateStore.Load();
        }

        var context = GetOrCreateContext(plugin);
        var version = context.Metadata?.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            _stateStore.TryRecordInvalid(context.Slug, version, "invalid_plugin_version");
            return;
        }

        if (!_stateStore.TryBeginAttempt(context.Slug, version, PersistedPluginState.Deactivated, out var id))
        {
            return;
        }

        _stateStore.TryComplete(id, [
            new SharedPluginState(context.Slug, version, PersistedPluginState.Deactivated)
        ]);
    }

    /// <summary>
    /// Drops the state of a framework that is being disabled. Godot frees its instance and creates another when it is
    /// enabled again, without an assembly reload: kept, this state would stay valid with the freed instance, so the new
    /// one would never initialize. Like after a reload, the next <see cref="Instance"/> starts empty.
    /// </summary>
    internal static void Detach(EPluginPlugin plugin)
    {
        if (_instance is null || !ReferenceEquals(_instance._ePluginContext, plugin)) return;
        // work still posted by the old instance finds it invalid instead of using the freed plugin
        _instance._ePluginContext = null;
        _instance = null;
    }

    internal void RecordFrameworkEnabled(EPluginPlugin plugin)
    {
        if (_stateStore is null || _stateStore.IsReadOnly)
        {
            return;
        }

        var context = GetOrCreateContext(plugin);
        if (_stateStore.IsBlocked(context.Slug) ||
            _stateStore.GetShared(context.Slug)?.State == PersistedPluginState.Activated)
        {
            return;
        }

        var version = context.Metadata?.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            _stateStore.TryRecordInvalid(context.Slug, version, "invalid_plugin_version");
            return;
        }

        if (_stateStore.TryBeginAttempt(context.Slug, version, PersistedPluginState.Activated, out var id))
        {
            _stateStore.TryComplete(id, [
                new SharedPluginState(context.Slug, version, PersistedPluginState.Activated)
            ]);
        }
    }

    /// <summary>Call before consuming persisted plugin state and after an update batch.</summary>
    internal void RefreshPlainPlugins() => _plainPluginObserver?.Refresh();

    private bool IsManagedPlugin(string slug)
    {
        if (slug == _ePluginContext?.GetPluginSlug() ||
            _contexts.Any(c => c.Slug == slug && c.Plugin is not null))
        {
            return true;
        }

        // Disabled C# plugins have no editor instance after a reload. Their script types are still compiled.
        return _host.IsEPlugin(slug);
    }

    private IReadOnlySet<string> GetEnabledPluginSlugs() => _host.EnabledPluginSlugs();

    /// <summary>The context of an ePlugin's editor instance with this slug; null for a plain plugin or one not enabled.</summary>
    private PluginContext? ManagedContext(string slug) => _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);

    public PluginContext GetOrCreateContext(EditorPlugin pluginBase)
    {
        var context = _contexts.FirstOrDefault(c => c.PluginBase == pluginBase);
        if (context is null)
        {
            var ePlugin = pluginBase as IEEditorPlugin;
            var pluginLogger = _loggerFactory?.CreateLogger(pluginBase.GetType().FullName ?? "UNKNOWN");
            context = new PluginContext(ePlugin, pluginBase, pluginLogger) { Cli = GetCli(pluginLogger) };
            AddContext(context);
        }

        return context;
    }

    /// <summary>
    /// Adds the context of a new editor instance of a plugin. Godot frees a plugin's instance when it is disabled and
    /// creates another when it is enabled again, so a context left by the earlier instance is replaced: found first, it
    /// would make the plugin look deactivated to the plugins that need it.
    /// </summary>
    internal void AddContext(PluginContext context)
    {
        if (context.Plugin is not null)
        {
            _contexts.RemoveAll(c => c.Slug == context.Slug && c.Plugin is not null && c.PluginBase != context.PluginBase &&
                                     (c.State is EEditorPluginState.Deactivated or EEditorPluginState.Error ||
                                      !GodotObject.IsInstanceValid(c.PluginBase)));
        }

        _contexts.Add(context);
        if (context.PluginBase is IInitialize initialize)
        {
            // before the framework is set up there is nothing to pass yet; it initializes the plugin then
            AddInitializer(initialize);
        }
    }

    public IDotnetCli? GetCli(ILogger? logger)
    {
        return CliService?.Create(logger, _host.ProjectRoot, _host.AssemblyName, _host.SolutionDirectory);
    }

    private void RefreshEditor(bool rebuild = true)
    {
        if (_updateRefreshSuppression > 0) return;
        ActivationProgress.SetText("Refreshing editor and rebuilding solution...");
        _ePluginContext?.Logger.Log($"Refreshed Editor state.");

        // refresh what we can in Godot Editor UI.
        _host.ScanFilesystem();

        if (_ePluginContext is not null)
        {
            var baseContext = GetOrCreateContext(_ePluginContext);
            if (rebuild)
            {
                baseContext.Cli?.RebuildSolution();
            }

            _ePluginContext.Logger.Log(
                $"Completed loading. {_contexts.Count(c => c.State == EEditorPluginState.Activated)} active plugins.");
        }
    }

    /// <summary>
    /// If assembly reload is triggered based on code changes the contexts are lost. Need to rebuild it from active plugins.
    /// </summary>
    internal void ReloadContexts(ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger(GetType().FullName ?? "UNKNOWN");
        var children = _host.PluginNodes();

        if (_ePluginContext is null)
        {
            // get ePlugin base
            var ePluginNode = children.FirstOrDefault(c => c is EPluginPlugin);
            if (ePluginNode is null)
            {
                logger?.Error($"ePlugin base node not found!");
                return;
            }

            _ePluginContext = (EPluginPlugin)ePluginNode;
        }

        // handle other plugins
        _ePluginContext.Logger.Log($"Refreshing plugins");

        var deactivatedPlugins = _contexts.Where(c => c.State == EEditorPluginState.Deactivated).ToArray();
        if (deactivatedPlugins.Length > 0)
        {
            foreach (var pluginContext in deactivatedPlugins)
            {
                _contexts.Remove(pluginContext);
            }
        }

        foreach (var childNode in children)
        {
            if (childNode.GetScript().VariantType is Variant.Type.Nil ||
                string.IsNullOrEmpty(((Script)childNode.GetScript()).GetPath()))
            {
                continue;
            }

            if (childNode is EditorPlugin pluginBase)
            {
                if (pluginBase.GetPluginDirectory() is null)
                {
                    // native Godot plugin, skip
                    continue;
                }

                var context = GetOrCreateContext(pluginBase);

                context.State = _enabledWithFramework.Contains(context) || _toCheckEnable.Contains(context)
                    ?
                    // the plugin was enabled while the framework was not initialized and waits for its activation, so
                    // this is a new plugin need to bootstrap.
                    EEditorPluginState.Created
                    :
                    // initial start or assembly reload, nothing need to be done as installation already happened
                    EEditorPluginState.Activated;

                var updateOwned = _updateJournals?.OwnsAttempt(_stateStore?.GetAttemptId(context.Slug)) == true;
                if (_stateStore?.IsReadOnly == true || (_stateStore?.IsBlocked(context.Slug) == true && !updateOwned))
                {
                    context.State = EEditorPluginState.Error;
                    context.Logger?.Error(
                        $"Plugin {context.Slug} has unresolved local state ({DescribeLocalState(context.Slug)}); use the manual retry action after recovery.");
                }
                else if (!updateOwned && _stateStore?.GetShared(context.Slug) is { } saved)
                {
                    if (saved.State != PersistedPluginState.Activated ||
                        !string.Equals(saved.Version, context.Metadata?.Version, StringComparison.Ordinal))
                    {
                        context.Logger?.Warn(
                            $"Plugin {context.Slug} differs from recorded state ({saved.State}, version {saved.Version}); installed version is {context.Metadata?.Version ?? "unknown"}.");
                    }
                }

                _ePluginContext.Logger.Log($"  - plugin {pluginBase.GetPluginSlug()} ({pluginBase.GetName()})");
            }
        }

        _ePluginContext.Logger.Log(
            $"Found {_contexts.Count(p => p.State is EEditorPluginState.Activated)} active plugins.");

        ExecuteInitializers();
    }

    internal void ExecuteInitializers()
    {
        if (!IsValid())
        {
            return;
        }

        while (_toInitialize.Count > 0)
        {
            RunInitializer(_toInitialize.Dequeue());
        }
    }

    /// <summary>Calls an initializer; one that fails is logged and does not keep the others from running.</summary>
    private void RunInitializer(IInitialize initializer)
    {
        try
        {
            initializer.Initialize(_ePluginContext!);
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Error($"Initializer {initializer.GetType().FullName} failed: {ex.Message}");
        }
    }

    public void AddInitializer(IInitialize initializer)
    {
        // The queue is only run while the framework starts: one registered later, e.g. by a plugin enabled since, runs now.
        if (!IsValid())
        {
            _toInitialize.Enqueue(initializer);
            return;
        }

        if (_ePluginContext!.EnableDebugLogging)
        {
            _ePluginContext.Logger?.Log($"Initializer {initializer.GetType().FullName} runs now, the framework is ready.");
        }

        RunInitializer(initializer);
    }
}
#endif
