#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Enabling an ePlugin: its license gate, its hard dependencies, which are enabled and checked first, and its recipe.
/// Plugins enabled before the framework was initialized, or waiting for a dependency, are resumed in rounds.
/// </summary>
internal sealed partial class EGlobal
{
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
                // Joining, the new instance took this one's place in the transition. One that stopped before, e.g. at the
                // license gate, did not: the activation did not happen, and waiting for this context would never end.
                if (_transition?.Participants.ContainsKey(context) == true)
                {
                    context.Logger?.Error($"{context.Slug} was enabled again, but did not continue its activation.");
                    FailTransition("retry_not_continued");
                }

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
                    // Godot runs the dependency's _EnablePlugin inside the toggle, which resumes this plugin once it is
                    // activated. One that never called EnableEPlugin (e.g. it threw first) neither activated nor waits.
                    var started = _contexts.LastOrDefault(c => c.Slug == slug && c.Plugin is not null);
                    if (!_toCheckEnable.Contains(context) || started is not null &&
                        (started.State == EEditorPluginState.Activated || _toCheckEnable.Contains(started)))
                    {
                        return DependencyReadiness.Wait; // EnableEPlugin will be called by the newly enabled plugin
                    }

                    var silent = $"Plugin {slug} was enabled but did not start its activation; its _EnablePlugin has to call this.EnableEPlugin().";
                    context.Logger?.Warn(silent);
                    FailAllUncheckedPluginsAndRefresh($"Plugin dependency {slug} did not activate!");
                    return DependencyReadiness.Failed;
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

        var managed = ManagedContext(slug);
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
            var others = recipe.Autoloads.Count > 0 ? RemainingRecipes(context, recipe) : [];
            foreach (var autoload in recipe.Autoloads) TrackRecipeOperation(context, new(RecipeOperationKind.AddAutoload, Autoload: autoload), () => ApplyAutoload(context, autoload, others));
        }
        catch (Exception ex)
        {
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = ex;
            context.Logger?.Error($"Applying recipe for {context.Slug} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the plugin's recipe on its context, once. When <see cref="IEEditorPlugin.CreateRecipe"/> throws, the plugin
    /// is in error with the exception as its detail, and its recipe stays uncreated and empty for a retry.
    /// </summary>
    /// <param name="scan">
    /// The recipe is only read while another plugin changes: a failure is logged, but leaves the plugin as it is, since
    /// no attempt of its own would let a retry or disable resolve its error.
    /// </param>
    private static bool TryEnsureRecipe(PluginContext context, bool scan = false)
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
            if (scan)
            {
                context.Logger?.Warn($"Cannot read the recipe of {context.Slug}: {ex.Message}");
                return false;
            }

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
}
#endif
