#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// This is an internally used class by Enaweg.Plugin and should not be used by anything else. It manages plugin states
/// as well as provides some global values.
/// </summary>
[Tool]
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
    private readonly Stack<PluginContext> _toCheckDisable = new();
    private readonly Queue<IInitialize> _toInitialize = new();
    private PluginTransition? _transition;
    private bool _transitionFailed;
    // While above zero the transition stays open, so several plugins toggled one after another are built once.
    private int _transitionHold;

    internal bool IsChangingPlugins => _transition is not null || _toCheckEnable.Count > 0 ||
        _toCheckDisable.Count > 0 || _transitionHold > 0;

    private sealed class PluginTransition(Guid attemptId)
    {
        public Guid AttemptId { get; } = attemptId;
        public Dictionary<PluginContext, PersistedPluginState> Participants { get; } = [];
    }


    private readonly IEditorHost _host;

    private EGlobal() : this(new EditorHost())
    {
    }

    /// <summary>An instance with its own editor, for tests; the framework uses <see cref="Instance"/>.</summary>
    internal EGlobal(IEditorHost host)
    {
        _host = host;
    }

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
            // Valid only once everything ran: a framework that is half set up would carry on with what is missing.
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
            context.Cli = GetCli(context.Logger);
        }

        _stateStore = new PluginStateStore(
            Path.GetFullPath(ProjectSettings.GlobalizePath("res://addons/eplugin-state.json")), plugin.Logger);
        _stateStore.Load();
        InitializeUpdateJournals();
        ReloadContexts(_loggerFactory, false);
        CreateStateBaseline();
        RecordFrameworkEnabled(plugin);
        _plainPluginObserver = new PlainPluginObserver(_stateStore, GetEnabledPluginSlugs,
            InstalledVersion,
            IsManagedPlugin,
            slug => _updateJournals?.OwnedSlugs.Contains(slug) == true,
            plugin.Logger);
        RefreshPlainPlugins();
        InitializePluginToggles();

        if (_toCheckEnable.Count > 0)
        {
            // Take the waiting plugins off the stack first: EnableEPlugin pushes a plugin back while one of its
            // dependencies is still being enabled, which would break enumerating the stack, and draining it until
            // empty would never end for a dependency that cannot be enabled.
            var waitingPlugins = _toCheckEnable.ToArray();
            _toCheckEnable.Clear();
            foreach (var pluginContext in waitingPlugins)
            {
                EnableEPlugin(pluginContext, false);
            }

            if (FinishTransition())
            {
                RefreshEditor(rebuild: false);
            }
        }

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
        _stateStore ??= new PluginStateStore(
            Path.GetFullPath(ProjectSettings.GlobalizePath("res://addons/eplugin-state.json")), plugin.Logger);
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

    private void RecordFrameworkEnabled(EPluginPlugin plugin)
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

    public void RetryFailedPlugins()
    {
        RefreshPlainPlugins();
        if (_stateStore is null || _stateStore.IsReadOnly)
        {
            return;
        }

        var retriedUpdates = new HashSet<Guid>();
        foreach (var attempt in OrderRetries(_stateStore.LocalAttempts))
        {
            var updateJournal = _updateJournals?.Read().FirstOrDefault(j => j.AttemptId == attempt.AttemptId);
            if (updateJournal is not null || attempt.Reason.StartsWith("update_", StringComparison.Ordinal))
            {
                if (retriedUpdates.Add(attempt.AttemptId)) RetryUpdate(updateJournal);
                continue;
            }
            var context = _contexts.FirstOrDefault(c => c.Slug == attempt.Slug && c.Plugin is not null);
            var expectedState = attempt.TargetState == PersistedPluginState.Activated
                ? EEditorPluginState.Activated
                : EEditorPluginState.Deactivated;
            if (_stateStore.GetLocal(attempt.Slug) is null && context?.State == expectedState)
            {
                continue; // an earlier retry of this loop already completed it, e.g. as a dependency
            }

            if (context is null && attempt.Reason == "invalid_plugin_version" &&
                !IsManagedPlugin(attempt.Slug))
            {
                if (_plainPluginObserver?.RetryInvalid(attempt.Slug) == true)
                {
                    var plain = _contexts.FirstOrDefault(c => c.Slug == attempt.Slug);
                    if (plain is not null)
                    {
                        plain.State = GetEnabledPluginSlugs().Contains(attempt.Slug)
                            ? EEditorPluginState.Activated : EEditorPluginState.Deactivated;
                    }
                }

                continue;
            }

            if (context is null && !IsManagedPlugin(attempt.Slug))
            {
                _ePluginContext?.Logger.Error($"Cannot retry {attempt.Slug}: {attempt.Reason} requires manual recovery.");
                continue;
            }

            if (context is null)
            {
                try
                {
                    _host.SetPluginEnabled(attempt.Slug, true);
                    context = _contexts.FirstOrDefault(c => c.Slug == attempt.Slug && c.Plugin is not null);
                }
                catch (Exception ex)
                {
                    _ePluginContext?.Logger.Error($"Cannot recreate {attempt.Slug} for manual retry: {ex.Message}");
                }
            }

            if (context is null)
            {
                _ePluginContext?.Logger.Error(
                    $"Cannot retry {attempt.Slug}: no editor plugin instance is available. Repair it manually.");
                continue;
            }

            context.RefreshMetadata(ReadInstalledMetadata(context.Slug)); // picks up a repaired plugin.cfg, e.g. a version that was missing
            if (attempt.TargetState == PersistedPluginState.Activated)
            {
                EnableEPlugin(context, manualRetry: true);
            }
            else
            {
                DisableEPlugin(context, manualRetry: true);
                if (context.State == EEditorPluginState.Deactivated && _host.IsPluginEnabled(context.Slug))
                {
                    _host.SetPluginEnabled(context.Slug, false);
                }
            }
        }
    }

    /// <summary>
    /// Orders blocked attempts so a failed hard dependency is retried before the plugins that need it. Otherwise
    /// a dependant would be retried while its dependency is still in error and fail again.
    /// </summary>
    internal IReadOnlyList<LocalPluginAttempt> OrderRetries(IReadOnlyCollection<LocalPluginAttempt> attempts)
    {
        var bySlug = attempts.ToDictionary(a => a.Slug, StringComparer.Ordinal);
        var ordered = new List<LocalPluginAttempt>(attempts.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(LocalPluginAttempt attempt)
        {
            if (!visited.Add(attempt.Slug))
            {
                return; // already ordered, or a dependency cycle
            }

            foreach (var dependency in GetHardDependencySlugs(attempt.Slug))
            {
                if (bySlug.TryGetValue(dependency, out var blockedDependency))
                {
                    Visit(blockedDependency);
                }
            }

            ordered.Add(attempt);
        }

        foreach (var attempt in attempts)
        {
            Visit(attempt);
        }

        return ordered;
    }

    private IEnumerable<string> GetHardDependencySlugs(string slug)
    {
        var context = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
        if (context is null)
        {
            return [];
        }

        if (context.IsRecipeCreated)
        {
            return context.Builder.PluginRecipe.PluginDependencies.Select(d => d.Slug);
        }

        try
        {
            // a throw-away builder: a failing CreateRecipe must not leave a half-filled recipe on the context
            var builder = EEditorPluginBuilder.Create();
            context.Plugin!.CreateRecipe(builder);
            return builder.PluginRecipe.PluginDependencies.Select(d => d.Slug);
        }
        catch (Exception)
        {
            return []; // the retry itself reports the recipe failure
        }
    }

    private string DescribeLocalState(string slug)
    {
        if (_stateStore?.IsReadOnly == true)
        {
            return "plugin state files cannot be read";
        }

        return _stateStore?.GetLocal(slug)?.Reason ?? "unknown";
    }

    internal bool JoinTransition(PluginContext context, PersistedPluginState target, bool manualRetry = false)
    {
        if (_stateStore is null)
        {
            return true; // EGlobal's existing headless tests do not initialize the editor.
        }

        if (_recipeUpdateJournal is not null)
        {
            RegisterUpdateParticipant(context.Slug, context.Metadata?.Version);
            return true;
        }

        // A plugin joins again when its operation resumes, e.g. it is disabled once its dependants are done; the
        // transition's own local marker must not block it then.
        if (_transition?.Participants.TryGetValue(context, out var joinedTarget) == true)
        {
            return joinedTarget == target;
        }

        var ownMarker = _transition is not null && _stateStore.GetLocal(context.Slug)?.AttemptId == _transition.AttemptId;
        if (_stateStore.IsReadOnly || (_stateStore.IsBlocked(context.Slug) && !manualRetry && !ownMarker))
        {
            context.Logger?.Error(
                $"Plugin {context.Slug} is blocked by local state ({DescribeLocalState(context.Slug)}). Use manual retry after recovery.");
            context.State = EEditorPluginState.Error;
            FailTransition("participant_blocked");
            return false;
        }

        var version = context.Metadata?.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            context.Logger?.Error(
                $"Plugin {context.Slug} has no version in plugin.cfg. Add one and use manual retry.");
            _stateStore.TryRecordInvalid(context.Slug, version, "invalid_plugin_version", manualRetry);
            context.State = EEditorPluginState.Error;
            FailTransition("invalid_plugin_version");
            return false;
        }

        if (_transition is null)
        {
            if (!_stateStore.TryBeginAttempt(context.Slug, version, target, out var id, manualRetry))
            {
                context.State = EEditorPluginState.Error;
                return false;
            }

            _transition = new PluginTransition(id);
            _transitionFailed = false;
        }
        else if (!_stateStore.TryAddParticipant(_transition.AttemptId, context.Slug, version, target))
        {
            context.State = EEditorPluginState.Error;
            FailTransition("cannot_record_participant");
            return false;
        }

        _transition.Participants.Add(context, target);
        return true;
    }

    private void FailTransition(string reason)
    {
        if (_recipeUpdateJournal is not null)
        {
            _recipeUpdateJournal.Failure = reason;
            _recipeUpdateJournal.Save();
            _toCheckEnable.Clear(); _toCheckDisable.Clear();
            return;
        }
        if (_transition is null)
        {
            return;
        }

        _stateStore?.TryFail(_transition.AttemptId, PersistedPluginState.Failed, reason);
        foreach (var participant in _transition.Participants.Keys)
        {
            participant.State = EEditorPluginState.Error;
            participant.ErrorDetail ??= new InvalidOperationException($"Plugin operation failed: {reason}");
        }

        _toCheckEnable.Clear();
        _toCheckDisable.Clear();
        _transition = null;
        _transitionFailed = true;
    }

    internal bool FinishTransition()
    {
        if (_recipeUpdateJournal is not null) return false;
        if (_transition is null)
        {
            return !_transitionFailed;
        }

        if (_toCheckEnable.Count > 0 || _toCheckDisable.Count > 0 || _transitionHold > 0)
        {
            return false;
        }

        var transition = _transition;
        if (transition.Participants.Any(x => x.Key.State == EEditorPluginState.Error))
        {
            FailTransition("recipe_operation_failed");
            return false;
        }

        if (transition.Participants.Any(x =>
                x.Key.State != (x.Value == PersistedPluginState.Activated
                    ? EEditorPluginState.Activated
                    : EEditorPluginState.Deactivated)))
        {
            return false; // a dependency has not completed its callback yet
        }

        if (_ePluginContext is not null)
        {
            var cli = GetOrCreateContext(_ePluginContext).Cli as ICheckedDotnetCli;
            if (cli is null || !cli.TryRebuildSolution())
            {
                FailTransition("solution_build_failed");
                return false;
            }
        }

        var completed = transition.Participants.Select(x =>
            new SharedPluginState(x.Key.Slug,
                _stateStore?.GetShared(x.Key.Slug)?.Version ?? x.Key.Metadata?.Version ?? "",
                x.Value)).ToArray();
        if (_stateStore?.TryComplete(transition.AttemptId, completed) == false)
        {
            FailTransition("state_commit_failed");
            return false;
        }

        _transition = null;
        return true;
    }

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
            if (_ePluginContext is null)
            {
                AddInitializer(initialize);
            }
            else
            {
                RunInitializer(initialize);
            }
        }
    }

    public IDotnetCli? GetCli(ILogger? logger)
    {
        return CliService?.Create(logger);
    }

    private void EnsureEEditorPluginEnabled(PluginContext context)
    {
        if (!_host.IsPluginEnabled("ePlugin"))
        {
            _toCheckEnable.Push(context);
            _host.SetPluginEnabled("ePlugin", true);
        }
    }

    public void EnableEPlugin(PluginContext context, bool refreshAtEnd = true, bool manualRetry = false)
    {
        EnsureEEditorPluginEnabled(context);

        if (context.Plugin is null)
        {
            return;
        }

        if (!IsValid())
        {
            _toCheckEnable.Push(context);
            return;
        }

        if (context.State == EEditorPluginState.Activated && !manualRetry)
        {
            // already activated, nothing to do
            return;
        }

        if ((context.State is EEditorPluginState.Deactivated or EEditorPluginState.Error) && !manualRetry)
        {
            // already failed, nothing can be done here
            return;
        }

        if (manualRetry)
        {
            context.State = EEditorPluginState.Created;
            context.ErrorDetail = null;
        }

        // before anything is recorded or installed. A manual retry or an update continues work whose licenses were
        // already accepted; updates review changed licenses before they are installed.
        if (!manualRetry && _recipeUpdateJournal is null && !PassesLicenseGate(context))
        {
            return;
        }

        if (!JoinTransition(context, PersistedPluginState.Activated, manualRetry))
        {
            return;
        }

        if (!_host.IsPluginEnabled(context.Slug))
        {
            _host.SetPluginEnabled(context.Slug, true);
        }

        if (!TryEnsureRecipe(context))
        {
            FailTransition("recipe_creation_failed");
            return;
        }

        // A plugin that needs itself, also through other plugins, would wait for itself forever.
        if (FindDependencyCycle(context.Slug) is { } cycle)
        {
            var reason = $"Plugin dependencies form a cycle: {string.Join(" -> ", cycle)}.";
            context.Logger?.Error(reason);
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = new InvalidOperationException(reason);
            _toCheckEnable.Push(context);
            FailAllUncheckedPluginsAndRefresh(reason);
            return;
        }

        // check dependencies
        var recipe = context.Builder.PluginRecipe;
        foreach (var dependency in recipe.PluginDependencies)
        {
            if (_recipeUpdateJournal is not null && !_host.IsPluginEnabled(dependency.Slug))
            {
                EnsureUpdateDependency(dependency);
            }

            if (!_host.IsPluginEnabled(dependency.Slug))
            {
                if (IsManagedPlugin(dependency.Slug))
                {
                    _toCheckEnable.Push(context);
                    _host.SetPluginEnabled(dependency.Slug, true);
                    return; // EnableEPlugin will be called by newly enabled plugin, stop here
                }

                // A plain plugin runs no framework code when it is enabled, so nothing would resume this plugin. It is
                // enabled right away instead, and so are the optional recipes of other plugins that it satisfies.
                if (!EnablePlainDependency(context, dependency.Slug))
                {
                    return;
                }
            }

            if (dependency.Version is not null)
            {
                var dependencyContext = _contexts.FirstOrDefault(c => c.Slug == dependency.Slug);
                // a plain plugin enabled in this session has no context
                var dependencyVersion = dependencyContext?.Metadata?.Version ?? InstalledVersion(dependency.Slug);
                if (dependencyVersion is null)
                {
                    context.Logger?.Warn($"Plugin {dependency.Slug} not found!");
                    _toCheckEnable.Push(context);
                    FailAllUncheckedPluginsAndRefresh($"Plugin dependency {dependency.Slug} not found!");
                    return;
                }

                if (MatchesVersion(dependencyVersion, dependency.Version, context.Logger))
                {
                    if (dependencyContext?.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
                    {
                        context.Logger?.Warn(
                            $"Plugin dependency {dependency.Slug} not ready but needed by {context.Slug}!");
                        _toCheckEnable.Push(context);
                        FailAllUncheckedPluginsAndRefresh(
                            $"Plugin dependency {dependency.Slug} not ready but needed by {context.Slug}!");
                        return;
                    }
                }
                else
                {
                    context.Logger?.Warn(
                        $"Dependency {dependency.Slug} {dependencyVersion} does not match needed {dependency.Version} of {context.Slug}!");

                    _toCheckEnable.Push(context);
                    FailAllUncheckedPluginsAndRefresh(
                        $"Dependency {dependency.Slug} {dependencyVersion} does not match needed {dependency.Version} of {context.Slug}!");
                    return;
                }
            }

            var managedDependency = _contexts.FirstOrDefault(c => c.Slug == dependency.Slug && c.Plugin is not null);
            if (managedDependency?.State == EEditorPluginState.Error)
            {
                _toCheckEnable.Push(context);
                FailAllUncheckedPluginsAndRefresh($"Plugin dependency {dependency.Slug} failed.");
                return;
            }

            if (managedDependency is not null && managedDependency.State != EEditorPluginState.Activated)
            {
                _toCheckEnable.Push(context);
                return; // its activation callback will resume the waiting plugin
            }

            if (_ePluginContext?.EnableDebugLogging == true)
            {
                context.Logger?.Log($"Dependency {dependency.Slug} {dependency.Version} ready for {context.Slug}.");
            }
        }

        //all dependencies are ready, we can finally install the requested plugin
        ActivationProgress.SetText($"Activating {context.Name}...");
        try
        {
            InstallEPlugin(context, recipe);
        }
        catch (Exception ex)
        {
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            context.Logger?.Error($"Installing {context.Slug} failed: {ex.Message}");
        }

        if (context.State is not EEditorPluginState.Error)
        {
            // this plugin may satisfy optional dependencies of plugins that were activated before it
            try
            {
                ReevaluateOptionalDependencies(context.Slug);
            }
            catch (Exception ex)
            {
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = ex;
                FailTransition("optional_recipe_failed");
            }
        }

        if (refreshAtEnd)
        {
            // trigger install for waiting plugins
            while (_toCheckEnable.Count > 0)
            {
                var nextPlugin = _toCheckEnable.Pop();

                EnableEPlugin(nextPlugin, false);
            }

            if (FinishTransition())
            {
                RefreshEditor(rebuild: false);
            }
        }
    }

    /// <summary>
    /// Enables a plain plugin that <paramref name="context"/> requires. Returns false when it cannot be enabled, which
    /// fails the activation like a missing dependency.
    /// </summary>
    private bool EnablePlainDependency(PluginContext context, string slug)
    {
        _contexts.RemoveAll(c => c.Slug == slug && c.Plugin is null);
        _host.SetPluginEnabled(slug, true);
        if (!_host.IsPluginEnabled(slug))
        {
            context.Logger?.Warn($"Plugin {slug} cannot be enabled!");
            _toCheckEnable.Push(context);
            FailAllUncheckedPluginsAndRefresh($"Plugin dependency {slug} cannot be enabled!");
            return false;
        }

        // its license was reviewed with the plugin that requires it
        NotePluginToggle(slug, true);
        try
        {
            ReevaluateOptionalDependencies(slug);
        }
        catch (Exception ex)
        {
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            FailTransition("optional_recipe_failed");
            return false;
        }

        return true;
    }

    private string? InstalledVersion(string slug) => ReadInstalledMetadata(slug)?.Version;

    /// <summary>The plugin.cfg of a plugin below addons, enabled or not; null when it cannot be read.</summary>
    private EEditorPluginMetadata? ReadInstalledMetadata(string slug) =>
        EditorPluginExtensions.ReadMetadata($"{_host.PluginDirectory(slug)}/plugin.cfg");

    private void FailAllUncheckedPluginsAndRefresh(string reason)
    {
        // disabling a plugin runs its DisableEPlugin, which leaves a plugin that is still waiting alone
        foreach (var plugin in _toCheckEnable.ToArray())
        {
            plugin.State = EEditorPluginState.Error;
            plugin.ErrorDetail ??= new InvalidOperationException(reason);
            _host.SetPluginEnabled(plugin.Slug, false);
        }

        _toCheckEnable.Clear();

        FailTransition("dependency_resolution_failed");
        RefreshEditor();
    }

    /// <summary>
    /// Determines which optional plugin dependencies of <paramref name="recipe"/> are currently satisfied and
    /// returns their nested recipes. An optional dependency is satisfied when its plugin is enabled and, if a
    /// version constraint was given, the installed version matches. Unsatisfied ones are skipped — they never
    /// enable a plugin and never fail the activation.
    /// </summary>
    /// <param name="ignoreSlug">
    /// When given, that plugin is treated as if it were not enabled. Used to reconstruct which optional
    /// recipes were already installed before a plugin became available.
    /// </param>
    /// <param name="assumeEnabledSlug">
    /// When given, that plugin is treated as if it were still enabled. Used to reconstruct which optional
    /// recipes were installed while a plugin that is being disabled right now was still available.
    /// </param>
    /// <param name="requireReady">
    /// When true, an enabled optional plugin that is in the error state (e.g. blocked by local state) does not
    /// count as satisfied. Used when installing; reconstructing what was installed earlier leaves it false.
    /// </param>
    internal List<EEditorPluginRecipe.OptionalPlugin> ResolveOptionalRecipes(PluginContext context,
        EEditorPluginRecipe recipe, string? ignoreSlug = null, string? assumeEnabledSlug = null,
        bool requireReady = false)
    {
        var resolved = new List<EEditorPluginRecipe.OptionalPlugin>();

        foreach (var optional in recipe.OptionalPluginDependencies)
        {
            if (optional.Slug == ignoreSlug)
            {
                continue;
            }

            if (requireReady && optional.Slug != assumeEnabledSlug &&
                _contexts.FirstOrDefault(c => c.Slug == optional.Slug && c.Plugin is not null)
                    is { State: EEditorPluginState.Error })
            {
                context.Logger?.Warn(
                    $"Optional dependency {optional.Slug} is in an error state, skipping its recipe for {context.Slug}.");
                continue;
            }

            if (optional.Slug != assumeEnabledSlug && !_host.IsPluginEnabled(optional.Slug))
            {
                context.Logger?.Log(
                    $"Optional dependency {optional.Slug} not enabled, skipping its recipe for {context.Slug}.");
                continue;
            }

            if (optional.Version is not null)
            {
                var optionalContext = _contexts.FirstOrDefault(c => c.Slug == optional.Slug);
                // a plain plugin enabled in this session has no context
                var optionalVersion = optionalContext?.Metadata?.Version ?? InstalledVersion(optional.Slug) ?? "0.0";

                if (!MatchesVersion(optionalVersion, optional.Version, context.Logger))
                {
                    context.Logger?.Log(
                        $"Optional dependency {optional.Slug} {optionalVersion} does not match needed {optional.Version}, skipping its recipe for {context.Slug}.");
                    continue;
                }
            }

            context.Logger?.Log($"Optional dependency {optional.Slug} satisfied for {context.Slug}.");
            resolved.Add(optional);
        }

        return resolved;
    }

    /// <summary>
    /// Re-checks the optional dependencies of every other installed plugin now that the plugin
    /// <paramref name="enabledSlug"/> is available, and installs the nested recipes that became
    /// satisfied. This makes activation order irrelevant: a plugin that was activated before its optional
    /// dependency still picks it up once that dependency is enabled.
    /// </summary>
    private void ReevaluateOptionalDependencies(string enabledSlug)
    {
        foreach (var other in _contexts.ToArray())
        {
            if (other.Slug == enabledSlug || other.Plugin is null)
            {
                continue;
            }

            if (other.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
            {
                continue;
            }

            var otherRecipe = GetInstalledOptionalRecipeOrNull(other, enabledSlug);
            if (otherRecipe is null)
            {
                continue;
            }

            // without a snapshot (the context was rebuilt after an assembly reload) assume everything that
            // was already satisfied before this plugin appeared is installed. Applied entries stay in the
            // snapshot even when they are no longer satisfied, so uninstall still reverses them.
            var applied = new List<EEditorPluginRecipe.OptionalPlugin>(other.AppliedOptionalDependencies ??
                ResolveOptionalRecipes(other, otherRecipe, ignoreSlug: enabledSlug));
            other.AppliedOptionalDependencies = applied;

            foreach (var optional in ResolveOptionalRecipes(other, otherRecipe, requireReady: true))
            {
                if (applied.Contains(optional) || _recipeUpdateJournal is not null && applied.Any(a => a.Slug == optional.Slug && a.Version == optional.Version))
                {
                    continue;
                }

                other.Logger?.Log(
                    $"Installing optional recipe of {other.Slug} that became satisfied by {enabledSlug}.");

                if (!JoinTransition(other, PersistedPluginState.Activated))
                {
                    throw new InvalidOperationException($"Cannot record optional recipe installation for {other.Slug}.");
                }

                applied.Add(optional);
                ApplyRecipe(other, optional.Recipe);

                if (other.State is EEditorPluginState.Error)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Reverses the nested recipes that other installed plugins got because the plugin
    /// <paramref name="disabledSlug"/> was available. Without this their code would keep referencing a
    /// plugin that is no longer installed and the project would stop compiling.
    /// </summary>
    private void UninstallOptionalDependenciesOn(string disabledSlug)
    {
        foreach (var other in _contexts.ToArray())
        {
            if (other.Slug == disabledSlug || other.Plugin is null)
            {
                continue;
            }

            if (other.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
            {
                continue;
            }

            var otherRecipe = GetInstalledOptionalRecipeOrNull(other, disabledSlug);
            if (otherRecipe is null)
            {
                continue;
            }

            // without a snapshot (the context was rebuilt after an assembly reload) reconstruct what was
            // installed while the plugin being disabled was still available.
            var applied = other.AppliedOptionalDependencies ??
                          ResolveOptionalRecipes(other, otherRecipe, assumeEnabledSlug: disabledSlug);
            other.AppliedOptionalDependencies = applied;

            foreach (var optional in applied.Where(o => o.Slug == disabledSlug).ToArray())
            {
                other.Logger?.Log(
                    $"Removing optional recipe of {other.Slug} that depended on {disabledSlug}.");

                if (!JoinTransition(other, PersistedPluginState.Activated))
                {
                    throw new InvalidOperationException($"Cannot record optional recipe removal for {other.Slug}.");
                }

                ReverseRecipe(other, optional.Recipe);
                applied.Remove(optional);
            }
        }
    }

    /// <summary>
    /// Returns the recipe of <paramref name="context"/> when it is installed and declares an optional
    /// dependency on <paramref name="slug"/>, otherwise <see langword="null"/>.
    /// </summary>
    private static EEditorPluginRecipe? GetInstalledOptionalRecipeOrNull(PluginContext context, string slug)
    {
        // installed either in an earlier session (Activated after a reload) or in this one (it has a
        // snapshot). Anything else was never installed and resolves its optionals on activation.
        if (context.State is not EEditorPluginState.Activated && context.AppliedOptionalDependencies is null)
        {
            return null;
        }

        if (!TryEnsureRecipe(context))
        {
            return null;
        }

        var recipe = context.Builder.PluginRecipe;
        return recipe.OptionalPluginDependencies.Any(o => o.Slug == slug) ? recipe : null;
    }

    private void InstallEPlugin(PluginContext context, EEditorPluginRecipe recipe)
    {
        // track applied optional dependencies as we go so a failure mid-install can still be reversed.
        var applied = new List<EEditorPluginRecipe.OptionalPlugin>();
        context.AppliedOptionalDependencies = applied;

        ApplyRecipe(context, recipe);

        if (context.State is EEditorPluginState.Error)
        {
            return;
        }

        foreach (var optional in ResolveOptionalRecipes(context, recipe, requireReady: true))
        {
            applied.Add(optional);
            ApplyRecipe(context, optional.Recipe);

            if (context.State is EEditorPluginState.Error)
            {
                return;
            }
        }

        context.State = EEditorPluginState.Activated;
    }

    private void ApplyRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        try
        {
            foreach (var nuget in recipe.Nugets) TrackRecipeOperation(context, new(RecipeOperationKind.AddNuget, Nuget: nuget), () => ApplyNuget(context, nuget));
            foreach (var project in recipe.Projects) TrackRecipeOperation(context, new(RecipeOperationKind.AddProject, Project: project), () => ApplyProject(context, project));
            foreach (var directory in recipe.Directories) TrackRecipeOperation(context, new(RecipeOperationKind.ShowDirectory, Directory: directory), () => ShowHideHelper.ShowDirectory(context, directory));
            foreach (var autoload in recipe.Autoloads) TrackRecipeOperation(context, new(RecipeOperationKind.AddAutoload, Autoload: autoload), () => ApplyAutoload(context, autoload));
        }
        catch (Exception ex)
        {
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            context.Logger?.Error($"Applying recipe for {context.Slug} failed: {ex.Message}");
        }
    }

    public void DisableEPlugin(PluginContext context, bool refreshAtEnd = true, bool manualRetry = false)
    {
        if (context.Plugin is null)
        {
            return;
        }

        if (_toCheckEnable.Contains(context))
        {
            // plugin in process of enablement, skip disable
            return;
        }

        if (context.State == EEditorPluginState.Deactivated && !manualRetry)
        {
            return;
        }

        if (context.State == EEditorPluginState.Error && !manualRetry)
        {
            return;
        }

        if (manualRetry)
        {
            context.State = EEditorPluginState.Activated;
            context.ErrorDetail = null;
        }

        if (!JoinTransition(context, PersistedPluginState.Deactivated, manualRetry))
        {
            return;
        }

        if (!TryEnsureRecipe(context))
        {
            FailTransition("recipe_creation_failed");
            return;
        }

        // disable plugins dependent on this one
        var pluginsToDisable = new List<PluginContext>();
        foreach (var plugin in _contexts)
        {
            if (plugin == context)
            {
                continue;
            }

            if (plugin.State is not EEditorPluginState.Activated)
            {
                continue;
            }

            if (plugin.Plugin is null)
            {
                // not an ePlugin plugin.
                continue;
            }

            if (!TryEnsureRecipe(plugin))
            {
                FailTransition("dependent_recipe_creation_failed");
                return;
            }

            var isDependant = plugin.Builder.PluginRecipe.PluginDependencies.Any(d => d.Slug == context.Slug);
            if (!isDependant)
            {
                continue;
            }

            pluginsToDisable.Add(plugin);
        }

        if (pluginsToDisable.Count > 0)
        {
            _toCheckDisable.Push(context);

            foreach (var plugin in pluginsToDisable)
            {
                if (plugin.State is not EEditorPluginState.Activated)
                {
                    continue;
                }

                if (_host.IsPluginEnabled(plugin.Slug))
                {
                    _toCheckDisable.Push(plugin);
                    _host.SetPluginEnabled(plugin.Slug, false);
                }
            }

            return;
        }

        ActivationProgress.SetText($"Deactivating {context.Name}...");
        try
        {
            UninstallEPlugin(context, context.Builder.PluginRecipe);

            // other plugins may have installed code that depends on this one via an optional dependency
            UninstallOptionalDependenciesOn(context.Slug);
        }
        catch (Exception ex)
        {
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            context.Logger?.Error($"Deactivating {context.Slug} failed: {ex.Message}");
            FailTransition("deactivation_failed");
            return;
        }

        context.State = EEditorPluginState.Deactivated;

        // @ local dependencies: can not disable as we do not know which are needed. There is no way to track manual or
        //                       auto enabled plugins right now.

        if (refreshAtEnd)
        {
            // trigger uninstall for waiting plugins
            while (_toCheckDisable.Count > 0)
            {
                var nextPlugin = _toCheckDisable.Pop();

                DisableEPlugin(nextPlugin, false);
            }

            if (FinishTransition())
            {
                RefreshEditor(rebuild: false);
            }
        }
    }

    private void UninstallEPlugin(PluginContext context, EEditorPluginRecipe recipe)
    {
        // without a snapshot (e.g. the context was rebuilt after an assembly reload) fall back to resolving
        // the optional dependencies against the current editor state.
        var applied = context.AppliedOptionalDependencies ?? ResolveOptionalRecipes(context, recipe);

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            ReverseRecipe(context, applied[i].Recipe);
        }

        ReverseRecipe(context, recipe);
        context.AppliedOptionalDependencies = null;
    }

    private void ReverseRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        foreach (var autoload in recipe.Autoloads) ReverseAutoload(context, autoload);
        foreach (var directory in recipe.Directories) ShowHideHelper.HideDirectory(context, directory);
        foreach (var project in recipe.Projects) ReverseProject(context, project);
        foreach (var nuget in recipe.Nugets) ReverseNuget(context, nuget);
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
    private void ReloadContexts(ILoggerFactory? loggerFactory, bool changeTriggered)
    {
        var logger = loggerFactory?.CreateLogger(GetType().FullName ?? "UNKNOWN");
        var parentNode = EditorInterface.Singleton.GetBaseControl().GetParent();
        var children = parentNode.GetChildren();

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

                context.State = changeTriggered
                    ?
                    // was a change to plugins, so this is a new plugin need to bootstrap.
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

    /// <summary>
    /// Creates the plugin's recipe on its context, once. When <see cref="IEEditorPlugin.CreateRecipe"/> throws, the plugin
    /// is in error with the exception as its detail, and its recipe stays uncreated and empty for a retry.
    /// </summary>
    private static bool TryEnsureRecipe(PluginContext context)
    {
        if (context.IsRecipeCreated)
        {
            return true;
        }

        try
        {
            context.Plugin!.CreateRecipe(context.Builder);
            context.IsRecipeCreated = true;
            return true;
        }
        catch (Exception ex)
        {
            context.Builder = EEditorPluginBuilder.Create();
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            context.Logger?.Error($"Creating the recipe of {context.Slug} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The plugins from <paramref name="slug"/> back to itself when its hard dependencies lead back to it, also when it
    /// requires itself; null without a cycle. Plain plugins have no recipe, so only ePlugins are followed.
    /// </summary>
    internal IReadOnlyList<string>? FindDependencyCycle(string slug)
    {
        var path = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);

        List<string>? Visit(string current)
        {
            var index = path.IndexOf(current);
            if (index >= 0)
            {
                return [..path.Skip(index), current];
            }

            if (current == FrameworkSlug || !done.Add(current))
            {
                return null;
            }

            path.Add(current);
            foreach (var dependency in RecipeOf(current)?.PluginDependencies ?? [])
            {
                if (Visit(dependency.Slug) is { } cycle)
                {
                    return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            return null;
        }

        return Visit(slug);
    }

    internal static bool MatchesVersion(string? givenVersion, string condition, ILogger? logger)
    {
        var matches = VersionConstraint.Matches(givenVersion, condition, out var error);
        if (error is not null)
        {
            logger?.Warn($"Dependency version cannot be checked: {error}.");
        }

        return matches;
    }

    public void AddInitializer(IInitialize initializer)
    {
        _toInitialize.Enqueue(initializer);

        if (_ePluginContext?.EnableDebugLogging ?? false)
        {
            _ePluginContext.Logger?.Log($"Initializer {initializer.GetType().FullName} enqueued.");
        }
    }
}
#endif
