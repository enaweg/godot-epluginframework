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
    private const int NativeExtensionCheckFrames = 600;
    private ulong _nextManagerStatusCheck;
    private string? _managerStatusKey;
    private ManagerStatus _managerStatus;
    private ulong _nextNativeExtensionCheck;
    private string _nativeExtensionProblems = "";

    /// <summary>
    /// The <see cref="AttentionItem.Key"/>s of the problems the Needs attention dialog showed in this editor session; the
    /// ePlugin Manager opens it by itself only for others. An assembly reload forgets them.
    /// </summary>
    internal HashSet<string> ShownAttention { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What the toolbar button of the ePlugin Manager shows, as the manager would list it. Called every frame; looks again
    /// every <see cref="ManagerStatusCheckFrames"/> frames, and reads the plugins only when the pending updates, the
    /// enabled plugins, their states or the GDExtensions Godot does not run changed. While plugins are being changed it
    /// keeps the last status.
    /// </summary>
    internal ManagerStatus CurrentManagerStatus()
    {
        var frame = Engine.GetProcessFrames();
        if (frame < _nextManagerStatusCheck || IsChangingPlugins || _recipeUpdateJournal is not null) return _managerStatus;
        _nextManagerStatusCheck = frame + ManagerStatusCheckFrames;
        var pending = PendingUpdates;
        var attempts = _stateStore?.LocalAttempts ?? [];
        var states = _contexts.Where(c => c.Plugin is not null).Select(c => $"{c.Slug}:{c.State}").ToArray();
        var native = NativeExtensionProblems(frame);
        // Nothing can need attention or be updated: no need to read the plugins.
        if (pending.Count == 0 && attempts.Count == 0 && native.Length == 0 && !_contexts.Any(c => c.Plugin is not null && c.State == EEditorPluginState.Error))
        {
            _managerStatusKey = null;
            return _managerStatus = ManagerStatus.Normal;
        }
        var key = string.Join("\n", pending.Select(c => $"{c.Slug} {c.NewVersion} {c.SourceUrl}")) + "\n#" +
                  string.Join(",", attempts.Select(a => a.AttemptId)) + "\n#" + string.Join(",", states) + "\n#" +
                  string.Join(",", GetEnabledPluginSlugs().Order(StringComparer.Ordinal)) + "\n#" + native;
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
    internal void InvalidateManagerStatus() { _managerStatusKey = null; _nextManagerStatusCheck = 0; _nextNativeExtensionCheck = 0; }

    /// <summary>
    /// The <c>.gdextension</c> files below the addons folder that Godot does not run although it should (see
    /// <see cref="NativeExtensionStatus.Problem"/>), joined into one key; empty when there is none. Reading them means
    /// walking every addon folder, so they are read again only every <see cref="NativeExtensionCheckFrames"/> frames.
    /// </summary>
    private string NativeExtensionProblems(ulong frame)
    {
        if (frame < _nextNativeExtensionCheck) return _nativeExtensionProblems;
        _nextNativeExtensionCheck = frame + NativeExtensionCheckFrames;
        try
        {
            using var addons = DirAccess.Open(_host.AddonsDirectory);
            return _nativeExtensionProblems = string.Join(",", (addons?.GetDirectories() ?? [])
                .SelectMany(slug => NativeExtensionStatus.Read(_host.ProjectRoot, slug, _host.IsExtensionLoaded)
                    .Where(e => e.Problem is not null).Select(e => $"{slug}/{e.File}")));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return _nativeExtensionProblems = "";
        }
    }

    /// <summary>
    /// Every addon with a plugin.cfg directly below the addons folder, enabled or not, every GDExtension add-on, plus
    /// plugins that only remain as a failed local attempt.
    /// </summary>
    internal IReadOnlyList<PluginInfo> CollectPlugins()
    {
        RefreshPlainPlugins();
        ReloadUpdateSites();
        var enabled = GetEnabledPluginSlugs();
        var types = PluginTypes.All;
        var framework = _ePluginContext?.GetPluginSlug() ?? "ePlugin";
        var plugins = new List<PluginInfo>();
        using var addons = DirAccess.Open(_host.AddonsDirectory);
        foreach (var slug in addons?.GetDirectories() ?? [])
        {
            var directory = _host.PluginDirectory(slug);
            if (!FileAccess.FileExists(directory + "/plugin.cfg")) continue;
            using var config = new ConfigFile();
            if (config.Load(directory + "/plugin.cfg") != Error.Ok) continue;
            string Value(string key) => config.GetValue("plugin", key, "").AsString().Trim();
            var script = PluginCatalog.ResolveScript(directory, Value("script"));
            var context = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
            var name = Value("name");
            plugins.Add(new PluginInfo(slug, name.Length > 0 ? name : slug,
                PluginKinds.Classify(script, slug == framework, types, PluginKinds.HasNativeExtension(ProjectSettings.GlobalizePath(directory))), enabled.Contains(slug))
            {
                Version = Value("version"), Author = Value("author"), Description = Value("description"),
                UpdateUrl = Value("update_url") is { Length: > 0 } url ? url : null, UpdateSite = UpdateSiteOf(slug), Known = KnownPluginOf(slug), Script = script,
                DocumentationUrl = Value("documentation_url") is { Length: > 0 } documentation ? documentation : null,
                SourceUrl = Value("source_url") is { Length: > 0 } source ? source : null,
                State = context?.State, Error = context?.ErrorDetail?.Message,
                FailedAttempt = _stateStore?.GetLocal(slug),
                Recipe = context is { IsRecipeCreated: true } ? context.Builder.PluginRecipe : null,
                LocalPackages = LocalIndex.Matching(slug).Count(),
                Extensions = NativeExtensionStatus.Read(_host.ProjectRoot, slug, _host.IsExtensionLoaded)
            });
        }
        plugins.AddRange(ExtensionAddonSlugs().Select(ExtensionAddonInfo));
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
