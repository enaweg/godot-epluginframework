#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Manager;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private const int ManagerStatusCheckFrames = 30;
    private ulong _nextManagerStatusCheck;
    private string? _managerStatusKey;
    private ManagerStatus _managerStatus;

    /// <summary>
    /// What the toolbar button of the ePlugin Manager shows, as the manager would list it. Called every frame; looks again
    /// every <see cref="ManagerStatusCheckFrames"/> frames, and reads the plugins only when the pending updates, the
    /// enabled plugins or their states changed. While plugins are being changed it keeps the last status.
    /// </summary>
    internal ManagerStatus CurrentManagerStatus()
    {
        var frame = Engine.GetProcessFrames();
        if (frame < _nextManagerStatusCheck || IsChangingPlugins || _recipeUpdateJournal is not null) return _managerStatus;
        _nextManagerStatusCheck = frame + ManagerStatusCheckFrames;
        var pending = PendingUpdates;
        var attempts = _stateStore?.LocalAttempts ?? [];
        var states = _contexts.Where(c => c.Plugin is not null).Select(c => $"{c.Slug}:{c.State}").ToArray();
        // Nothing can need attention or be updated: no need to read the plugins.
        if (pending.Count == 0 && attempts.Count == 0 && !_contexts.Any(c => c.Plugin is not null && c.State == EEditorPluginState.Error))
        {
            _managerStatusKey = null;
            return _managerStatus = ManagerStatus.Normal;
        }
        var key = string.Join("\n", pending.Select(c => $"{c.Slug} {c.NewVersion} {c.SourceUrl}")) + "\n#" +
                  string.Join(",", attempts.Select(a => a.AttemptId)) + "\n#" + string.Join(",", states) + "\n#" +
                  string.Join(",", GetEnabledPluginSlugs().Order(StringComparer.Ordinal));
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

    /// <summary>Reads the plugins again for the next <see cref="CurrentManagerStatus"/>, e.g. after the ePlugin Manager changed update sites.</summary>
    internal void InvalidateManagerStatus() { _managerStatusKey = null; _nextManagerStatusCheck = 0; }

    /// <summary>
    /// Every addon with a plugin.cfg directly below res://addons, enabled or not, plus plugins that only remain as a
    /// failed local attempt.
    /// </summary>
    internal IReadOnlyList<PluginInfo> CollectPlugins()
    {
        RefreshPlainPlugins();
        ReloadUpdateSites();
        var enabled = GetEnabledPluginSlugs();
        var types = PluginTypes.All;
        var framework = _ePluginContext?.GetPluginSlug() ?? "ePlugin";
        var plugins = new List<PluginInfo>();
        using var addons = DirAccess.Open("res://addons");
        foreach (var slug in addons?.GetDirectories() ?? [])
        {
            var directory = $"res://addons/{slug}";
            if (!FileAccess.FileExists(directory + "/plugin.cfg")) continue;
            using var config = new ConfigFile();
            if (config.Load(directory + "/plugin.cfg") != Error.Ok) continue;
            string Value(string key) => config.GetValue("plugin", key, "").AsString().Trim();
            var script = PluginCatalog.ResolveScript(directory, Value("script"));
            var context = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
            var name = Value("name");
            plugins.Add(new PluginInfo(slug, name.Length > 0 ? name : slug,
                PluginCatalog.Classify(script, slug == framework, types), enabled.Contains(slug))
            {
                Version = Value("version"), Author = Value("author"), Description = Value("description"),
                UpdateUrl = Value("update_url") is { Length: > 0 } url ? url : null, UpdateSite = UpdateSiteOf(slug), Known = KnownPluginOf(slug), Script = script,
                DocumentationUrl = Value("documentation_url") is { Length: > 0 } documentation ? documentation : null,
                SourceUrl = Value("source_url") is { Length: > 0 } source ? source : null,
                State = context?.State, Error = context?.ErrorDetail?.Message,
                FailedAttempt = _stateStore?.GetLocal(slug),
                Recipe = context is { IsRecipeCreated: true } ? context.Builder.PluginRecipe : null,
                LocalPackages = LocalIndex.Matching(slug).Count()
            });
        }
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
