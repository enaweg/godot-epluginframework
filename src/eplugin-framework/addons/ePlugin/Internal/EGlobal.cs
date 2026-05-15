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

    private const int MAX_FAILED_TRIES = 5;

    private readonly List<PluginContext> _contexts = [];
    private EPluginPlugin? _ePluginContext = null;

    private DotnetCli? _cli = null;

    private ILoggerFactory? _loggerFactory = null;

    private Stack<PluginContext> _toCheck = new();
    private Stack<PluginContext> _toDisable = new();


    private EGlobal()
    {
    }

    public void Initialize(EPluginPlugin plugin, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _ePluginContext = plugin;
        plugin.Logger = _loggerFactory.CreateLogger(_ePluginContext.GetType().FullName ?? "UNKNOWN");

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

    public PluginContext GetOrCreateContext(IEEditorPlugin plugin)
    {
        var context = _contexts.FirstOrDefault(c => c.Plugin == plugin);

        if (context is null)
        {
            var pluginLogger = _loggerFactory?.CreateLogger(plugin.GetType().FullName ?? "UNKNOWN");
            context = new PluginContext(plugin, pluginLogger);
            _contexts.Add(context);
        }

        return context;
    }

    public DotnetCli GetCli(EPluginPlugin plugin)
    {
        if (_cli == null)
        {
            _cli = new DotnetCli(plugin, plugin.Logger);
        }
        else
        {
            _cli.UseLogger(plugin.Logger);
        }

        return _cli;
    }

    public DotnetCli GetCli(IEEditorPlugin plugin)
    {
        var context = GetOrCreateContext(plugin);
        if (_cli == null)
        {
            _cli = new DotnetCli(_ePluginContext, context?.Logger);
        }
        else
        {
            _cli.UseLogger(context?.Logger);
        }

        return _cli;
    }

    internal void EnsureEEditorPluginEnabled()
    {
        if (!EditorInterface.Singleton.IsPluginEnabled("ePlugin"))
        {
            EditorInterface.Singleton.SetPluginEnabled("ePlugin", true);
        }
    }

    public void EnableEPlugin(PluginContext context, bool refreshAtEnd = true)
    {
        EnsureEEditorPluginEnabled();

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
            EditorInterface.Singleton.SetPluginEnabled(context.Slug, false);
            _toCheck.Push(context);
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
                _toCheck.Push(context);
                EditorInterface.Singleton.SetPluginEnabled(dependency.Slug, true);
                return; // EnableEPlugin will be called by newly enabled plugin, stop here
            }

            if (dependency.Version is not null)
            {
                var dependencyContext = _contexts.FirstOrDefault(c => c.Slug == dependency.Slug);
                if (dependencyContext is null)
                {
                    context.Logger?.Warn($"Plugin {dependency.Slug} not found!");
                    _toCheck.Push(context);
                    FailAllUncheckedPluginsAndRefresh($"Plugin dependency {dependency.Slug} not found!");
                    return;
                }

                var dependencyVersion = dependencyContext.Version;
                if (MatchesVersion(dependencyVersion, dependency.Version, context.Logger))
                {
                    if (context.State is EEditorPluginState.Deactivated or EEditorPluginState.Error)
                    {
                        context.Logger?.Warn(
                            $"Plugin dependency {dependency.Slug} not ready but needed by {context.Slug}!");
                        _toCheck.Push(context);
                        FailAllUncheckedPluginsAndRefresh(
                            $"Plugin dependency {dependency.Slug} not ready but needed by {context.Slug}!");
                        return;
                    }
                }
                else
                {
                    context.Logger.Warn(
                        $"Dependency {dependency.Slug} {dependencyVersion} does not match needed {dependency.Version} of {context.Slug}!");

                    _toCheck.Push(context);
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
            while (_toCheck.Any())
            {
                var nextPlugin = _toCheck.Pop();

                EnableEPlugin(nextPlugin, false);
            }

            RefreshEditor();
        }
    }

    private void FailAllUncheckedPluginsAndRefresh(string reason)
    {
        foreach (var plugin in _toCheck)
        {
            plugin.State = EEditorPluginState.Error;
            plugin.ErrorDetail = new Exception(reason);
            EditorInterface.Singleton.SetPluginEnabled(plugin.Slug, false);
        }

        _toCheck.Clear();

        RefreshEditor();
    }

    private void InstallEPlugin(PluginContext context, EEditorPluginRecipe recipe)
    {
        foreach (var nuget in recipe.Nugets)
        {
            if (!context.Plugin.AddNuget(nuget.Name, nuget.Version, nuget.Source))
            {
                context.FailedTries = uint.MaxValue;
                return;
            }
        }

        foreach (var project in recipe.Projects)
        {
            context.Plugin.AddProject(project.Path, project.FolderName, project.Reference);
        }

        foreach (var directory in recipe.Directories)
        {
            ShowHideHelper.ShowDirectory(context, directory);
        }

        foreach (var autoload in recipe.Autoloads)
        {
            context.Plugin.GodotPlugin.AddAutoloadSingleton(autoload.Name, autoload.Path);
        }
    }

    public void DisableEPlugin(PluginContext context, bool refreshAtEnd = true)
    {
        if (context.Plugin is null)
        {
            return;
        }

        if (_toCheck.Contains(context))
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
            _toDisable.Push(context);

            foreach (var plugin in pluginsToDisable)
            {
                if (EditorInterface.Singleton.IsPluginEnabled(plugin.Slug))
                {
                    _toDisable.Push(plugin);
                    EditorInterface.Singleton.SetPluginEnabled(plugin.Slug, false);
                }
            }

            return;
        }

        UninstallEPlugin(context, context.Builder.PluginRecipe);

        // @ local dependencies: can not disable as we do not know which are needed. There is no way to track manual or
        //                       auto enabled plugins right now.

        if (refreshAtEnd)
        {
            // trigger uninstall for waiting plugins
            while (_toDisable.Any())
            {
                var nextPlugin = _toDisable.Pop();

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
            context.Plugin.GodotPlugin.RemoveAutoloadSingleton(autoload.Name);
        }

        foreach (var directory in recipe.Directories)
        {
            ShowHideHelper.HideDirectory(context, directory);
        }

        foreach (var project in recipe.Projects)
        {
            context.Plugin.RemoveProject(project.Path);
        }

        foreach (var nuget in recipe.Nugets)
        {
            context.Plugin.RemoveNuget(nuget.Name);
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

        _cli?.UseLogger(_ePluginContext?.Logger);
        _cli?.Call.RebuildSolution();

        _ePluginContext?.Logger.Log(
            $"Completed loading. {_contexts.Count(c => c.State == EEditorPluginState.Activated)} active plugins.");
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

        // ensure logger is set with current loggerFactory
        if (loggerFactory is not null)
        {
            foreach (var context in _contexts)
            {
                if (context.Logger is not null)
                {
                    continue;
                }

                context.Logger = _loggerFactory?.CreateLogger(context.GetType().FullName ?? "UNKNOWN");
            }
        }

        // init cli (so it does not happen at random)
        var _ = GetCli(_ePluginContext).IsDotnetAvailable;

        // handle other plugins
        _ePluginContext.Logger.Log($"Refreshing ePlugins");

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
            if (childNode is null)
            {
                continue;
            }

            if (childNode is IEEditorPlugin editorPlugin)
            {
                AddOrUpdatePluginContext(editorPlugin, changeTriggered);
                _ePluginContext.Logger.Log(
                    $"  - ePlugin {editorPlugin.GodotPlugin.GetPluginSlug()} ({editorPlugin.GodotPlugin.GetName()})");
            }
        }

        _ePluginContext.Logger.Log($"Found {_contexts.Count} active ePlugin(s).");

        // initialize plugins
        var initializeTypes = Assembly.GetExecutingAssembly().GetExportedTypes().Except([typeof(IInitialize)])
            .Where(t => t.IsAssignableTo(typeof(IInitialize))).ToArray();

        if (_ePluginContext.EnableDebugLogging && initializeTypes.Any())
        {
            _ePluginContext.Logger.Log($"Calling Initializers:");
        }

        foreach (var initializeType in initializeTypes)
        {
            try
            {
                var instance = Activator.CreateInstance(initializeType);
                if (instance is IInitialize initializeInstance)
                {
                    if (_ePluginContext.EnableDebugLogging)
                    {
                        _ePluginContext.Logger.Log($" - Calling {initializeType.FullName}");
                    }

                    initializeInstance.Initialize(_ePluginContext);
                }
            }
            catch (Exception ex)
            {
                _ePluginContext.Logger.Error($"Error initializing {initializeType.FullName}: {ex}");
            }
        }
    }

    private void AddOrUpdatePluginContext(IEEditorPlugin editorPlugin, bool changeTriggered)
    {
        var context = _contexts.FirstOrDefault(c => c.Plugin == editorPlugin);
        if (context is null)
        {
            var pluginLogger = _loggerFactory?.CreateLogger(editorPlugin.GetType().FullName ?? "UNKNOWN");
            if (changeTriggered)
            {
                // was a change to plugins, so this is a new plugin need to bootstrap.
                context = new PluginContext(editorPlugin, pluginLogger)
                {
                    State = EEditorPluginState.Created,
                };
            }
            else
            {
                // initial start or assembly reload, nothing need to be done as installation already happened
                context = new PluginContext(editorPlugin, pluginLogger)
                {
                    State = EEditorPluginState.Activated
                };
            }

            _contexts.Add(context);
        }
    }

    private bool MatchesVersion(string givenVersion, string condition, ILogger logger)
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
            logger.Warn(
                $"Dependency version is in wrong format! (was: {givenVersion} needed: [major].[minor].[patch])");
            return false;
        }

        if (condition.AsSpan().StartsWith(">"))
        {
            var conditionVersionStr = condition.AsSpan()[1..];
            if (!Version.TryParse(conditionVersionStr, out var checkVersion))
            {
                logger.Warn(
                    $"Dependency version is in wrong format! (was: {givenVersion} needed: >[major].[minor].[patch])");
                return false;
            }

            return version >= checkVersion;
        }
        else
        {
            if (!Version.TryParse(condition, out var checkVersion))
            {
                logger.Warn(
                    $"Dependency version is in wrong format! (was: {givenVersion} needed: [major].[minor].[patch])");
                return false;
            }

            return version == checkVersion;
        }
    }
}
#endif