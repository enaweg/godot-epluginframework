#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// This is an internally used class by Enaweg.Plugin and should not be used by anything else. It manages plugin states
/// as well as provides some global values.
/// </summary>
[Tool]
internal sealed class EGlobal
{
    private static EGlobal? _instance = null;

    public static EGlobal Instance
    {
        get
        {
            _instance ??= new EGlobal();

            return _instance;
        }
    }

    private readonly List<PluginContext> _contexts = [];
    private EPluginPlugin? _ePluginContext = null;

    public DotnetVersionManager? CliService { get; private set; } = null;

    private ILoggerFactory? _loggerFactory = null;

    private readonly Stack<PluginContext> _toCheckEnable = new();
    private readonly Stack<PluginContext> _toCheckDisable = new();
    private readonly Queue<IInitialize> _toInitialize = new();


    private EGlobal()
    {
    }

    /// <summary>
    /// This needs to be called first to initialize the ePlugin system.
    /// </summary>
    /// <param name="plugin"></param>
    /// <param name="loggerFactory"></param>
    public void Initialize(EPluginPlugin plugin, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _ePluginContext = plugin;
        plugin.Logger = _loggerFactory.CreateLogger(_ePluginContext.GetType().FullName ?? "UNKNOWN");

        CliService = new DotnetVersionManager(plugin.Logger, plugin.EnableDebugLogging);

        ReloadContexts(_loggerFactory, false);

        if (_toCheckEnable.Any())
        {
            foreach (var pluginContext in _toCheckEnable)
            {
                EnableEPlugin(pluginContext, false);
            }

            RefreshEditor();
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

        ReloadContexts(_loggerFactory, false);
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

    public PluginContext GetOrCreateContext(EditorPlugin pluginBase)
    {
        var context = _contexts.FirstOrDefault(c => c.PluginBase == pluginBase);
        if (context is null)
        {
            var ePlugin = pluginBase as IEEditorPlugin;
            var pluginLogger = _loggerFactory?.CreateLogger(pluginBase.GetType().FullName ?? "UNKNOWN");
            context = new PluginContext(ePlugin, pluginBase, pluginLogger);
            _contexts.Add(context);

            if (context.PluginBase is IInitialize initialize)
            {
                initialize.Initialize(_ePluginContext);
            }
        }

        return context;
    }

    public IDotnetCli? GetCli(ILogger? logger)
    {
        return CliService?.Create(logger);
    }

    private void EnsureEEditorPluginEnabled(PluginContext context)
    {
        if (!EditorInterface.Singleton.IsPluginEnabled("ePlugin"))
        {
            _toCheckEnable.Push(context);
            EditorInterface.Singleton.SetPluginEnabled("ePlugin", true);
        }
    }

    public void EnableEPlugin(PluginContext context, bool refreshAtEnd = true)
    {
        EnsureEEditorPluginEnabled(context);

        if (context.Plugin is null)
        {
            return;
        }

        if (!EditorInterface.Singleton.IsPluginEnabled(context.Slug))
        {
            EditorInterface.Singleton.SetPluginEnabled(context.Slug, true);
        }

        if (!IsValid())
        {
            _toCheckEnable.Push(context);
            return;
        }

        if (context.State == EEditorPluginState.Activated)
        {
            // already activated, nothing to do
            return;
        }

        if (context.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
        {
            // already failed, nothing can be done here
            return;
        }

        if (!context.IsRecipeCreated)
        {
            context.Plugin.CreateRecipe(context.Builder);
            context.IsRecipeCreated = true;
        }

        // check dependencies
        var recipe = context.Builder.PluginRecipe;
        foreach (var dependency in recipe.PluginDependencies)
        {
            if (!EditorInterface.Singleton.IsPluginEnabled(dependency.Slug))
            {
                _toCheckEnable.Push(context);
                EditorInterface.Singleton.SetPluginEnabled(dependency.Slug, true);
                return; // EnableEPlugin will be called by newly enabled plugin, stop here
            }

            if (dependency.Version is not null)
            {
                var dependencyContext = _contexts.FirstOrDefault(c => c.Slug == dependency.Slug);
                if (dependencyContext is null)
                {
                    context.Logger?.Warn($"Plugin {dependency.Slug} not found!");
                    _toCheckEnable.Push(context);
                    FailAllUncheckedPluginsAndRefresh($"Plugin dependency {dependency.Slug} not found!");
                    return;
                }

                var dependencyVersion = dependencyContext.Metadata?.Version ?? "0.0";

                if (MatchesVersion(dependencyVersion, dependency.Version, context.Logger))
                {
                    if (context.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
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
                    context.Logger.Warn(
                        $"Dependency {dependency.Slug} {dependencyVersion} does not match needed {dependency.Version} of {context.Slug}!");

                    _toCheckEnable.Push(context);
                    FailAllUncheckedPluginsAndRefresh(
                        $"Dependency {dependency.Slug} {dependencyVersion} does not match needed {dependency.Version} of {context.Slug}!");
                    return;
                }
            }

            if (_ePluginContext.EnableDebugLogging)
            {
                context.Logger?.Log($"Dependency {dependency.Slug} {dependency.Version} ready for {context.Slug}.");
            }
        }

        //all dependencies are ready, we can finally install the requested plugin
        InstallEPlugin(context, recipe);

        if (context.State is not EEditorPluginState.Error)
        {
            // this plugin may satisfy optional dependencies of plugins that were activated before it
            ReevaluateOptionalDependencies(context);
        }

        if (refreshAtEnd)
        {
            // trigger install for waiting plugins
            while (_toCheckEnable.Any())
            {
                var nextPlugin = _toCheckEnable.Pop();

                EnableEPlugin(nextPlugin, false);
            }

            RefreshEditor();
        }
    }

    private void FailAllUncheckedPluginsAndRefresh(string reason)
    {
        foreach (var plugin in _toCheckEnable)
        {
            plugin.State = EEditorPluginState.Error;
            plugin.ErrorDetail = new Exception(reason);
            EditorInterface.Singleton.SetPluginEnabled(plugin.Slug, false);
        }

        _toCheckEnable.Clear();

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
    internal List<EEditorPluginRecipe.OptionalPlugin> ResolveOptionalRecipes(PluginContext context,
        EEditorPluginRecipe recipe, string? ignoreSlug = null, string? assumeEnabledSlug = null)
    {
        var resolved = new List<EEditorPluginRecipe.OptionalPlugin>();

        foreach (var optional in recipe.OptionalPluginDependencies)
        {
            if (optional.Slug == ignoreSlug)
            {
                continue;
            }

            if (optional.Slug != assumeEnabledSlug && !EditorInterface.Singleton.IsPluginEnabled(optional.Slug))
            {
                context.Logger?.Log(
                    $"Optional dependency {optional.Slug} not enabled, skipping its recipe for {context.Slug}.");
                continue;
            }

            if (optional.Version is not null)
            {
                var optionalContext = _contexts.FirstOrDefault(c => c.Slug == optional.Slug);
                var optionalVersion = optionalContext?.Metadata?.Version ?? "0.0";

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
    /// Re-checks the optional dependencies of every other installed plugin now that
    /// <paramref name="enabledContext"/> is available, and installs the nested recipes that became
    /// satisfied. This makes activation order irrelevant: a plugin that was activated before its optional
    /// dependency still picks it up once that dependency is enabled.
    /// </summary>
    private void ReevaluateOptionalDependencies(PluginContext enabledContext)
    {
        foreach (var other in _contexts.ToArray())
        {
            if (other == enabledContext || other.Plugin is null)
            {
                continue;
            }

            if (other.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
            {
                continue;
            }

            var otherRecipe = GetInstalledOptionalRecipeOrNull(other, enabledContext.Slug);
            if (otherRecipe is null)
            {
                continue;
            }

            // without a snapshot (the context was rebuilt after an assembly reload) assume everything that
            // was already satisfied before this plugin appeared is installed. Applied entries stay in the
            // snapshot even when they are no longer satisfied, so uninstall still reverses them.
            var applied = new List<EEditorPluginRecipe.OptionalPlugin>(other.AppliedOptionalDependencies ??
                ResolveOptionalRecipes(other, otherRecipe, ignoreSlug: enabledContext.Slug));
            other.AppliedOptionalDependencies = applied;

            foreach (var optional in ResolveOptionalRecipes(other, otherRecipe))
            {
                if (applied.Contains(optional))
                {
                    continue;
                }

                other.Logger?.Log(
                    $"Installing optional recipe of {other.Slug} that became satisfied by {enabledContext.Slug}.");

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
    /// Reverses the nested recipes that other installed plugins got because
    /// <paramref name="disabledContext"/> was available. Without this their code would keep referencing a
    /// plugin that is no longer installed and the project would stop compiling.
    /// </summary>
    private void UninstallOptionalDependenciesOn(PluginContext disabledContext)
    {
        foreach (var other in _contexts.ToArray())
        {
            if (other == disabledContext || other.Plugin is null)
            {
                continue;
            }

            if (other.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
            {
                continue;
            }

            var otherRecipe = GetInstalledOptionalRecipeOrNull(other, disabledContext.Slug);
            if (otherRecipe is null)
            {
                continue;
            }

            // without a snapshot (the context was rebuilt after an assembly reload) reconstruct what was
            // installed while the plugin being disabled was still available.
            var applied = other.AppliedOptionalDependencies ??
                          ResolveOptionalRecipes(other, otherRecipe, assumeEnabledSlug: disabledContext.Slug);
            other.AppliedOptionalDependencies = applied;

            foreach (var optional in applied.Where(o => o.Slug == disabledContext.Slug).ToArray())
            {
                other.Logger?.Log(
                    $"Removing optional recipe of {other.Slug} that depended on {disabledContext.Slug}.");

                ReverseRecipe(other, optional.Recipe);
                applied.Remove(optional);
            }
        }
    }

    /// <summary>
    /// Returns the recipe of <paramref name="context"/> when it is installed and declares an optional
    /// dependency on <paramref name="slug"/>, otherwise <see langword="null"/>.
    /// </summary>
    private EEditorPluginRecipe? GetInstalledOptionalRecipeOrNull(PluginContext context, string slug)
    {
        // installed either in an earlier session (Activated after a reload) or in this one (it has a
        // snapshot). Anything else was never installed and resolves its optionals on activation.
        if (context.State is not EEditorPluginState.Activated && context.AppliedOptionalDependencies is null)
        {
            return null;
        }

        if (!context.IsRecipeCreated)
        {
            context.Plugin!.CreateRecipe(context.Builder);
            context.IsRecipeCreated = true;
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

        foreach (var optional in ResolveOptionalRecipes(context, recipe))
        {
            applied.Add(optional);
            ApplyRecipe(context, optional.Recipe);

            if (context.State is EEditorPluginState.Error)
            {
                return;
            }
        }
    }

    private void ApplyRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        foreach (var nuget in recipe.Nugets)
        {
            if (!context.Cli!.AddNugetToProject(nuget.Name, nuget.Version, nuget.Source))
            {
                context.State = EEditorPluginState.Error;
                context.ErrorDetail =
                    new Exception($"Adding nuget {nuget.Name} {nuget.Version} to the project failed!");
                return;
            }

            if (nuget.Source is not null)
            {
                NugetConfigManager.RegisterSource(context.Slug, nuget.Source, context.Logger);
            }
        }

        foreach (var project in recipe.Projects)
        {
            context.Cli!.AddProjectToSolution(project.Path, project.FolderName);

            if (project.Reference)
            {
                context.Cli!.AddProjectReference(project.Path);
            }
        }

        foreach (var directory in recipe.Directories)
        {
            ShowHideHelper.ShowDirectory(context, directory);
        }

        foreach (var autoload in recipe.Autoloads)
        {
            context.PluginBase.AddAutoloadSingleton(autoload.Name, autoload.Path);
        }
    }

    public void DisableEPlugin(PluginContext context, bool refreshAtEnd = true)
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

        if (context.State == EEditorPluginState.Deactivated)
        {
            return;
        }

        if (!context.IsRecipeCreated)
        {
            context.Plugin.CreateRecipe(context.Builder);
            context.IsRecipeCreated = true;
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

            if (!plugin.IsRecipeCreated)
            {
                plugin.Plugin.CreateRecipe(plugin.Builder);
                plugin.IsRecipeCreated = true;
            }

            var isDependant = plugin.Builder.PluginRecipe.PluginDependencies.Any(d => d.Slug == context.Slug);
            if (!isDependant)
            {
                continue;
            }

            pluginsToDisable.Add(plugin);
        }

        if (pluginsToDisable.Any())
        {
            _toCheckDisable.Push(context);

            foreach (var plugin in pluginsToDisable)
            {
                if (plugin.State is not EEditorPluginState.Activated)
                {
                    continue;
                }

                if (EditorInterface.Singleton.IsPluginEnabled(plugin.Slug))
                {
                    _toCheckDisable.Push(plugin);
                    EditorInterface.Singleton.SetPluginEnabled(plugin.Slug, false);
                }
            }

            return;
        }

        UninstallEPlugin(context, context.Builder.PluginRecipe);

        // other plugins may have installed code that depends on this one via an optional dependency
        UninstallOptionalDependenciesOn(context);

        context.State = EEditorPluginState.Deactivated;

        // @ local dependencies: can not disable as we do not know which are needed. There is no way to track manual or
        //                       auto enabled plugins right now.

        if (refreshAtEnd)
        {
            // trigger uninstall for waiting plugins
            while (_toCheckDisable.Any())
            {
                var nextPlugin = _toCheckDisable.Pop();

                DisableEPlugin(nextPlugin, false);
            }

            RefreshEditor();
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

        context.AppliedOptionalDependencies = null;

        ReverseRecipe(context, recipe);
    }

    private void ReverseRecipe(PluginContext context, EEditorPluginRecipe recipe)
    {
        foreach (var autoload in recipe.Autoloads)
        {
            // hijack base plugin as actual plugin is already destroyed here.
            context.PluginBase.RemoveAutoloadSingleton(autoload.Name);
        }

        foreach (var directory in recipe.Directories)
        {
            ShowHideHelper.HideDirectory(context, directory);
        }

        foreach (var project in recipe.Projects)
        {
            context.Cli!.RemoveProjectReference(project.Path);
            context.Cli!.RemoveProjectFromSolution(project.Path);
        }

        foreach (var nuget in recipe.Nugets)
        {
            context.Cli!.RemoveNugetFromProject(nuget.Name);

            if (nuget.Source is not null)
            {
                NugetConfigManager.UnregisterSource(context.Slug, nuget.Source, context.Logger);
            }
        }
    }

    private void RefreshEditor()
    {
        _ePluginContext?.Logger.Log($"Refreshed Editor state.");

        // refresh what we can in Godot Editor UI.
        if (!EditorInterface.Singleton.GetResourceFilesystem().IsScanning())
        {
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }

        if (_ePluginContext is not null)
        {
            var baseContext = GetOrCreateContext(_ePluginContext);
            baseContext.Cli?.RebuildSolution();

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
        if (deactivatedPlugins.Any())
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

                _ePluginContext.Logger.Log($"  - plugin {pluginBase.GetPluginSlug()} ({pluginBase.GetName()})");
            }
        }

        _ePluginContext.Logger.Log(
            $"Found {_contexts.Count(p => p.State is EEditorPluginState.Activated)} active plugins.");

        ExecuteInitializers();
    }

    private void ExecuteInitializers()
    {
        if (!IsValid())
        {
            return;
        }

        while (_toInitialize.Count > 0)
        {
            var initializer = _toInitialize.Dequeue();
            initializer.Initialize(_ePluginContext);
        }
    }

    internal bool MatchesVersion(string givenVersion, string condition, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(givenVersion) || string.IsNullOrWhiteSpace(condition))
            return false;

        // clean to support semver postfixes by just ignoring them
        var index = givenVersion.IndexOf('-');
        if (index > 0)
        {
            givenVersion = givenVersion[..index];
        }

        if (!Version.TryParse(givenVersion, out var version))
        {
            logger?.Warn(
                $"Dependency version is in wrong format! (was: {givenVersion} needed: [major].[minor].[patch])");
            return false;
        }

        if (condition.AsSpan().StartsWith(">"))
        {
            var conditionVersionStr = condition.AsSpan()[1..];
            if (!Version.TryParse(conditionVersionStr, out var checkVersion))
            {
                logger?.Warn(
                    $"Dependency version is in wrong format! (was: {givenVersion} needed: >[major].[minor].[patch])");
                return false;
            }

            return version >= checkVersion;
        }
        else
        {
            if (!Version.TryParse(condition, out var checkVersion))
            {
                logger?.Warn(
                    $"Dependency version is in wrong format! (was: {givenVersion} needed: [major].[minor].[patch])");
                return false;
            }

            return version == checkVersion;
        }
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