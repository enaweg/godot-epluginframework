#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Manager;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private const int ManagerStatusCheckFrames = 30;
    private const int AddonProblemCheckFrames = 600;
    private FrameThrottle _managerStatusChecks = new(ManagerStatusCheckFrames);
    private string? _managerStatusKey;
    private ManagerStatus _managerStatus;
    private FrameThrottle _addonProblemChecks = new(AddonProblemCheckFrames);
    private string _addonProblems = "";

    /// <summary>
    /// The <see cref="AttentionItem.Key"/>s of the problems the Needs attention dialog showed in this editor session; the
    /// ePlugin Manager opens it by itself only for others. An assembly reload forgets them.
    /// </summary>
    internal HashSet<string> ShownAttention { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What the toolbar button of the ePlugin Manager shows, as the manager would list it. Called every frame; looks again
    /// every <see cref="ManagerStatusCheckFrames"/> frames, and reads the plugins only when the pending updates, the
    /// enabled plugins, their states or the <see cref="AddonProblems"/> changed. While plugins are being changed it
    /// keeps the last status.
    /// </summary>
    internal ManagerStatus CurrentManagerStatus()
    {
        var frame = Engine.GetProcessFrames();
        // a check skipped while plugins change is not counted, so the next frame checks again
        if (IsChangingPlugins || _recipeUpdateJournal is not null || !_managerStatusChecks.Due(frame)) return _managerStatus;
        var pending = PendingUpdates;
        var attempts = _stateStore?.LocalAttempts ?? [];
        var states = _contexts.Where(c => c.Plugin is not null).Select(c => $"{c.Slug}:{c.State}").ToArray();
        var problems = AddonProblems(frame);
        // Nothing can need attention or be updated: no need to read the plugins.
        if (pending.Count == 0 && attempts.Count == 0 && problems.Length == 0 && !_contexts.Any(c => c.Plugin is not null && c.State == EEditorPluginState.Error))
        {
            _managerStatusKey = null;
            return _managerStatus = ManagerStatus.Normal;
        }
        var key = string.Join("\n", pending.Select(c => $"{c.Slug} {c.NewVersion} {c.SourceUrl}")) + "\n#" +
                  string.Join(",", attempts.Select(a => a.AttemptId)) + "\n#" + string.Join(",", states) + "\n#" +
                  string.Join(",", GetEnabledPluginSlugs().Order(StringComparer.Ordinal)) + "\n#" + problems;
        if (key == _managerStatusKey) return _managerStatus;
        _managerStatusKey = key;
        try
        {
            var model = new PluginManagerViewModel(CollectPlugins(), pending, CollectUpdateTargets(), UpdateCache, c => UpdatePreflightFindings([c]));
            return _managerStatus = model.Status;
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Warn($"Cannot tell whether plugins need attention or can be updated: {ex.Message}");
            return _managerStatus = ManagerStatus.Normal;
        }
    }

    /// <summary>Enables or disables a plugin in the editor, as the ePlugin Manager's list does; see <see cref="IEditorHost.SetPluginEnabled"/>.</summary>
    internal void SetPluginEnabled(string slug, bool enabled) => _host.SetPluginEnabled(slug, enabled);

    /// <summary>Reads the plugins again for the next <see cref="CurrentManagerStatus"/>, e.g. after the ePlugin Manager changed update sites.</summary>
    internal void InvalidateManagerStatus() { _managerStatusKey = null; _managerStatusChecks.Reset(); _addonProblemChecks.Reset(); }

    /// <summary>
    /// What makes a plugin need attention without any state of the framework telling: a <c>plugin.cfg</c> Godot cannot
    /// read, and the <c>.gdextension</c> files below the addons folder that Godot does not run although it should (see
    /// <see cref="NativeExtensionStatus.Problem"/>), joined into one key; empty when there is none. Reading them means
    /// walking every addon folder, so they are read again only every <see cref="AddonProblemCheckFrames"/> frames.
    /// </summary>
    private string AddonProblems(ulong frame)
    {
        if (!_addonProblemChecks.Due(frame)) return _addonProblems;
        try
        {
            var layout = ScanAddons();
            var configs = layout.Plugins.Where(slug => !CanReadPluginConfig(slug)).Select(slug => $"{slug}/plugin.cfg");
            var extensions = layout.Plugins.Concat(layout.ExtensionAddons)
                .SelectMany(slug => NativeExtensionStatus.Read(_host.ProjectRoot, slug, _host.IsExtensionLoaded)
                    .Where(e => e.Problem is not null).Select(e => $"{slug}/{e.File}"));
            return _addonProblems = string.Join(",", configs.Concat(extensions));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return _addonProblems = "";
        }
    }

    /// <summary>Whether Godot reads the plugin.cfg of <paramref name="slug"/>; the manager lists one it cannot read as needing attention.</summary>
    private bool CanReadPluginConfig(string slug)
    {
        var file = _host.PluginDirectory(slug) + "/plugin.cfg";
        if (!FileAccess.FileExists(file)) return true;
        using var config = new ConfigFile();
        return config.Load(file) == Error.Ok;
    }

    /// <summary>The plugins and GDExtension add-ons below the addons folder, see <see cref="AddonLayout"/>.</summary>
    private AddonLayout.Layout ScanAddons() => AddonLayout.Scan(AddonsPath);

    /// <summary>The addons folder as a file system path.</summary>
    private string AddonsPath => ProjectSettings.GlobalizePath(_host.AddonsDirectory);

    /// <summary>
    /// Every plugin Godot's Plugins list shows (a folder with a plugin.cfg below the addons folder, also in a subfolder,
    /// see <see cref="AddonLayout"/>), enabled or not, every enabled sub-plugin, which its plugin enabled, every
    /// GDExtension add-on, plus plugins that only remain as a failed local attempt.
    /// </summary>
    internal IReadOnlyList<PluginInfo> CollectPlugins()
    {
        RefreshPlainPlugins();
        ReloadUpdateSites();
        var enabled = GetEnabledPluginSlugs();
        var types = PluginTypes.All;
        var framework = FrameworkSlug;
        var plugins = new List<PluginInfo>();
        var layout = ScanAddons();
        // Godot does not list a sub-plugin, but its plugin may enable it; its state is then shown too.
        var subPlugins = enabled.Where(slug => !layout.Plugins.Contains(slug, StringComparer.Ordinal)).Order(StringComparer.Ordinal);
        foreach (var slug in layout.Plugins.Concat(subPlugins))
        {
            var directory = _host.PluginDirectory(slug);
            if (!FileAccess.FileExists(directory + "/plugin.cfg")) continue;
            using var config = new ConfigFile();
            if (config.Load(directory + "/plugin.cfg") is var loaded and not Error.Ok)
            {
                // listed, so the user sees why a plugin is missing from the list instead of wondering
                plugins.Add(new PluginInfo(slug, slug, PluginKind.Unknown, enabled.Contains(slug))
                {
                    ConfigProblem = $"Its plugin.cfg cannot be read (Godot reports {loaded}).", FailedAttempt = _stateStore?.GetLocal(slug),
                    PartOf = AddonLayout.ContainingPlugin(AddonsPath, slug)
                });
                continue;
            }
            string Value(string key) => config.GetValue("plugin", key, "").AsString().Trim();
            var script = PluginCatalog.ResolveScript(directory, Value("script"));
            var context = ManagedContext(slug);
            var name = Value("name");
            plugins.Add(new PluginInfo(slug, name.Length > 0 ? name : slug,
                PluginKinds.Classify(script, slug == framework, types, PluginKinds.HasNativeExtension(ProjectSettings.GlobalizePath(directory))), enabled.Contains(slug))
            {
                Version = Value("version"), Author = Value("author"), Description = Value("description"),
                UpdateUrl = Value("update_url") is { Length: > 0 } url ? url : null, UpdateSite = UpdateSiteOf(slug), Known = KnownPluginOf(slug), Script = script,
                DocumentationUrl = Value("documentation_url") is { Length: > 0 } documentation ? documentation : null,
                SourceUrl = Value("source_url") is { Length: > 0 } source ? source : null,
                State = context?.State, Error = context?.ErrorDetail?.Message,
                StateProblem = context?.State == EEditorPluginState.Error ? _stateStore?.LoadProblem : null,
                FailedAttempt = _stateStore?.GetLocal(slug),
                Recipe = context is { IsRecipeCreated: true } ? context.Builder.PluginRecipe : null,
                LocalPackages = LocalIndex.Matching(slug).Count(),
                Extensions = NativeExtensionStatus.Read(_host.ProjectRoot, slug, _host.IsExtensionLoaded),
                Repository = PluginRepositoryOf(slug),
                PartOf = AddonLayout.ContainingPlugin(AddonsPath, slug)
            });
        }
        plugins.AddRange(layout.ExtensionAddons.Select(ExtensionAddonInfo));
        foreach (var attempt in _stateStore?.LocalAttempts ?? [])
        {
            if (plugins.Any(p => p.Slug == attempt.Slug)) continue;
            plugins.Add(new PluginInfo(attempt.Slug, attempt.Slug, PluginKind.Unknown, false)
            { Version = attempt.InstalledVersion ?? "", FailedAttempt = attempt, Missing = true });
        }
        return plugins;
    }
}
#endif
