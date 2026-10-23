#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    internal bool IsChangingPlugins => _transition is not null || _toCheckEnable.Count > 0 ||
        _toCheckDisable.Count > 0 || _transitionHold > 0;

    private sealed class PluginTransition(Guid attemptId)
    {
        public Guid AttemptId { get; } = attemptId;
        public Dictionary<PluginContext, PersistedPluginState> Participants { get; } = [];
    }


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

        _stateStore = new PluginStateStore(StateFilePath, plugin.Logger);
        _stateStore.Load();
        InitializeUpdateJournals();
        ReloadContexts(_loggerFactory, false);
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

    /// <summary>Activates the plugins that were enabled before the framework was initialized.</summary>
    internal void ResumeWaitingPlugins()
    {
        _enabledWithFramework.Clear();
        if (_toCheckEnable.Count == 0) return;

        ResumeWaitingInRounds();

        if (FinishTransition())
        {
            RefreshEditor(rebuild: false);
        }
    }

    /// <summary>
    /// Activates the plugins waiting on <see cref="_toCheckEnable"/>, in rounds: each takes them off the stack first,
    /// because one whose dependency is still not activated pushes itself back, and popping until empty would never end.
    /// A later round resumes the plugins whose dependencies an earlier one activated; a round that resumes none of them
    /// leaves them waiting for their dependencies' activation callbacks.
    /// </summary>
    private void ResumeWaitingInRounds()
    {
        while (_toCheckEnable.Count > 0)
        {
            var waiting = _toCheckEnable.ToArray();
            _toCheckEnable.Clear();
            foreach (var nextPlugin in waiting)
            {
                EnableEPlugin(nextPlugin, false);
            }

            if (_toCheckEnable.ToHashSet().SetEquals(waiting) && waiting.All(c => c.State == EEditorPluginState.Created))
            {
                break;
            }
        }
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

    /// <summary>Retries every failed or interrupted attempt.</summary>
    public void RetryFailedPlugins() => RetryFailedPlugins(null);

    /// <summary>
    /// Retries the failed attempts of <paramref name="slugs"/> together with those <see cref="RetryScope"/> adds; null
    /// retries every attempt.
    /// </summary>
    public void RetryFailedPlugins(IReadOnlyCollection<string>? slugs)
    {
        RefreshPlainPlugins();
        if (_stateStore is null || _stateStore.IsReadOnly)
        {
            return;
        }

        Retry(slugs is null ? _stateStore.LocalAttempts : RetryScope(slugs, dependencies: true), disable: null);
    }

    /// <summary>
    /// Gives up on a plugin whose activation or deactivation failed: undoes what it installed and disables it. The other
    /// plugins of the same attempt are retried. False when it has no such attempt, e.g. after a failed update, which is
    /// retried or rolled back instead; the ePlugin Manager then disables it like any other plugin.
    /// </summary>
    public bool DisableFailedPlugin(string slug)
    {
        RefreshPlainPlugins();
        if (_stateStore is null || _stateStore.IsReadOnly || _stateStore.GetLocal(slug) is not { } attempt || IsUpdateAttempt(attempt))
        {
            return false;
        }

        if (attempt.Reason == "invalid_plugin_version")
        {
            // The version is checked before anything is installed, and a plugin without one cannot complete a transition.
            if (!_stateStore.TryAbandonAttempt(attempt.AttemptId)) return false;
            if (_contexts.FirstOrDefault(c => c.Slug == slug) is { } invalid) invalid.State = EEditorPluginState.Deactivated;
            if (_host.IsPluginEnabled(slug)) _host.SetPluginEnabled(slug, false);
            return true;
        }

        Retry(RetryScope([slug], dependencies: false), disable: slug);
        return true;
    }

    /// <summary>
    /// The failed attempts a retry of <paramref name="slugs"/> covers: theirs, those of the plugins that failed in the
    /// same attempt (a retry replaces the attempt of all of them), and with <paramref name="dependencies"/> those of the
    /// failed hard dependencies they need.
    /// </summary>
    internal IReadOnlyCollection<LocalPluginAttempt> RetryScope(IEnumerable<string> slugs, bool dependencies)
    {
        if (_stateStore is null) return [];
        var attempts = _stateStore.LocalAttempts;
        var scope = new Dictionary<string, LocalPluginAttempt>(StringComparer.Ordinal);
        var queue = new Queue<string>(slugs);
        while (queue.TryDequeue(out var slug))
        {
            if (_stateStore.GetLocal(slug) is not { } attempt || !scope.TryAdd(slug, attempt)) continue;
            foreach (var related in attempts.Where(a => a.AttemptId == attempt.AttemptId)) queue.Enqueue(related.Slug);
            if (dependencies)
                foreach (var dependency in GetHardDependencySlugs(slug)) queue.Enqueue(dependency);
        }
        return scope.Values;
    }

    private bool IsUpdateAttempt(LocalPluginAttempt attempt) =>
        attempt.Reason.StartsWith("update_", StringComparison.Ordinal) ||
        _updateJournals?.Read().Any(j => j.AttemptId == attempt.AttemptId) == true;

    /// <param name="disable">A plugin whose failed attempt is retried as a deactivation, whatever it aimed for.</param>
    private void Retry(IReadOnlyCollection<LocalPluginAttempt> attempts, string? disable)
    {
        if (_stateStore is null) return;
        var retriedUpdates = new HashSet<Guid>();
        foreach (var failed in OrderRetries(attempts))
        {
            var attempt = failed.Slug == disable ? failed with { TargetState = PersistedPluginState.Deactivated } : failed;
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

        // A new editor instance of a plugin that joined already, e.g. one a manual retry enabled again after its failed
        // activation disabled it: it takes the place of the old one, which Godot freed. The attempt has one entry per slug.
        foreach (var stale in _transition.Participants.Keys.Where(c => c.Slug == context.Slug && c != context).ToArray())
        {
            _transition.Participants.Remove(stale);
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
            AddInitializer(initialize);
        }
    }

    public IDotnetCli? GetCli(ILogger? logger)
    {
        return CliService?.Create(logger, _host.ProjectRoot, _host.AssemblyName);
    }

    private void EnsureEEditorPluginEnabled(PluginContext context)
    {
        if (!_host.IsPluginEnabled(FrameworkSlug))
        {
            // Waits on the stack below until the framework is initialized, which happens on its first frame.
            _enabledWithFramework.Add(context);
            _host.SetPluginEnabled(FrameworkSlug, true);
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
            // A plugin disabled by its failed activation has no editor instance left: Godot created a new one, whose
            // EnableEPlugin did this activation in its place. This context's instance is freed; it must not go on.
            if (_contexts.LastOrDefault(c => c.Slug == context.Slug && c.Plugin is not null) is { } current && current != context)
            {
                _contexts.Remove(context);
                return;
            }
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

        var recipe = context.Builder.PluginRecipe;
        foreach (var dependency in recipe.PluginDependencies)
        {
            if (CheckDependency(context, dependency) != DependencyReadiness.Ready)
            {
                return;
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
            ResumeWaitingInRounds();

            if (FinishTransition())
            {
                RefreshEditor(rebuild: false);
            }
        }
    }

    private enum DependencyReadiness
    {
        Ready,
        // the dependency's own activation resumes the dependant
        Wait,
        // the activation failed and was reported
        Failed
    }

    /// <summary>
    /// Makes a hard dependency of <paramref name="context"/> available and checks it before the plugin is installed. It is
    /// ready once it is enabled in a matching version and, for an ePlugin, activated. Its state decides alike with or
    /// without a version constraint: in error or deactivated it fails the dependant, because nothing would resume it.
    /// </summary>
    private DependencyReadiness CheckDependency(PluginContext context, EEditorPluginRecipe.Plugin dependency)
    {
        var slug = dependency.Slug;
        if (_recipeUpdateJournal is not null && !IsDependencyAvailable(slug))
        {
            EnsureUpdateDependency(dependency);
        }

        if (!IsDependencyAvailable(slug))
        {
            if (IsManagedPlugin(slug))
            {
                _toCheckEnable.Push(context);
                _host.SetPluginEnabled(slug, true);
                if (_host.IsPluginEnabled(slug))
                {
                    return DependencyReadiness.Wait; // EnableEPlugin will be called by the newly enabled plugin
                }

                // Godot refused it (e.g. its script does not load), so no activation callback would resume this plugin.
                context.Logger?.Warn($"Plugin {slug} cannot be enabled!");
                FailAllUncheckedPluginsAndRefresh($"Plugin dependency {slug} cannot be enabled!");
                return DependencyReadiness.Failed;
            }

            // A plain plugin runs no framework code when it is enabled, so nothing would resume this plugin. It is
            // enabled right away instead, and so are the optional recipes of other plugins that it satisfies.
            if (!EnablePlainDependency(context, slug))
            {
                return DependencyReadiness.Failed;
            }
        }

        var managed = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
        // a plain plugin has a context only while its editor instance is known, e.g. not when enabled in this session
        var plain = managed is null ? _contexts.FirstOrDefault(c => c.Slug == slug) : null;
        if (dependency.Version is not null)
        {
            var version = (managed ?? plain)?.Metadata?.Version ?? InstalledVersion(slug);
            if (version is null)
            {
                var unknown = $"The version of the GDExtension {slug} is unknown, so it cannot be checked against {dependency.Version}. Install a version of it in the ePlugin Manager.";
                return IsExtensionAddon(slug)
                    ? FailDependency(context, unknown, unknown)
                    : FailDependency(context, $"Plugin {slug} not found!", $"Plugin dependency {slug} not found!");
            }

            if (!MatchesVersion(version, dependency.Version, context.Logger))
            {
                var mismatch = $"Dependency {slug} {version} does not match needed {dependency.Version} of {context.Slug}!";
                return FailDependency(context, mismatch, mismatch);
            }
        }

        if ((managed ?? plain)?.State == EEditorPluginState.Error)
        {
            return FailDependency(context, $"Plugin dependency {slug} failed.", $"Plugin dependency {slug} failed.");
        }

        // A plain plugin is ready while the editor has it enabled; an ePlugin once its activation completed.
        switch (managed?.State)
        {
            case EEditorPluginState.Deactivated:
                var notReady = $"Plugin dependency {slug} not ready but needed by {context.Slug}!";
                return FailDependency(context, notReady, notReady);
            case EEditorPluginState.Created:
                _toCheckEnable.Push(context);
                return DependencyReadiness.Wait; // its activation callback will resume the waiting plugin
        }

        if (_ePluginContext?.EnableDebugLogging == true)
        {
            context.Logger?.Log($"Dependency {slug} {dependency.Version} ready for {context.Slug}.");
        }

        return DependencyReadiness.Ready;
    }

    /// <summary>Fails the activation of <paramref name="context"/> and of every plugin waiting with it.</summary>
    private DependencyReadiness FailDependency(PluginContext context, string warning, string reason)
    {
        context.Logger?.Warn(warning);
        _toCheckEnable.Push(context);
        FailAllUncheckedPluginsAndRefresh(reason);
        return DependencyReadiness.Failed;
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

    /// <summary>The version in a plugin's plugin.cfg, or the recorded one of a GDExtension add-on; null when neither is known.</summary>
    private string? InstalledVersion(string slug) => ReadInstalledMetadata(slug)?.Version ?? (IsExtensionAddon(slug) ? ExtensionAddonVersion(slug) : null);

    /// <summary>The plugin.cfg of a plugin below addons, enabled or not; null when it cannot be read.</summary>
    private EEditorPluginMetadata? ReadInstalledMetadata(string slug) =>
        EditorPluginExtensions.ReadMetadata($"{_host.PluginDirectory(slug)}/plugin.cfg");

    private void FailAllUncheckedPluginsAndRefresh(string reason)
    {
        var failed = _toCheckEnable.ToArray();
        foreach (var plugin in failed)
        {
            plugin.State = EEditorPluginState.Error;
            plugin.ErrorDetail ??= new InvalidOperationException(reason);
        }

        _toCheckEnable.Clear();

        FailTransition("dependency_resolution_failed");
        // Godot may still be running the _EnablePlugin of each of them: the plugin that failed and those waiting for it
        // as their dependency. Disabling one right away would free it inside its own callback, so they are disabled on
        // the next frame. In error, the DisableEPlugin that runs changes nothing.
        Post(() =>
        {
            foreach (var plugin in failed.Where(p => _host.IsPluginEnabled(p.Slug)))
            {
                _host.SetPluginEnabled(plugin.Slug, false);
            }
        });
        // nothing of the failed activation is built: it is retried as a whole
        RefreshEditor(rebuild: false);
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

            if (optional.Slug != assumeEnabledSlug && !IsDependencyAvailable(optional.Slug))
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
            if (other.Slug == enabledSlug || other.Plugin is null || IsBeingDeactivated(other))
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

    /// <summary>Whether the current transition disables the plugin, now or once its dependants are disabled.</summary>
    private bool IsBeingDeactivated(PluginContext context) =>
        _transition?.Participants.GetValueOrDefault(context) == PersistedPluginState.Deactivated || _toCheckDisable.Contains(context);

    /// <summary>
    /// Reverses the nested recipes that other installed plugins got because the plugin
    /// <paramref name="disabledSlug"/> was available. Without this their code would keep referencing a
    /// plugin that is no longer installed and the project would stop compiling.
    /// </summary>
    private void UninstallOptionalDependenciesOn(string disabledSlug)
    {
        foreach (var other in _contexts.ToArray())
        {
            // a plugin this transition disables reverses its whole snapshot itself, e.g. one that a dependant of it
            // declares an optional dependency of
            if (other.Slug == disabledSlug || other.Plugin is null || IsBeingDeactivated(other))
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
            foreach (var directory in recipe.Directories) TrackRecipeOperation(context, new(RecipeOperationKind.ShowDirectory, Directory: directory), () => ShowHideHelper.ShowDirectory(context, directory, _host));
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

        if (!IsValid())
        {
            // Without the framework there is no dotnet CLI or state file to uninstall with, and Godot frees the plugin
            // right after this callback, so its uninstall cannot wait for the framework either. A partial uninstall that
            // records nothing would leave the project in a state no retry knows about.
            var warning = $"{context.Slug} was disabled while the ePlugin framework is not running, so its recipe stays installed. " +
                          "Enable the framework, then enable and disable the plugin again to remove it.";
            if (context.Logger is { } logger) logger.Warn(warning);
            else GD.PushWarning(warning);
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

        // A dependant the editor already disabled runs no DisableEPlugin that would resume this plugin, e.g. because its
        // _DisablePlugin threw before calling it; waiting for it would keep the transition open forever.
        foreach (var plugin in pluginsToDisable.Where(p => !_host.IsPluginEnabled(p.Slug)))
        {
            context.Logger?.Warn($"{plugin.Slug} requires {context.Slug}, but the editor already disabled it; its recipe stays installed.");
        }

        pluginsToDisable.RemoveAll(p => !_host.IsPluginEnabled(p.Slug));
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
        // the snapshot shrinks as its recipes are reversed, so the ones still installed keep what they share with others.
        var applied = (context.AppliedOptionalDependencies ?? ResolveOptionalRecipes(context, recipe)).ToList();
        context.AppliedOptionalDependencies = applied;

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            ReverseRecipe(context, applied[i].Recipe);
            applied.RemoveAt(i);
        }

        ReverseRecipe(context, recipe);
        context.AppliedOptionalDependencies = null;
    }

    private void ReverseRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        var remaining = RemainingRecipes(context, recipe);
        foreach (var autoload in recipe.Autoloads) ReverseAutoload(context, autoload);
        foreach (var directory in recipe.Directories) ShowHideHelper.HideDirectory(context, directory, _host);
        foreach (var project in recipe.Projects) ReverseProject(context, project, remaining);
        foreach (var nuget in recipe.Nugets) ReverseNuget(context, nuget, remaining);
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
    internal void ReloadContexts(ILoggerFactory? loggerFactory, bool changeTriggered)
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

                context.State = changeTriggered || _enabledWithFramework.Contains(context) || _toCheckEnable.Contains(context)
                    ?
                    // was a change to plugins, or the plugin was enabled while the framework was not initialized and
                    // waits for its activation, so this is a new plugin need to bootstrap.
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
