#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// This is a internally used class by Enaweg.Plugin and should not be used by anything else. It manages plugin states
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

    private bool _eventsRegistered = false;
    private bool _hasWork = false;
    private bool _shouldProcess = false;

    private DotnetCli? _cli = null;

    private EGlobal()
    {
    }

    public DotnetCli GetCli(EEditorPluginBase plugin)
    {
        if (_cli == null)
        {
            _cli = new DotnetCli(plugin.Logger);
        }

        return _cli;
    }

    internal void StartProcessing(EPluginPlugin ePlugin)
    {
        if (_ePluginContext is not null)
        {
            // already running
            return;
        }

        _ePluginContext = ePlugin;
        StartProcessing();
    }

    /// <summary>
    /// Free all references as these will prevent proper reload of assembly after changes.
    /// </summary>
    /// <param name="context"></param>
    private void StopProcessingInternal(AssemblyLoadContext context)
    {
        var logger = _ePluginContext?.Logger;

        _hasWork = false;
        _shouldProcess = false;
        _ePluginContext = null;
        _contexts.Clear();
        if (_eventsRegistered)
        {
            _eventsRegistered = false;

            // this is auto-disconnected on assembly reload
            // EditorInterface.Singleton.GetEditorMainScreen().GetTree().ProcessFrame -= GlobalProcessor;
            
            AssemblyLoadContext.GetLoadContext(GetType().Assembly).Unloading -= StopProcessingInternal;
        }

        logger?.Log("Unloading done (Assembly reload)");
    }

    private void StartProcessing()
    {
        if (!_eventsRegistered)
        {
            AssemblyLoadContext.GetLoadContext(GetType().Assembly).Unloading += StopProcessingInternal;
            EditorInterface.Singleton.GetEditorMainScreen().GetTree().ProcessFrame += GlobalProcessor;
            _eventsRegistered = true;
        }

        _shouldProcess = true;
    }

    internal void StopProcessing()
    {
        _shouldProcess = false;
    }

    private void GlobalProcessor()
    {
        if (_ePluginContext is null)
        {
            return;
        }

        var workToDo = Step(_ePluginContext, _ePluginContext.Logger);

        if (!_shouldProcess && !workToDo)
        {
            StopProcessingInternal(null);
        }
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

    internal void Register(EEditorPlugin plugin)
    {
        if (_contexts.Count <= 0)
        {
            ReloadContexts(plugin.Logger);
        }

        _contexts.RemoveAll(c => c.Slug == plugin.PluginSlug);
        _contexts.Add(new PluginContext(plugin));
        _hasWork = true;
        StartProcessing();
    }

    internal void Activate(EEditorPlugin plugin)
    {
        if (_contexts.Count <= 0)
        {
            ReloadContexts(plugin.Logger);
        }

        var context = _contexts.Find(c => c.Plugin == plugin);
        if (context is not null)
        {
            context.IsFirstActivation = true;
            context.State = EEditorPluginState.Created;
        }

        StartProcessing();
    }

    internal void Deactivate(EEditorPlugin plugin)
    {
        if (_contexts.Count <= 0)
        {
            ReloadContexts(plugin.Logger);
        }

        var context = _contexts.Find(c => c.Plugin == plugin);
        if (context != null)
        {
            context.State = EEditorPluginState.DeactivationRequested;
            _hasWork = true;
        }

        StartProcessing();
    }

    private bool Step(EEditorPluginBase ePluginPlugin, ILogger logger)
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
                context.Logger.Error($"{ex}");
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

    private void ToDeactivated(IEnumerable<PluginContext> contexts, EEditorPluginBase ePluginPlugin, ILogger logger)
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
            if (!EditorInterface.Singleton.IsPluginEnabled(dependency.slug))
            {
                EditorInterface.Singleton.SetPluginEnabled(dependency.slug, true);
                needsWork = true;
                continue;
            }

            if (dependency.version is not null)
            {
                var dependencyContext = _contexts.FirstOrDefault(c => c.Slug == dependency.slug);
                if (dependencyContext is null)
                {
                    context.Logger.Warn($"Plugin {dependency.slug} not found!");
                    needsWork = true;
                    continue;
                }

                var dependencyVersion = dependencyContext.Version;
                if (MatchesVersion(dependencyVersion, dependency.version, context.Logger))
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
                        $"Dependency {dependency.slug} {dependencyVersion} does not match needed {dependency.version}.");
                    needsWork = true;
                    context.FailedTries = uint.MaxValue;
                    continue;
                }
            }

            context.Logger.Log($"Dependency {dependency.slug} {dependency.version} ready.");
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
            if (!context.Plugin.AddNuget(nuget.name, nuget.version, nuget.source))
            {
                context.FailedTries = uint.MaxValue;
                return true;
            }
        }

        foreach (var project in recipe.Projects)
        {
            context.Plugin.AddProject(project.path, project.folderName, project.reference);
        }

        foreach (var directory in recipe.Directories)
        {
            ShowHideHelper.ShowDirectory(context.Plugin, directory, context.Logger);
        }

        foreach (var autoload in recipe.Autoloads)
        {
            context.Plugin.AddAutoloadSingleton(autoload.name, autoload.path);
        }

        return false;
    }

    private void Uninstall(PluginContext context, EEditorPluginBase ePluginPlugin)
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
                context.Logger.Error($"{ex}");
            }
        }

        var recipe = context.Builder.PluginRecipe;

        foreach (var autoload in recipe.Autoloads)
        {
            // hijack base plugin as actual plugin is already destroyed here.
            ePluginPlugin.RemoveAutoloadSingleton(autoload.name);
        }

        foreach (var directory in recipe.Directories)
        {
            ShowHideHelper.HideDirectory(context.Plugin, directory, context.Logger);
        }

        foreach (var project in recipe.Projects)
        {
            context.Plugin.RemoveProject(project.path);
        }

        foreach (var nuget in recipe.Nugets)
        {
            context.Plugin.RemoveNuget(nuget.name);
        }
    }

    /// <summary>
    /// If assembly reload is triggered based on code changes the contexts are lost. Need to rebuild it from active plugins.
    /// </summary>
    private void ReloadContexts(ILogger logger)
    {
        _contexts.Clear();

        var parentNode = EditorInterface.Singleton.GetBaseControl().GetParent();
        var children = parentNode.GetChildren();

        // get ePlugin base
        var ePluginNode = children.FirstOrDefault(c => c is EPluginPlugin);
        if (ePluginNode is null)
        {
            logger.Error($"ePlugin base node not found!");
            return;
        }

        _ePluginContext = (EPluginPlugin)ePluginNode;

        // init cli (so it does not happen at random)
        var _ = GetCli(_ePluginContext).IsDotnetAvailable;

        // handle other plugins
        _ePluginContext.Logger.Log($"Refreshing ePlugins");
        foreach (var childNode in children)
        {
            if (childNode is null)
            {
                continue;
            }

            if (childNode is EEditorPlugin editorPlugin)
            {
                var context = new PluginContext(editorPlugin);
                context.State = EEditorPluginState.Activated;
                _contexts.Add(context);
                _ePluginContext.Logger.Log(
                    $"  - ePlugin {editorPlugin.PluginSlug} ({editorPlugin.GetName()})");
            }
        }

        _ePluginContext.Logger.Log($"Found {_contexts.Count} active ePlugin(s).");
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