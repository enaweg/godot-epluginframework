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

    private void InstallEPlugin(PluginContext context, EEditorPluginRecipe recipe)
    {
        foreach (var nuget in recipe.Nugets)
        {
            if (!context.Cli!.AddNugetToProject(nuget.Name, nuget.Version, nuget.Source))
            {
                context.FailedTries = uint.MaxValue;
                return;
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
        // initialize plugins using reflection as these types are only available after install added them (future: source generators)
        var initializersTypes = Assembly.GetExecutingAssembly().GetExportedTypes().Except([typeof(IInitialize)])
            .Where(t => t.IsAssignableTo(typeof(IInitialize))).ToArray();
        if (_ePluginContext.EnableDebugLogging && initializersTypes.Any())
        {
            _ePluginContext.Logger.Log($"Calling Initializers:");
        }

        foreach (var initializerType in initializersTypes)
        {
            try
            {
                if (Activator.CreateInstance(initializerType) is not IInitialize initializer)
                {
                    continue;
                }

                if (_ePluginContext.EnableDebugLogging)
                {
                    _ePluginContext.Logger.Log($" - Initializing {initializerType.FullName}");
                }

                initializer.Initialize(_ePluginContext);
            }
            catch (Exception ex)
            {
                _ePluginContext.Logger.Error($"Error initializing {initializerType.FullName}: {ex}");
            }
        }
    }

    private bool MatchesVersion(string givenVersion, string condition, ILogger? logger)
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
}
#endif