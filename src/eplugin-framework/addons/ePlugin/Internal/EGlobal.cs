#if TOOLS
using System;
using System.Collections.Generic;
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

    private const int MaxFailedTries = 10;

    private readonly List<PluginContext> _contexts = [];
    private EPluginPlugin? _ePluginContext = null;

    private bool _hasWork = false;
    private int _lastChildNodeCount = 0;
    private DotnetCli? _cli = null;

    private ILoggerFactory? _loggerFactory = null;

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

    public PluginContext? GetContext(IEEditorPlugin plugin)
    {
        return _contexts.FirstOrDefault(c => c.Plugin == plugin);
    }

    public DotnetCli GetCli(EPluginPlugin plugin)
    {
        if (_cli == null)
        {
            _cli = new DotnetCli(plugin.Logger);
        }
        else
        {
            _cli.UseLogger(plugin.Logger);
        }

        return _cli;
    }

    public DotnetCli GetCli(IEEditorPlugin plugin)
    {
        var context = GetContext(plugin);
        if (_cli == null)
        {
            _cli = new DotnetCli(context?.Logger);
        }
        else
        {
            _cli.UseLogger(context?.Logger);
        }

        return _cli;
    }

    public void GlobalProcessor()
    {
        if (!IsValid())
        {
            return;
        }

        var childCount = EditorInterface.Singleton.GetBaseControl().GetParent().GetChildCount();
        if (_lastChildNodeCount != childCount)
        {
            // Check if something changed, not ideal but AssemblyReload kills event bindings to editor.
            ReloadContexts(_loggerFactory, true);
        }

        var _ = Step(_ePluginContext, _ePluginContext.Logger);
    }

    internal void EnsureEEditorPluginEnabled()
    {
        if (!EditorInterface.Singleton.IsPluginEnabled("ePlugin"))
        {
            EditorInterface.Singleton.SetPluginEnabled("ePlugin", true);
        }
    }

    internal void DeactivateAllEEditorPlugins()
    {
        foreach (var context in _contexts)
        {
            EditorInterface.Singleton.SetPluginEnabled(context.Slug, false);
        }

        _hasWork = true;
    }

    private bool Step(EPluginPlugin ePluginPlugin, ILogger logger)
    {
        if (!_hasWork)
        {
            return false;
        }

        var activePlugins = _contexts.Where(c => c.State == EEditorPluginState.Activated);
        var activePluginInfo = string.Join(',', activePlugins.Select(ap => $"{ap.Slug} ({ap.Name})"));
        if (string.IsNullOrWhiteSpace(activePluginInfo))
        {
            _ePluginContext?.Logger.Log($"Processing plugins");
        }
        else
        {
            _ePluginContext?.Logger.Log($"Processing plugins (already finished: {activePluginInfo})");
        }


        // mark already active plugins properly to not bootstrap again (sadly IsPluginEnabled does not report state as expected)
        var alreadyActivePlugins = _contexts.Where(context =>
            context.State == EEditorPluginState.Created && !context.IsFirstActivation);
        foreach (var context in alreadyActivePlugins)
        {
            context.State = EEditorPluginState.Activated;
        }

        //push plugins a step further
        var created = _contexts.Where(context => context.State == EEditorPluginState.Created);
        _hasWork = ToBootstrapped(created, logger);

        var bootstrapped = _contexts.Where(context => context.State == EEditorPluginState.Bootstrapped);
        _hasWork |= ToActivated(bootstrapped, logger);

        var deactivationRequested = _contexts.Where(c => c.State == EEditorPluginState.DeactivationRequested);
        ToDeactivated(deactivationRequested, ePluginPlugin, logger);

        if (!_hasWork)
        {
            _ePluginContext?.Logger.Log($"Refreshed Editor state.");

            if (!EditorInterface.Singleton.GetResourceFilesystem().IsScanning())
            {
                EditorInterface.Singleton.GetResourceFilesystem().Scan();
            }

            _cli?.UseLogger(logger);
            _cli?.Call.RebuildSolution();

            _ePluginContext?.Logger.Log(
                $"Completed loading. {_contexts.Count(c => c.State == EEditorPluginState.Activated)} active plugins.");
        }

        return _hasWork;
    }

    private bool ToBootstrapped(IEnumerable<PluginContext> contexts, ILogger logger)
    {
        var needsWork = false;
        foreach (var context in contexts)
        {
            if (context.Plugin is null)
            {
                continue;
            }

            try
            {
                context.Plugin.Bootstrap(context.Builder);
                context.State = EEditorPluginState.Bootstrapped;
                needsWork = true;
            }
            catch (Exception ex)
            {
                EditorInterface.Singleton.SetPluginEnabled(context.Slug, false);
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = ex;
                context.Logger?.Error($"{ex}");
            }
        }

        return needsWork;
    }

    private bool ToActivated(IEnumerable<PluginContext> contexts, ILogger logger)
    {
        var needsWork = false;
        foreach (var context in contexts)
        {
            if (context.FailedTries >= MaxFailedTries)
            {
                logger.Error($"Failed to activate plugin {context.Slug} ({context.Name})");
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = new Exception($"Failed to load after at least {MaxFailedTries - 1} retries.");
                EditorInterface.Singleton.SetPluginEnabled(context.Slug, false);
                continue;
            }

            try
            {
                if (!Install(context))
                {
                    context.State = EEditorPluginState.Activated;
                }
                else
                {
                    needsWork = true;
                }
            }
            catch (Exception ex)
            {
                EditorInterface.Singleton.SetPluginEnabled(context.Slug, false);
                context.State = EEditorPluginState.Error;
                logger.Error($"{ex}");
            }
        }

        return needsWork;
    }

    private void ToDeactivated(IEnumerable<PluginContext> contexts, EPluginPlugin ePluginPlugin, ILogger logger)
    {
        foreach (var context in contexts)
        {
            try
            {
                Uninstall(context, ePluginPlugin);
                context.State = EEditorPluginState.Deactivated;
            }
            catch (Exception ex)
            {
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = ex;
                logger.Error($"{ex}");
            }
            finally
            {
                context.Plugin = default;
            }
        }
    }

    private bool Install(PluginContext context)
    {
        if (context.Plugin is null)
        {
            return false;
        }

        var needsWork = false;
        var recipe = context.Builder.PluginRecipe;

        foreach (var dependency in recipe.PluginDependencies)
        {
            if (!EditorInterface.Singleton.IsPluginEnabled(dependency.Slug))
            {
                EditorInterface.Singleton.SetPluginEnabled(dependency.Slug, true);
                needsWork = true;
                continue;
            }

            if (dependency.Version is not null)
            {
                var dependencyContext = _contexts.FirstOrDefault(c => c.Slug == dependency.Slug);
                if (dependencyContext is null)
                {
                    context.Logger?.Warn($"Plugin {dependency.Slug} not found!");
                    needsWork = true;
                    continue;
                }

                var dependencyVersion = dependencyContext.Version;
                if (MatchesVersion(dependencyVersion, dependency.Version, context.Logger))
                {
                    if (context.State != EEditorPluginState.Activated)
                    {
                        needsWork = true;
                        continue;
                    }
                }
                else
                {
                    context.Logger.Warn(
                        $"Dependency {dependency.Slug} {dependencyVersion} does not match needed {dependency.Version}.");
                    needsWork = true;
                    context.FailedTries = uint.MaxValue;
                    continue;
                }
            }

            context.Logger?.Log($"Dependency {dependency.Slug} {dependency.Version} ready.");
        }

        if (needsWork)
        {
            // some dependency is not ready, retry later
            if (context.FailedTries < int.MaxValue)
            {
                context.FailedTries++;
            }

            return needsWork;
        }

        foreach (var nuget in recipe.Nugets)
        {
            if (!context.Plugin.AddNuget(nuget.Name, nuget.Version, nuget.Source))
            {
                context.FailedTries = uint.MaxValue;
                return true;
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

        return false;
    }

    private void Uninstall(PluginContext context, EPluginPlugin ePluginPlugin)
    {
        if (context.Plugin is null)
        {
            return;
        }

        if (!context.IsFirstActivation)
        {
            // plugin was already active at start of Godot. Need to bootstrap first to fill recipe.
            try
            {
                context.Plugin.Bootstrap(context.Builder);
            }
            catch (Exception ex)
            {
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = ex;
                context.Logger?.Error($"{ex}");
            }
        }

        var recipe = context.Builder.PluginRecipe;

        foreach (var autoload in recipe.Autoloads)
        {
            // hijack base plugin as actual plugin is already destroyed here.
            ePluginPlugin.RemoveAutoloadSingleton(autoload.Name);
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

        _lastChildNodeCount = children.Count;

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

        var removedPlugins = _contexts.Where(c => children.All(x => x != c.Plugin)).ToArray();
        if (removedPlugins.Any())
        {
            foreach (var pluginContext in removedPlugins)
            {
                pluginContext.State = EEditorPluginState.DeactivationRequested;
            }

            _hasWork = true;
        }


        _ePluginContext.Logger.Log($"Found {_contexts.Count} active ePlugin(s).");

        // initialize plugins
        foreach (var context in _contexts)
        {
            if (context.Plugin is null)
            {
                continue;
            }

            context.Plugin.Reinitialize();
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
                    IsFirstActivation = true,
                };
                _hasWork = true;
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